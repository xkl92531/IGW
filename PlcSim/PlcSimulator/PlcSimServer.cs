using System.Net;
using System.Net.Sockets;
using System.Text;
using HslCommunication;
using HslCommunication.Core.Net;
using HslCommunication.Profinet.Siemens;

namespace PlcSimulator
{
    /// <summary>
    /// 使用可取消的 Accept 循环替代 HslCommunication 11.2.1 自带的 BeginAccept 回调。
    /// 旧回调在 ServerClose 后仍会对已关闭的监听 Socket 连续重试，第三次会在后台线程
    /// 抛出未处理异常并终止整个 WPF 进程。
    /// </summary>
    internal sealed class StoppableSiemensS7Server : SiemensS7Server
    {
        private readonly object _lifecycleLock = new object();
        private CancellationTokenSource _acceptCancellation;
        private Task _acceptTask = Task.CompletedTask;

        public override void ServerStart(int port)
        {
            lock (_lifecycleLock)
            {
                if (IsStarted) return;

                Socket listener = null;
                try
                {
                    StartInitialization();
                    listener = EnableIPv6
                        ? new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
                        : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    listener.Bind(new IPEndPoint(EnableIPv6 ? IPAddress.IPv6Any : IPAddress.Any, port));
                    listener.Listen(500);

                    CoreSocket = listener;
                    Port = port;
                    IsStarted = true;
                    _acceptCancellation = new CancellationTokenSource();
                    _acceptTask = AcceptLoopAsync(listener, _acceptCancellation.Token);
                }
                catch
                {
                    IsStarted = false;
                    try { listener?.Close(); } catch { }
                    CoreSocket = null;
                    throw;
                }
            }
        }

        public override void ServerClose()
        {
            CancellationTokenSource cancellation;
            Task acceptTask;
            Socket listener;

            lock (_lifecycleLock)
            {
                if (!IsStarted && CoreSocket == null) return;

                // 先阻止新连接并关闭监听，再关闭现有会话，避免客户端在关闭窗口内重连。
                IsStarted = false;
                cancellation = _acceptCancellation;
                acceptTask = _acceptTask;
                listener = CoreSocket;
                CoreSocket = null;
                _acceptCancellation = null;
                _acceptTask = Task.CompletedTask;
            }

            try { cancellation?.Cancel(); } catch { }
            try { listener?.Close(); } catch { }
            try { acceptTask?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }

            try { CloseAction(); }
            finally { cancellation?.Dispose(); }
        }

