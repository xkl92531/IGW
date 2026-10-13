using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using IGW.Abstractions;

namespace IGW.Core;

public enum ConnectionDiagnosticCategory
{
    NetworkAdapterUnavailable,
    NameResolutionFailure,
    NetworkUnreachable,
    ConnectionRefused,
    ConnectionTimeout,
    ProtocolMismatch,
    AuthenticationRequired,
    AuthenticationFailed,
    SecurityPolicyMismatch,
    PermissionDenied,
    DataAddressMismatch,
    PartialDataFailure,
    RemoteClosed,
    UnknownConnectionFailure,
}

public enum ConnectionDiagnosticScope { Transport, Session, DataAccess }

public sealed record ConnectionDiagnosticDescriptor(
    ConnectionDiagnosticCategory Category,
    string CategoryLabel,
    string Cause,
    string Suggestion,
    string RawError,
    ConnectionDiagnosticScope Scope);

public sealed record ConnectionIncident(
    string Device,
    string Channel,
    string Protocol,
    ConnectionDiagnosticCategory Category,
    string CategoryLabel,
    string Cause,
    string Suggestion,
    string RawError,
    ConnectionDiagnosticScope Scope,
    ConnectionState State,
    DateTimeOffset FirstOccurredAt,
    DateTimeOffset LastOccurredAt,
    DateTimeOffset? RecoveredAt,
    int OccurrenceCount,
    bool Retrying,
    int NextRetryMs)
{
    public string Code => Category.ToString();
    public bool Active => RecoveredAt is null;
}

public sealed record ConnectionIncidentSnapshot(
    IReadOnlyList<ConnectionIncident> Active,
    IReadOnlyList<ConnectionIncident> Recovered);

/// <summary>一个逻辑数组点位的运行质量摘要。失败范围使用 PLC 的实际下界索引。</summary>
public sealed record ArrayTagHealth(
    string TagId,
    string State,
    long Total,
    long Good,
    long Uncertain,
    long Bad,
    long EstimatedBytes,
    int Blocks,
    IReadOnlyList<string> FailedRanges,
    string Message);

/// <summary>把数组元素质量压缩成适合监控中心展示的范围与容量指标。</summary>
public static class ArrayHealthDiagnostics
{
    private const int MaxDisplayedRanges = 16;

    public static ArrayTagHealth Summarize(
        string tagId,
        ArrayRuntimeValue value,
        ArrayDef definition,
        ArrayDriverCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(capabilities);

        long total = value.Count;
        long estimatedBytes = EstimateBytes(definition, total);
        long bytesPerElement = Math.Max(1, (estimatedBytes + Math.Max(1, total) - 1) / Math.Max(1, total));
        long byElements = capabilities.MaxElementsPerRequest > 0
            ? capabilities.MaxElementsPerRequest
            : long.MaxValue;
        long byBytes = capabilities.MaxBytesPerRequest > 0
            ? Math.Max(1, capabilities.MaxBytesPerRequest / bytesPerElement)
            : long.MaxValue;
        long blockCapacity = Math.Max(1, Math.Min(byElements, byBytes));
        int blocks = checked((int)Math.Min(int.MaxValue, (total + blockCapacity - 1) / blockCapacity));
        IReadOnlyList<string> failedRanges = value.BadCount == 0 ? [] : BuildFailedRanges(value);

        string state = value.BadCount == 0 && value.UncertainCount == 0
            ? "Online"
            : value.GoodCount > 0 ? "PartialOnline" : "Unavailable";
        string message = value.BadCount == 0 && value.UncertainCount == 0
            ? ""
            : $"数组 {tagId}：Good {value.GoodCount}/{total}，Uncertain {value.UncertainCount}，Bad {value.BadCount}；失败索引 {(failedRanges.Count == 0 ? "无" : string.Join("、", failedRanges))}";

        return new ArrayTagHealth(
            tagId, state, total, value.GoodCount, value.UncertainCount, value.BadCount,
            estimatedBytes, blocks, failedRanges, message);
    }

    private static IReadOnlyList<string> BuildFailedRanges(ArrayRuntimeValue value)
    {
        var result = new List<string>();
        var quality = value.ElementQuality.Span;
        int cursor = 0;
        int omitted = 0;
        while (cursor < quality.Length)
        {
            if (!quality[cursor].IsBad)
            {
                cursor++;
                continue;
            }

            int start = cursor;
            while (cursor + 1 < quality.Length && quality[cursor + 1].IsBad) cursor++;
            int end = cursor++;
            if (result.Count < MaxDisplayedRanges)
                result.Add(FormatRange(value.Shape, start, end));
            else
                omitted++;
        }
        if (omitted > 0) result.Add($"另 {omitted} 段");
        return result;
    }

    private static string FormatRange(ArrayShape shape, int start, int end)
    {
        string first = string.Join(',', shape.GetIndices(start));
        if (start == end) return $"[{first}]";
        string last = string.Join(',', shape.GetIndices(end));
        return $"[{first}..{last}]";
    }

    private static long EstimateBytes(ArrayDef definition, long total)
    {
        if (definition.ElementStrideBits > 0)
            return checked((total * definition.ElementStrideBits + 7) / 8);
        int stride = definition.ElementStrideBytes > 0
            ? definition.ElementStrideBytes
            : InferElementBytes(definition);
        return checked(total * Math.Max(1, stride));
    }

    private static int InferElementBytes(ArrayDef definition) => definition.ElementType switch
    {
        IgwDataType.Bool or IgwDataType.Byte or IgwDataType.Char => 1,
        IgwDataType.Int16 or IgwDataType.UInt16 or IgwDataType.Bcd => 2,
        IgwDataType.Int32 or IgwDataType.UInt32 or IgwDataType.Float or IgwDataType.LBcd => 4,
        IgwDataType.Int64 or IgwDataType.UInt64 or IgwDataType.Double => 8,
        IgwDataType.String => Math.Max(1, definition.StringLength ?? 254) + 2,
        IgwDataType.WString => checked(Math.Max(1, definition.StringLength ?? 254) * 2 + 4),
        IgwDataType.Struct => Math.Max(1, definition.Struct?.SizeBytes ?? 1),
        _ => 1,
    };
}

public static partial class ConnectionDiagnosticClassifier
{
    private const int RawLimit = 2_000;

    public static ConnectionDiagnosticDescriptor Classify(Exception? error, DeviceConnectionDetail? detail)
    {
        string raw = SanitizeRaw(JoinRaw(detail?.Message, ErrorChain(error)));
        if (TryExplicit(detail?.Code, raw, error, out var explicitResult)) return explicitResult;

        Exception? current = error;
        while (current is not null)
        {
            if (current is SocketException socket) return Socket(socket.SocketErrorCode, raw);
            if (current is TimeoutException or TaskCanceledException)
                return Describe(ConnectionDiagnosticCategory.ConnectionTimeout, raw);
            if (current is AuthenticationException)
                return Describe(ConnectionDiagnosticCategory.SecurityPolicyMismatch, raw);
            if (current is UnauthorizedAccessException)
                return Describe(ConnectionDiagnosticCategory.PermissionDenied, raw);
            current = current.InnerException;
        }

        return FromText(raw);
    }

    private static bool TryExplicit(string? code, string raw, Exception? error, out ConnectionDiagnosticDescriptor result)
    {
        ConnectionDiagnosticCategory? category = code switch
        {
            "NetworkAdapterUnavailable" or "NetworkBindingFailure" => ConnectionDiagnosticCategory.NetworkAdapterUnavailable,
            "AuthenticationRequired" => ConnectionDiagnosticCategory.AuthenticationRequired,
            "AuthenticationFailed" => ConnectionDiagnosticCategory.AuthenticationFailed,
            "PermissionDenied" => ConnectionDiagnosticCategory.PermissionDenied,
            "UnsupportedFirmware" or "SymbolMismatch" => ConnectionDiagnosticCategory.ProtocolMismatch,
            "DbAddressMismatch" => ConnectionDiagnosticCategory.DataAddressMismatch,
            "PartialOnline" => ConnectionDiagnosticCategory.PartialDataFailure,
            // TransportFailure 是驱动层的大类，不能直接等同“网络不可达”；
            // 超时、对端关闭、端口拒绝都必须继续按原始错误细分。
            "TransportFailure" => null,
            _ => null,
        };
        if (code == "NetworkConnectFailure")
        {
            result = FromText(raw);
            if (result.Category == ConnectionDiagnosticCategory.UnknownConnectionFailure)
                result = Describe(ConnectionDiagnosticCategory.NetworkUnreachable, raw);
            return true;
        }
        if (code == "TransportFailure")
        {
            string transportRaw = SanitizeRaw(ErrorChain(error));
            result = FromText(string.IsNullOrWhiteSpace(transportRaw) ? raw : transportRaw);
            if (result.Category == ConnectionDiagnosticCategory.UnknownConnectionFailure)
                result = FromText(raw);
            if (result.Category == ConnectionDiagnosticCategory.UnknownConnectionFailure)
                result = Describe(ConnectionDiagnosticCategory.NetworkUnreachable, raw);
            else if (!string.Equals(result.RawError, raw, StringComparison.Ordinal))
                result = result with { RawError = raw };
            return true;
        }
        if (category is not null)
        {
            result = Describe(category.Value, raw);
            return true;
        }
        result = default!;
        return false;
    }