        private async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket client = null;
                try
                {
                    client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                    if (!IsStarted || cancellationToken.IsCancellationRequested)
                    {
                        client.Close();
                        continue;
                    }

                    var endPoint = client.RemoteEndPoint as IPEndPoint;
                    if (endPoint == null)
                    {
                        client.Close();
                        continue;
                    }

                    var check = SocketAcceptExtraCheck(client, endPoint);
                    if (!check.IsSuccess)
                    {
                        client.Close();
                        continue;
                    }

                    var acceptedClient = client;
                    client = null; // 会话所有权移交给 HSL 的协议处理逻辑
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { ThreadPoolLogin(acceptedClient, endPoint); }
                        catch { try { acceptedClient.Close(); } catch { } }
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || !IsStarted)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested || !IsStarted)
                {
                    break;
                }
                finally
                {
                    try { client?.Close(); } catch { }
                }
            }
        }
    }

    /// <summary>通讯日志条目</summary>
    public class TrafficEntry
    {
        public DateTime Time { get; set; }
        public string Direction { get; set; }
        public string Text { get; set; }
        public override string ToString() => $"{Time:HH:mm:ss.fff} [{Direction}] {Text}";
    }

    internal enum TrafficKind
    {
        None,
        Read,
        Write,
        Connect,
        Other
    }

    /// <summary>
    /// 基于 HslCommunication SiemensS7Server 的 S7 协议模拟 PLC 服务器。
    /// Kepware 使用 "Siemens TCP/IP Ethernet" 驱动，端口默认 102，Rack 0 Slot 2。
    /// </summary>
    public class PlcSimServer
    {
        private StoppableSiemensS7Server _server;
        private readonly object _lock = new object();
        private volatile bool _logReads;
        private volatile bool _logWrites = true;
        private volatile bool _logConnections = true;

        public bool IsRunning => _server != null && _server.IsStarted;
        public int OnlineCount => _server?.OnlineCount ?? 0;
        public bool LogReads { get => _logReads; set => _logReads = value; }
        public bool LogWrites { get => _logWrites; set => _logWrites = value; }
        public bool LogConnections { get => _logConnections; set => _logConnections = value; }

        public bool EnableWrite
        {
            get => _server?.EnableWrite ?? true;
            set { if (_server != null) _server.EnableWrite = value; }
        }

        /// <summary>收到客户端请求时触发（后台线程），参数为解析后的日志文本</summary>
        public event Action<TrafficEntry> TrafficReceived;

        public string Start(int port)
        {
            lock (_lock)
            {
                if (IsRunning) return "服务器已在运行";
                try
                {
                    _server = new StoppableSiemensS7Server();
                    _server.OnDataReceived += OnDataReceived;
                    _server.ServerStart(port);

                    foreach (var block in DbLayout.Blocks)
                    {
                        // 确保 DB 存在且容量足够：直接写满整个 DB 区间完成初始化
                        _server.AddDbBlock(block.DbNumber);
                        var r = _server.Write($"DB{block.DbNumber}.0", new byte[block.Size]);
                        if (!r.IsSuccess)
                            return $"初始化 DB{block.DbNumber} 失败：{r.Message}";
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    _server?.ServerClose();
                    _server = null;
                    return "启动失败：" + ex.Message;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_server == null) return;
                try { _server.ServerClose(); } catch { }
                _server = null;
            }
        }

        // ---------------- 内存读写（大端，S7 字节序） ----------------

        public bool ReadBool(FieldDef f)
        {
            var r = _server.Read(f.HslAddress, 1);
            if (!r.IsSuccess) return false;
            return (r.Content[0] & (1 << f.BitOffset)) != 0;
        }

        public void WriteBool(FieldDef f, bool value)
        {
            var r = _server.Read(f.HslAddress, 1);
            byte b = r.IsSuccess ? r.Content[0] : (byte)0;
            if (value) b |= (byte)(1 << f.BitOffset); else b &= (byte)~(1 << f.BitOffset);
            _server.Write(f.HslAddress, new[] { b });
        }

        public short ReadInt(FieldDef f)
        {
            var r = _server.Read(f.HslAddress, 2);
            if (!r.IsSuccess) return 0;
            return (short)((r.Content[0] << 8) | r.Content[1]);
        }

        public void WriteInt(FieldDef f, short value)
        {
            _server.Write(f.HslAddress, new[] { (byte)(value >> 8), (byte)value });
        }

        public float ReadReal(FieldDef f)
        {
            var r = _server.Read(f.HslAddress, 4);
            if (!r.IsSuccess) return 0f;
            var bytes = new[] { r.Content[3], r.Content[2], r.Content[1], r.Content[0] };
            return BitConverter.ToSingle(bytes, 0);
        }

        public void WriteReal(FieldDef f, float value)
        {
            var le = BitConverter.GetBytes(value);
            _server.Write(f.HslAddress, new[] { le[3], le[2], le[1], le[0] });
        }

        /// <summary>读取 S7 String（[maxLen][curLen][chars]）</summary>
        public string ReadString(FieldDef f)
        {
            var r = _server.Read(f.HslAddress, (ushort)(f.StringMaxLen + 2));
            if (!r.IsSuccess || r.Content.Length < 2) return "";
            int len = Math.Min(r.Content[1], f.StringMaxLen);
            return Encoding.ASCII.GetString(r.Content, 2, Math.Max(0, Math.Min(len, r.Content.Length - 2)));
        }

        public void WriteString(FieldDef f, string value)
        {
            value ??= "";
            if (value.Length > f.StringMaxLen) value = value.Substring(0, f.StringMaxLen);
            var bytes = Encoding.ASCII.GetBytes(value);
            var buf = new byte[bytes.Length + 2];
            buf[0] = (byte)f.StringMaxLen;
            buf[1] = (byte)bytes.Length;
            Buffer.BlockCopy(bytes, 0, buf, 2, bytes.Length);
            _server.Write(f.HslAddress, buf);
        }

        public string ReadFieldValue(FieldDef f)
        {
            switch (f.Type)
            {
                case S7DataType.Bool: return ReadBool(f) ? "TRUE" : "FALSE";
                case S7DataType.Int: return ReadInt(f).ToString();
                case S7DataType.Real: return ReadReal(f).ToString("0.######");
                default: return ReadString(f);
            }
        }

        /// <summary>按类型把文本写入字段，返回错误信息或 null</summary>
        public string WriteFieldValue(FieldDef f, string text)
        {
            try
            {
                switch (f.Type)
                {
                    case S7DataType.Bool:
                        WriteBool(f, text != null && (text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || text.Trim() == "1"));
                        break;
                    case S7DataType.Int:
                        WriteInt(f, short.Parse(text.Trim()));
                        break;
                    case S7DataType.Real:
                        WriteReal(f, float.Parse(text.Trim()));
                        break;
                    default:
                        WriteString(f, text);
                        break;
                }
                return null;
            }
            catch (Exception ex)
            {
                return "写入失败：" + ex.Message;
            }
        }

        // ---------------- 客户端报文解析（仅作日志展示） ----------------

        private void OnDataReceived(object sender, object source, byte[] data)
        {
            try
            {
                var handler = TrafficReceived;
                if (handler == null) return;

                TrafficKind kind = ClassifyTraffic(data);
                if (kind == TrafficKind.None) return;
                if (kind == TrafficKind.Read && !_logReads) return;
                if (kind == TrafficKind.Write && !_logWrites) return;
                if (kind == TrafficKind.Connect && !_logConnections) return;

                var entry = ParseS7Packet(source as AppSession, data);
                if (entry != null) handler(entry);
            }
            catch
            {
                // 日志解析失败不影响服务
            }
        }

        internal static TrafficKind ClassifyTraffic(byte[] data)
        {
            if (data == null || data.Length < 6 || data[0] != 0x03) return TrafficKind.None;
            int cotpPduType = data[5];
            if (cotpPduType == 0xE0) return TrafficKind.Connect;
            if (cotpPduType != 0xF0 || data.Length < 19 || data[7] != 0x32 || data[8] != 0x01)
                return TrafficKind.None;

            int function = (data[17] << 8) | data[18];
            return function switch
            {
                0x04 => TrafficKind.Read,
                0x05 => TrafficKind.Write,
                _ => TrafficKind.Other
            };
        }

        private static TrafficEntry ParseS7Packet(AppSession session, byte[] data)
        {
            if (data == null || data.Length < 10) return null;
            // TPKT(4) + COTP DT(3) + S7 Header(10...)
            if (data[0] != 0x03) return null;
            int cotpPduType = data[5];
            if (cotpPduType == 0xF0)   // COTP DT Data
            {
                if (data.Length < 19 || data[7] != 0x32) return null;
                int msgType = data[8];                    // 1=Job 2=Ack 3=Ack_Data 7=Userdata
                int function = (data[17] << 8) | data[18];
                if (msgType == 1)
                {
                    switch (function)
                    {
                        case 0x04: return ParseReadVar(session, data);
                        case 0x05: return ParseWriteVar(session, data);
                        case 0x00: return new TrafficEntry { Time = DateTime.Now, Direction = "←", Text = "CPU 功能/设置请求" };
                        case 0x07: return new TrafficEntry { Time = DateTime.Now, Direction = "←", Text = "读取 SZL(CPU 信息)" };
                        default: return new TrafficEntry { Time = DateTime.Now, Direction = "←", Text = $"S7 Job 功能码 0x{function:X2}" };
                    }
                }
                return null; // 响应报文不记录
            }
            if (cotpPduType == 0xE0)
                return new TrafficEntry { Time = DateTime.Now, Direction = "←", Text = $"新连接请求 COTP CR，来自 {session?.IpEndPoint}" };
            return null;
        }

        private static TrafficEntry ParseReadVar(AppSession session, byte[] data)
        {
            int itemCount = data[18];
            var sb = new StringBuilder("读变量: ");
            for (int i = 0; i < Math.Min(itemCount, 4); i++)
            {
                int o = 19 + i * 12;
                if (o + 11 >= data.Length) break;
                int count = (data[o + 4] << 8) | data[o + 5];
                int db = (data[o + 6] << 8) | data[o + 7];
                int area = data[o + 8];
                int bitAddr = (data[o + 9] << 16) | (data[o + 10] << 8) | data[o + 11];
                int byteAddr = bitAddr >> 3, bit = bitAddr & 7;
                string areaName = AreaName(area, db);
                sb.Append(i > 0 ? " | " : "").Append($"{areaName}.{byteAddr}.{bit}×{count}");
            }
            return new TrafficEntry { Time = DateTime.Now, Direction = "←", Text = sb.ToString() };
        }

        private static TrafficEntry ParseWriteVar(AppSession session, byte[] data)
        {
            int itemCount = data[18];
            var sb = new StringBuilder("写变量: ");
            int dataOffset = 19 + itemCount * 12;
            for (int i = 0; i < Math.Min(itemCount, 4); i++)
            {
                int o = 19 + i * 12;
                if (o + 11 >= data.Length) break;
                int db = (data[o + 6] << 8) | data[o + 7];
                int area = data[o + 8];
                int bitAddr = (data[o + 9] << 16) | (data[o + 10] << 8) | data[o + 11];
                int byteAddr = bitAddr >> 3;
                string areaName = AreaName(area, db);
                string len = "";
                if (dataOffset + 4 <= data.Length)
                {
                    int transportSize = data[dataOffset];
                    int bitLen = (data[dataOffset + 2] << 8) | data[dataOffset + 3];
                    int byteLen = transportSize == 0x03 || transportSize == 0x04 ? bitLen / 8 : bitLen;
                    len = $"({byteLen}字节)";
                    dataOffset += 4 + byteLen;
                }
                sb.Append(i > 0 ? " | " : "").Append($"{areaName}.{byteAddr}{len}");
            }
            return new TrafficEntry { Time = DateTime.Now, Direction = "←", Text = sb.ToString() };
        }

        private static string AreaName(int area, int db)
        {
            switch (area)
            {
                case 0x84: return $"DB{db}";
                case 0x81: return "I";
                case 0x82: return "Q";
                case 0x83: return "M";
                default: return $"Area0x{area:X2}";
            }
        }
    }
}