    private static ConnectionDiagnosticDescriptor Socket(SocketError code, string raw) => code switch
    {
        SocketError.ConnectionRefused => Describe(ConnectionDiagnosticCategory.ConnectionRefused, raw),
        SocketError.TimedOut => Describe(ConnectionDiagnosticCategory.ConnectionTimeout, raw),
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
            Describe(ConnectionDiagnosticCategory.NameResolutionFailure, raw),
        SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.HostUnreachable =>
            Describe(ConnectionDiagnosticCategory.NetworkUnreachable, raw),
        SocketError.ConnectionAborted or SocketError.ConnectionReset or SocketError.Disconnecting or SocketError.Shutdown =>
            Describe(ConnectionDiagnosticCategory.RemoteClosed, raw),
        SocketError.AccessDenied => Describe(ConnectionDiagnosticCategory.PermissionDenied, raw),
        _ => FromText(raw),
    };

    private static ConnectionDiagnosticDescriptor FromText(string raw)
    {
        string text = raw.ToLowerInvariant();
        if (Has(text, "badsecuritychecksfailed", "certificate", "security policy", "securitypolicy", "untrusted", "signing", "encryption", "证书", "安全策略"))
            return Describe(ConnectionDiagnosticCategory.SecurityPolicyMismatch, raw);
        if (Has(text, "authentication required", "anonymous access rejected", "badidentitytokeninvalid", "需要身份验证", "要求身份验证", "匿名连接被拒"))
            return Describe(ConnectionDiagnosticCategory.AuthenticationRequired, raw);
        if (Has(text, "badidentitytokenrejected", "badusersignatureinvalid", "authentication failed", "login failed", "invalid password", "credential rejected", "认证失败", "用户名或密码"))
            return Describe(ConnectionDiagnosticCategory.AuthenticationFailed, raw);
        if (Has(text, "baduseraccessdenied", "access denied", "permission denied", "unauthorized", "protection level", "权限不足", "访问被拒", "保护级别"))
            return Describe(ConnectionDiagnosticCategory.PermissionDenied, raw);
        if (Has(text, "item not available", "badnodeidunknown", "nodeid unknown", "address out of range", "tag not found", "db not found", "db不存在", "db 不存在", "地址越界", "地址不存在"))
            return Describe(ConnectionDiagnosticCategory.DataAddressMismatch, raw);
        if (Has(text, "protocol", "handshake", "rack", "slot", "unit id", "unsupported firmware", "s7 plus", "s7 classic", "型号不匹配", "协议不匹配", "握手失败"))
            return Describe(ConnectionDiagnosticCategory.ProtocolMismatch, raw);
        if (Has(text, "connection refused", "actively refused", "目标计算机积极拒绝", "端口拒绝"))
            return Describe(ConnectionDiagnosticCategory.ConnectionRefused, raw);
        if (Has(text, "timed out", "timeout", "超时"))
            return Describe(ConnectionDiagnosticCategory.ConnectionTimeout, raw);
        if (Has(text, "network is unreachable", "host is unreachable", "no route to host", "network unreachable", "网络不可达", "主机不可达", "无路由"))
            return Describe(ConnectionDiagnosticCategory.NetworkUnreachable, raw);
        if (Has(text, "name or service not known", "no such host", "host not found", "nodename nor servname", "名称解析", "找不到主机"))
            return Describe(ConnectionDiagnosticCategory.NameResolutionFailure, raw);
        if (Has(text, "forcibly closed", "connection reset", "reset by peer", "remote host closed", "end of file", "远程主机强迫关闭", "对端关闭"))
            return Describe(ConnectionDiagnosticCategory.RemoteClosed, raw);
        return Describe(ConnectionDiagnosticCategory.UnknownConnectionFailure, raw);
    }

    private static bool Has(string source, params string[] needles) => needles.Any(source.Contains);

    private static ConnectionDiagnosticDescriptor Describe(ConnectionDiagnosticCategory category, string raw) => category switch
    {
        ConnectionDiagnosticCategory.NetworkAdapterUnavailable => New(category, "网卡不可用", "配置的服务器网卡不存在或没有可用 IPv4 地址。", "重新选择当前已启用且有 IPv4 地址的网卡。", raw, ConnectionDiagnosticScope.Transport),
        ConnectionDiagnosticCategory.NameResolutionFailure => New(category, "名称解析失败", "设备主机名无法解析为 IP 地址。", "检查主机名、DNS 配置，或直接使用正确的 PLC IP 地址。", raw, ConnectionDiagnosticScope.Transport),
        ConnectionDiagnosticCategory.NetworkUnreachable => New(category, "网络不可达", "IGW 到 PLC 没有可用网络路径。", "检查服务器路由、网段、网关、网线/WLAN、VLAN 和 PLC 电源。", raw, ConnectionDiagnosticScope.Transport),
        ConnectionDiagnosticCategory.ConnectionRefused => New(category, "端口拒绝", "PLC 地址可访问，但目标端口拒绝建立连接。", "检查端口、PLC 通信服务是否启用，以及防火墙是否允许该端口。", raw, ConnectionDiagnosticScope.Transport),
        ConnectionDiagnosticCategory.ConnectionTimeout => New(category, "连接超时", "连接或协议握手在限定时间内未完成。", "检查网络丢包、端口、PLC 负载、源网卡和连接超时设置。", raw, ConnectionDiagnosticScope.Transport),
        ConnectionDiagnosticCategory.ProtocolMismatch => New(category, "协议/型号不匹配", "所选协议、PLC 型号或握手参数与设备不一致。", "核对 PLC 型号、Classic/S7 Plus、Rack/Slot、Unit ID 和协议端口。", raw, ConnectionDiagnosticScope.Session),
        ConnectionDiagnosticCategory.AuthenticationRequired => New(category, "需要身份验证", "PLC 拒绝匿名访问并要求登录。", "在设备认证设置中启用非匿名连接并填写 PLC 凭据。", raw, ConnectionDiagnosticScope.Session),
        ConnectionDiagnosticCategory.AuthenticationFailed => New(category, "认证失败", "PLC 拒绝了当前用户名、密码或身份令牌。", "重新输入有效凭据，并确认账户未锁定或过期。", raw, ConnectionDiagnosticScope.Session),
        ConnectionDiagnosticCategory.SecurityPolicyMismatch => New(category, "安全策略不匹配", "证书、信任、安全策略、签名或加密校验失败。", "核对双方安全策略并信任证书；测试环境可明确选择双方都允许的安全模式。", raw, ConnectionDiagnosticScope.Session),
        ConnectionDiagnosticCategory.PermissionDenied => New(category, "权限/保护限制", "连接已到达 PLC，但账户权限或 PLC 保护级别拒绝访问。", "调整 PLC 保护设置或为当前账户授予读取、写入和符号访问权限。", raw, ConnectionDiagnosticScope.Session),
        ConnectionDiagnosticCategory.DataAddressMismatch => New(category, "DB/地址错误", "通信链路可用，但配置的 DB、符号、寄存器或标记地址不存在或越界。", "核对 DB 是否存在、优化属性、地址偏移、长度、符号名和数据类型。", raw, ConnectionDiagnosticScope.DataAccess),
        ConnectionDiagnosticCategory.PartialDataFailure => New(category, "部分数据异常", "设备连接可用，但只有部分点位或数据通道读取失败。", "在 QC 中定位 Bad 点位并检查对应 DB、地址、类型和权限；Good 点位仍可使用。", raw, ConnectionDiagnosticScope.DataAccess),
        ConnectionDiagnosticCategory.RemoteClosed => New(category, "服务端断开", "PLC 或中间网络设备主动关闭了已建立的连接。", "检查 PLC 连接数、会话超时、网络稳定性和是否有其他客户端抢占连接。", raw, ConnectionDiagnosticScope.Transport),
        _ => New(ConnectionDiagnosticCategory.UnknownConnectionFailure, "未知连接异常", "驱动返回了尚未归类的连接错误。", "查看原始错误并核对网络、端口、协议、安全和 PLC 配置。", raw, ConnectionDiagnosticScope.Transport),
    };

    private static ConnectionDiagnosticDescriptor New(
        ConnectionDiagnosticCategory category, string label, string cause, string suggestion, string raw, ConnectionDiagnosticScope scope) =>
        new(category, label, cause, suggestion, string.IsNullOrWhiteSpace(raw) ? label : raw, scope);

    private static string ErrorChain(Exception? error)
    {
        if (error is null) return "";
        var parts = new List<string>();
        for (Exception? current = error; current is not null && parts.Count < 6; current = current.InnerException)
            if (!string.IsNullOrWhiteSpace(current.Message) && !parts.Contains(current.Message, StringComparer.Ordinal))
                parts.Add(current.Message);
        return string.Join(" | ", parts);
    }

    private static string JoinRaw(string? detail, string? error) =>
        string.Join(" | ", new[] { detail?.Trim(), error?.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));

    private static string SanitizeRaw(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string value = Whitespace().Replace(raw, " ").Trim();
        value = UriCredentials().Replace(value, "***:***@");
        value = NamedSecret().Replace(value, "$1=***");
        value = Authorization().Replace(value, "Authorization: ***");
        return value.Length <= RawLimit ? value : value[..RawLimit];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<=://)[^/@\s:]+:[^/@\s]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UriCredentials();

    [GeneratedRegex(@"\b(password|pwd|token|secret)\s*[:=]\s*[^\s,;|]+", RegexOptions.IgnoreCase)]
    private static partial Regex NamedSecret();

    [GeneratedRegex(@"\bauthorization\s*:\s*(?:(?:basic|bearer)\s+)?[^\s,;|]+", RegexOptions.IgnoreCase)]
    private static partial Regex Authorization();
}

public sealed class ConnectionIncidentRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ConnectionIncident> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConnectionIncident> _recovered = [];
    private readonly int _recoveredCapacity;

    public ConnectionIncidentRegistry(int recoveredCapacity = 200) =>
        _recoveredCapacity = Math.Max(1, recoveredCapacity);

    public ConnectionIncident Report(
        string device,
        string channel,
        string protocol,
        ConnectionDiagnosticDescriptor descriptor,
        ConnectionState state,
        bool retrying,
        int nextRetryMs,
        DateTimeOffset? occurredAt = null)
    {
        var now = occurredAt ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_active.TryGetValue(device, out var current) && current.Category == descriptor.Category)
            {
                var updated = current with
                {
                    CategoryLabel = descriptor.CategoryLabel,
                    Cause = descriptor.Cause,
                    Suggestion = descriptor.Suggestion,
                    RawError = descriptor.RawError,
                    Scope = descriptor.Scope,
                    State = state,
                    LastOccurredAt = now,
                    OccurrenceCount = current.OccurrenceCount + 1,
                    Retrying = retrying,
                    NextRetryMs = Math.Max(0, nextRetryMs),
                };
                _active[device] = updated;
                return updated;
            }

            if (current is not null) AddRecovered(current with { RecoveredAt = now });
            var created = new ConnectionIncident(
                device, channel, protocol, descriptor.Category, descriptor.CategoryLabel,
                descriptor.Cause, descriptor.Suggestion, descriptor.RawError, descriptor.Scope, state,
                now, now, null, 1, retrying, Math.Max(0, nextRetryMs));
            _active[device] = created;
            return created;
        }
    }

    public ConnectionIncident? Recover(string device, DateTimeOffset? recoveredAt = null)
    {
        lock (_gate)
        {
            if (!_active.Remove(device, out var current)) return null;
            var recovered = current with { RecoveredAt = recoveredAt ?? DateTimeOffset.UtcNow, Retrying = false, NextRetryMs = 0 };
            AddRecovered(recovered);
            return recovered;
        }
    }

    public ConnectionIncident? Current(string device)
    {
        lock (_gate) return _active.GetValueOrDefault(device);
    }

    public ConnectionIncidentSnapshot Snapshot()
    {
        lock (_gate)
            return new ConnectionIncidentSnapshot(
                _active.Values.OrderBy(item => item.FirstOccurredAt).ToArray(),
                _recovered.ToArray());
    }

    private void AddRecovered(ConnectionIncident incident)
    {
        _recovered.Insert(0, incident);
        if (_recovered.Count > _recoveredCapacity)
            _recovered.RemoveRange(_recoveredCapacity, _recovered.Count - _recoveredCapacity);
    }
}
