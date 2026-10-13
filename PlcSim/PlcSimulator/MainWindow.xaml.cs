using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PlcSimulator.Core;
using PlcSimulator.Import;
using PlcSimulator.Protocols;

namespace PlcSimulator
{
    /// <summary>界面行：一个字段点位</summary>
    public class FieldRow : INotifyPropertyChanged
    {
        public FieldDef Def { get; }
        public string Name => Def.Name;
        public string Address => Def.AddressText;
        public string Type => Def.TypeText;
        public string Comment => Def.Comment;

        private string _liveValue = "";
        public string LiveValue
        {
            get => _liveValue;
            set { if (_liveValue != value) { _liveValue = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LiveValue))); } }
        }

        private string _editValue = "";
        public string EditValue
        {
            get => _editValue;
            set { if (_editValue != value) { _editValue = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditValue))); } }
        }

        public FieldRow(FieldDef def) { Def = def; }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class SimulationTagRow : INotifyPropertyChanged
    {
        public SimulationTagRow(SimulationTag tag, SimulationValueStore store) { Tag = tag; Store = store; Refresh(); }
        public SimulationTag Tag { get; }
        public SimulationValueStore Store { get; }
        public string Name => Tag.Name;
        public string Address => Tag.ProtocolAddress;
        public string Type => Tag.IsArray ? $"{Tag.DataType}[{string.Join(",", Tag.Dimensions)}]" : Tag.DataType.ToString();
        public string Comment => Tag.Description;
        private string _liveValue = string.Empty;
        private long _liveVersion = -1;
        public string LiveValue { get => _liveValue; private set { if (_liveValue != value) { _liveValue = value; PropertyChanged?.Invoke(this, new(nameof(LiveValue))); } } }
        public string EditValue { get; set; } = string.Empty;
        public void Refresh()
        {
            if (Store.ReadVersion(Tag.Id) == _liveVersion) return;
            var current = Store.Read(Tag.Id);
            LiveValue = SimulationValueText.Format(current.Value); _liveVersion = current.Version;
        }
        public void Write() { Store.Write(Tag.Id, SimulationValueText.Parse(Tag, EditValue), SimulationWriteSource.UserInterface); Refresh(); }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class SimulationGroupNode
    {
        public SimulationGroupNode(SimulationGroup group) { Group = group; Children = group.Groups.Select(x => new SimulationGroupNode(x)).ToList(); }
        public SimulationGroup Group { get; }
        public IReadOnlyList<SimulationGroupNode> Children { get; }
        public string Header => $"{Group.Name} ({Group.Tags.Count})";
    }

    public partial class MainWindow : Window
    {
        private readonly PlcSimServer _server = new PlcSimServer();
        private readonly Dictionary<(int, string), FieldRow> _rows = new Dictionary<(int, string), FieldRow>();
        private readonly Dictionary<int, IReadOnlyList<FieldRow>> _rowsByDb = new();
        private readonly TrafficLogBuffer _trafficLogs = new(2000);
        private readonly Queue<string> _visibleLogLines = new();
        private readonly DispatcherTimer _valueTimer;
        private readonly DispatcherTimer _logTimer;
        private readonly AsyncShutdownCoordinator _shutdownCoordinator = new();
        private volatile bool _logRead;
        private volatile bool _logWrite = true;
        private volatile bool _logConnect = true;
        private SimulationProject _simulationProject;
        private SimulationValueStore _simulationStore;
        private IReadOnlyList<SimulationTagRow> _importedRows = Array.Empty<SimulationTagRow>();
        private OpcUaSimulationServer _opcUaServer;
        private readonly List<SiemensClassicSimulationServer> _classicServers = new();
        private readonly List<SiemensPlusSimulationServer> _plusServers = new();
        private readonly List<OmronCipSimulationServer> _cipServers = new();
        private readonly List<OmronFinsSimulationServer> _finsServers = new();
        private DataGrid _activeTagGrid;

        private bool IsSiemensRunning => _server.IsRunning || _classicServers.Any(x => x.IsRunning) || _plusServers.Any(x => x.IsRunning);
        private int SiemensOnlineCount => _server.OnlineCount + _classicServers.Sum(x => x.OnlineCount) + _plusServers.Sum(x => x.OnlineCount);

        public MainWindow()
        {
            InitializeComponent();
            BuildTabs();
            InitializeDefaultProject();

            _server.TrafficReceived += OnTraffic;
            _valueTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _valueTimer.Tick += (_, _) => RefreshLiveValues();
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _logTimer.Tick += (_, _) => FlushTrafficLogs();
            Closing += MainWindow_Closing;
            Loaded += OnLoaded;
        }

        private async void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_shutdownCoordinator.IsCompleted) return;

            e.Cancel = true;
            if (_shutdownCoordinator.IsStarted) return;
            IsEnabled = false;

            try
            {
                await _shutdownCoordinator.RunAsync(async () =>
                {
                    StopUiTimers();
                    _trafficLogs.Clear();
                    _server.Stop();
                    await StopImportedServersAsync();
                });
            }
            finally
            {
                Application.Current.Shutdown();
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppendLog("模拟器已就绪。可分别启动 S7、OPC UA、Omron CIP/FINS，或点击“启动全部协议”。");
            await LoadCommandLineDbProjectAsync();
        }

        private async Task<bool> LoadCommandLineDbProjectAsync()
        {
            string jsonPath = Environment.GetCommandLineArgs().Skip(1)
                .FirstOrDefault(path => File.Exists(path) && string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase));
            if (jsonPath != null)
            {
                try
                {
                    var imported = KepJsonSimulationImporter.ImportFile(jsonPath);
                    await ApplyImportedProjectAsync(imported.Project);
                    AppendLog($"命令行 JSON 项目已载入：{imported.Project.Devices.Count} 台设备、{imported.Project.EnumerateTags().Count()} 点");
                    foreach (string warning in imported.Warnings.Take(30)) AppendLog("导入警告：" + warning);
                    return await StartSiemensAsync(true);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "启动 JSON 项目失败", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
            }
            string[] paths = Environment.GetCommandLineArgs()
                .Skip(1)
                .Where(File.Exists)
                .Where(path => Path.GetExtension(path) is ".db" or ".scl" or ".txt")
                .ToArray();
            if (paths.Length == 0) return false;
            try
            {
                var imported = SiemensDbSimulationImporter.ImportFiles(paths);
                await ApplyImportedProjectAsync(imported.Project);
                AppendLog($"命令行 DB 项目已载入：{paths.Length} 个 DB、{imported.Project.EnumerateTags().Count()} 点，自动识别={imported.Project.Devices.Single().SiemensProfile}");
                return await StartSiemensAsync(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "启动 DB 项目失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // ---------------- 界面构建 ----------------

        private void BuildTabs()
        {
            foreach (var block in DbLayout.Blocks)
            {
                var tab = new TabItem { Header = block.Title, Tag = block.DbNumber };
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                // 场景按钮行
                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                foreach (var (text, action) in GetScenarios(block.DbNumber))
                {
                    var btn = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) };
                    btn.Click += (_, _) => action();
                    btnPanel.Children.Add(btn);
                }
                Grid.SetRow(btnPanel, 0);
                grid.Children.Add(btnPanel);

                // 点位表格
                var dg = new DataGrid
                {
                    AutoGenerateColumns = false,
                    CanUserAddRows = false,
                    IsReadOnly = false,
                    SelectionMode = DataGridSelectionMode.Single,
                    HeadersVisibility = DataGridHeadersVisibility.Column,
                    GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Visible
                };
                dg.Columns.Add(new DataGridTextColumn { Header = "字段", Binding = new System.Windows.Data.Binding("Name") { Mode = System.Windows.Data.BindingMode.OneWay }, Width = 175, IsReadOnly = true });
                dg.Columns.Add(new DataGridTextColumn { Header = "地址", Binding = new System.Windows.Data.Binding("Address") { Mode = System.Windows.Data.BindingMode.OneWay }, Width = 185, IsReadOnly = true });
                dg.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new System.Windows.Data.Binding("Type") { Mode = System.Windows.Data.BindingMode.OneWay }, Width = 75, IsReadOnly = true });
                dg.Columns.Add(new DataGridTextColumn { Header = "当前值", Binding = new System.Windows.Data.Binding("LiveValue") { Mode = System.Windows.Data.BindingMode.OneWay }, Width = 90, IsReadOnly = true });

                var editCol = new DataGridTemplateColumn { Header = "写入值", Width = 125 };
                var editFactory = new FrameworkElementFactory(typeof(TextBox));
                editFactory.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("EditValue") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
                editFactory.SetValue(TextBox.PaddingProperty, new Thickness(2));
                editCol.CellTemplate = new DataTemplate { VisualTree = editFactory };
                dg.Columns.Add(editCol);

                var applyCol = new DataGridTemplateColumn { Header = "", Width = 54 };
                var applyFactory = new FrameworkElementFactory(typeof(Button));
                applyFactory.SetValue(Button.ContentProperty, "写入");
                applyFactory.SetValue(Button.PaddingProperty, new Thickness(4, 1, 4, 1));
                applyFactory.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnApplyClick));
                applyCol.CellTemplate = new DataTemplate { VisualTree = applyFactory };
                dg.Columns.Add(applyCol);

                dg.Columns.Add(new DataGridTextColumn { Header = "注释", Binding = new System.Windows.Data.Binding("Comment") { Mode = System.Windows.Data.BindingMode.OneWay }, Width = 300, IsReadOnly = true });

                var rows = new ObservableCollection<FieldRow>(
                    DbLayout.Fields.Where(f => f.DbNumber == block.DbNumber).Select(f => new FieldRow(f)));
                foreach (var r in rows) _rows[(block.DbNumber, r.Def.Name)] = r;
                _rowsByDb[block.DbNumber] = rows.ToList();
                dg.ItemsSource = rows;

                Grid.SetRow(dg, 1);
                grid.Children.Add(dg);
                tab.Content = grid;
                TabDbs.Items.Add(tab);
            }
        }

        private void InitializeDefaultProject()
        {
            var project = new SimulationProject("内置西门子示例");
            var device = project.AddDevice("S7Sim");
            device.SourceChannelName = "Siemens S7";
            device.SiemensProfile = SiemensSimulationProfile.Classic;
            device.ProtocolEvidence = "内置非优化 DB，Classic 绝对地址";
            device.AddEndpoint(SimulationProtocol.SiemensS7, 102, 102);
            foreach (var block in DbLayout.Blocks)
            {
                var group = device.AddGroup(block.Title);
                foreach (var field in DbLayout.Fields.Where(x => x.DbNumber == block.DbNumber))
                {
                    var type = field.Type switch
                    {
                        S7DataType.Bool => SimulationDataType.Boolean,
                        S7DataType.Int => SimulationDataType.Int16,
                        S7DataType.Real => SimulationDataType.Float32,
                        _ => SimulationDataType.String,
                    };
                    object initial = type switch
                    {
                        SimulationDataType.Boolean => false,
                        SimulationDataType.Int16 => (short)0,
                        SimulationDataType.Float32 => 0f,
                        _ => string.Empty,
                    };
                    var tag = group.AddTag(field.Name, type, initial);
                    tag.ProtocolAddress = field.AddressText;
                    tag.ClassicAddress = field.AddressText;
                    tag.Description = field.Comment;
                }
            }
            _simulationProject = project;
            _simulationStore = new SimulationValueStore(project);
            BuildDeviceTree();
            ProjectSummaryText.Text = $"{project.Name} · {project.Devices.Count} 台设备 · {project.EnumerateTags().Count()} 点";
            UpdateDetectedSiemensProfile();
        }

        private void BuildDeviceTree()
        {
            DeviceTree.Items.Clear();
            if (_simulationProject == null) return;
            foreach (var device in _simulationProject.Devices)
            {
                var root = new TreeViewItem
                {
                    Header = $"{device.Name}  ·  {device.EnumerateTags().Count()} 点",
                    Tag = device,
                    IsExpanded = true,
                };
                root.ContextMenu = CreateDeleteContextMenu(root);
                foreach (var group in device.Groups) root.Items.Add(CreateGroupTreeItem(group));
                DeviceTree.Items.Add(root);
            }
        }

        private static TreeViewItem CreateGroupTreeItem(SimulationGroup group)
        {
            var item = new TreeViewItem { Header = $"{group.Name}  ({group.EnumerateTags().Count()})", Tag = group };
            item.ContextMenu = CreateDeleteContextMenu(item);
            foreach (var child in group.Groups) item.Items.Add(CreateGroupTreeItem(child));
            return item;
        }

        private static ContextMenu CreateDeleteContextMenu(TreeViewItem owner)
        {
            var menu = new ContextMenu();
            var delete = new MenuItem { Header = "删除" };
            delete.Click += async (_, _) =>
            {
                owner.IsSelected = true;
                if (Window.GetWindow(owner) is MainWindow window) await window.DeleteSelectedTreeNodeAsync();
            };
            menu.Items.Add(delete);
            return menu;
        }

        private void BuildProjectWorkspace()
        {
            BuildDeviceTree();
            TabDbs.Items.Clear();
            _rows.Clear();
            _rowsByDb.Clear();
            _activeTagGrid = CreateImportedTagGrid();
            _importedRows = _simulationProject.EnumerateTags().Select(x => new SimulationTagRow(x, _simulationStore)).ToList();
            _activeTagGrid.ItemsSource = _importedRows;
            TabDbs.Items.Add(new TabItem { Header = "全部点位", Content = _activeTagGrid });
            TabDbs.SelectedIndex = 0;
            SelectionTitleText.Text = "全部点位";
            SelectionSummaryText.Text = $"{_importedRows.Count} 点 · 虚拟化显示 · 所有协议共享当前值";
            ProjectSummaryText.Text = $"{_simulationProject.Name} · {_simulationProject.Devices.Count} 台设备 · {_importedRows.Count} 点";
            UpdateDetectedSiemensProfile();
        }

        private void UpdateDetectedSiemensProfile()
        {
            if (DetectedSiemensProfileText == null || DetectedSiemensEvidenceText == null || _simulationProject == null) return;
            var devices = _simulationProject.Devices.Where(x => x.SiemensProfile != SiemensSimulationProfile.None).ToList();
            if (devices.Count == 0)
            {
                DetectedSiemensProfileText.Text = "自动识别：当前项目无 Siemens 设备";
                DetectedSiemensEvidenceText.Text = "导入 Siemens DB 或包含 Siemens 设备的 KEP JSON 后自动判定协议";
                RefreshSiemensEndpoints();
                return;
            }

            var profiles = devices.Select(x => x.SiemensProfile).Distinct().ToList();
            string profile = profiles.Count > 1 || profiles.Contains(SiemensSimulationProfile.Hybrid)
                ? "混合（Classic + S7 Plus）"
                : profiles[0] switch
                {
                    SiemensSimulationProfile.Classic => "Classic S7",
                    SiemensSimulationProfile.Plus => "S7 Plus",
                    _ => "混合（Classic + S7 Plus）",
                };
            DetectedSiemensProfileText.Text = $"自动识别：{profile} · {devices.Count} 台设备";
            DetectedSiemensEvidenceText.Text = string.Join("；", devices.Take(3).Select(x => $"{x.Name}：{x.ProtocolEvidence}"))
                + (devices.Count > 3 ? $"；另 {devices.Count - 3} 台" : string.Empty);

            var firstEndpoint = devices.SelectMany(x => x.Endpoints)
                .FirstOrDefault(x => x.Enabled && x.Protocol is SimulationProtocol.SiemensS7 or SimulationProtocol.SiemensS7Plus);
            if (firstEndpoint != null) PortBox.Text = firstEndpoint.ListenPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RefreshSiemensEndpoints();
        }

        private void PortBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshSiemensEndpoints();

        private void RefreshSiemensEndpoints()
        {
            if (_simulationProject == null || SiemensEndpointMapText == null || SiemensPortLabelText == null || PortBox == null || BtnStart == null) return;
            var endpoints = _simulationProject.Devices.SelectMany(device => device.Endpoints
                .Where(endpoint => endpoint.Enabled && endpoint.Protocol is SimulationProtocol.SiemensS7 or SimulationProtocol.SiemensS7Plus)
                .Select(endpoint => (Device: device, Endpoint: endpoint))).ToList();
            bool multiple = endpoints.Count > 1;
            PortBox.Visibility = endpoints.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
            PortBox.IsEnabled = endpoints.Count == 1 && !IsSiemensRunning;
            SiemensPortLabelText.Text = multiple ? "各 PLC 独立端口" : endpoints.Count == 0 ? "无 S7 设备" : "本机端口";
            BtnStart.IsEnabled = endpoints.Count > 0;
            if (multiple) BtnStart.Content = IsSiemensRunning ? "停止全部 S7" : "启动全部 S7";
            var lines = endpoints.Select(item =>
            {
                bool running = item.Endpoint.Protocol == SimulationProtocol.SiemensS7
                    ? _classicServers.Any(server => server.IsRunning && server.ListenPort == item.Endpoint.ListenPort)
                    : _plusServers.Any(server => server.IsRunning && server.ListenPort == item.Endpoint.ListenPort);
                string port = item.Endpoint.ListenPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (_activeTagGrid == null) { running = _server.IsRunning; port = PortBox.Text.Trim(); }
                if (!multiple && !running && int.TryParse(PortBox.Text, out int pendingPort) && pendingPort is > 0 and <= 65535)
                    port = pendingPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string name = item.Device.Name;
                if (!string.IsNullOrWhiteSpace(item.Device.SourceChannelName) && item.Device.SourceChannelName != name)
                    name = item.Device.SourceChannelName + "/" + name;
                string protocol = item.Endpoint.Protocol == SimulationProtocol.SiemensS7Plus ? " Plus" : "";
                return $"{name}{protocol}  127.0.0.1:{port}  {(running ? "运行中" : "已停止")}";
            });
            SiemensEndpointMapText.Text = endpoints.Count == 0 ? "导入包含 S7 地址的工程后显示监听地址。" : string.Join(Environment.NewLine, lines);
        }

        private void DeviceTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_activeTagGrid == null || _simulationStore == null || e.NewValue is not TreeViewItem selected) return;
            IEnumerable<SimulationTag> tags;
            string title;
            if (selected.Tag is SimulationDevice device) { tags = device.EnumerateTags(); title = device.Name; }
            else if (selected.Tag is SimulationGroup group) { tags = group.EnumerateTags(); title = group.SymbolPath; }
            else return;
            _importedRows = tags.Select(x => new SimulationTagRow(x, _simulationStore)).ToList();
            _activeTagGrid.ItemsSource = _importedRows;
            SelectionTitleText.Text = title;
            SelectionSummaryText.Text = $"{_importedRows.Count} 点 · 写入后 OPC UA、Omron 与界面同步";
        }

        private async void BtnDeleteTreeNode_Click(object sender, RoutedEventArgs e) => await DeleteSelectedTreeNodeAsync();

        private async void DeviceTree_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            e.Handled = true;
            await DeleteSelectedTreeNodeAsync();
        }

        private async Task DeleteSelectedTreeNodeAsync()
        {
            if (DeviceTree.SelectedItem is not TreeViewItem selected || _simulationProject == null || _simulationStore == null)
            {
                MessageBox.Show("请先在左侧选择要删除的设备或数据组。", "删除");
                return;
            }

            string kind;
            string name;
            int tagCount;
            if (selected.Tag is SimulationDevice device) { kind = "设备"; name = device.Name; tagCount = device.EnumerateTags().Count(); }
            else if (selected.Tag is SimulationGroup group) { kind = "数据组"; name = group.SymbolPath; tagCount = group.EnumerateTags().Count(); }
            else return;
            if (MessageBox.Show($"确定删除{kind}“{name}”吗？\n将同时删除其下 {tagCount} 个点位。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            var values = _simulationProject.EnumerateTags().ToDictionary(x => x.Id, x => _simulationStore.Read(x.Id).Value);
            if (_server.IsRunning) { _server.Stop(); UpdateRunningUi(false); }
            await StopImportedServersAsync();

            bool removed = selected.Tag switch
            {
                SimulationDevice d => _simulationProject.RemoveDevice(d),
                SimulationGroup g when g.Parent == null => g.Device.RemoveGroup(g),
                SimulationGroup g => g.Parent!.RemoveGroup(g),
                _ => false,
            };
            if (!removed) { MessageBox.Show("删除失败：节点已不存在或不属于当前项目。", "删除"); return; }

            _simulationStore = new SimulationValueStore(_simulationProject);
            foreach (var tag in _simulationProject.EnumerateTags())
                if (values.TryGetValue(tag.Id, out var value)) _simulationStore.Write(tag.Id, value, SimulationWriteSource.Initialization);
            BuildProjectWorkspace();
            AppendLog($"已删除{kind}：{name}（{tagCount} 个点位）");
        }

        private bool ShowImportPreview(string source, SimulationProject project, IReadOnlyList<string> warnings)
        {
            string protocols = string.Join("、", project.Devices.SelectMany(x => x.Endpoints).Select(x => x.Protocol).Distinct());
            if (protocols.Length == 0) protocols = "未识别";
            string warningText = warnings.Count == 0 ? "无警告" : $"{warnings.Count} 条警告（导入后写入日志）";
            string message = $"来源：{source}\n\n设备：{project.Devices.Count}\n数据组：{project.Devices.Sum(x => x.Groups.Count)}\n点位：{project.EnumerateTags().Count()}\n协议：{protocols}\n检查：{warningText}\n\n是否导入并替换当前模拟项目？";
            return MessageBox.Show(message, "导入预览", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not FieldRow row) return;
            ApplyRow(row);
        }

        private void ApplyRow(FieldRow row)
        {
            if (!_server.IsRunning)
            {
                MessageBox.Show("服务未启动，请先点击“启动服务”。", "提示");
                return;
            }
            var err = _server.WriteFieldValue(row.Def, row.EditValue);
            if (err != null)
            {
                MessageBox.Show($"{row.Def.Name}: {err}", "写入失败");
                return;
            }
            row.LiveValue = _server.ReadFieldValue(row.Def);
        }

        // ---------------- 场景按钮 ----------------

        private IEnumerable<(string, Action)> GetScenarios(int db)
        {
            switch (db)
            {
                case 2100:
                    yield return ("模拟K值请求", SimulateKValueRequest);
                    yield return ("模拟出站请求", SimulateLeaveRequest);
                    yield return ("MES响应OK", () => MesRespond(isNg: false));
                    yield return ("MES响应NG", () => MesRespond(isNg: true));
                    yield return ("流程结束/清位", () =>
                    {
                        Set(2100, "MESFlag", "4");
                        Set(2100, "PLCFlag", "3");
                        Set(2100, "Request", "false");
                    });
                    break;
                case 2101:
                    yield return ("上报设备异常", () =>
                    {
                        Set(2101, "PLCFlag", "1");
                        Set(2101, "MESFlag", "0");
                        Set(2101, "ErrorMessage", "SERVO ALARM: Axis1 over current");
                    });
                    yield return ("异常恢复", () =>
                    {
                        Set(2101, "PLCFlag", "3");
                        Set(2101, "MESFlag", "4");
                        Set(2101, "ErrorMessage", "");
                    });
                    break;
                case 2102:
                    yield return ("置为在线", () => Set(2102, "Offline", "0"));
                    yield return ("置为离线", () => Set(2102, "Offline", "1"));
                    break;
                case 2103:
                    yield return ("模拟刷卡登录", () =>
                    {
                        Set(2103, "PLCFlag", "1");
                        Set(2103, "MESFlag", "0");
                    });
                    yield return ("登录完成", () =>
                    {
                        Set(2103, "MESFlag", "2");
                        Set(2103, "PLCFlag", "3");
                    });
                    break;
            }
        }

        private readonly Random _rng = new Random();

        private void SimulateKValueRequest()
        {
            Set(2100, "Request", "true");
            Set(2100, "PLCFlag", "1");
            Set(2100, "MESFlag", "0");
            for (int i = 0; i < DbLayout.CellCount; i++)
            {
                Set(2100, $"CellData[{i}].DMCCode", $"M1FE-C1G7201-{i:D2}");
                Set(2100, $"CellData[{i}].KValue", (0.0005 + _rng.NextDouble() * 0.003).ToString("0.000000"));
                Set(2100, $"CellData[{i}].Voltage", (3.65 + _rng.NextDouble() * 0.2).ToString("0.000"));
                Set(2100, $"CellData[{i}].IsNG", "0");
                Set(2100, $"CellData[{i}].ErrorMessage", "");
            }
            Set(2100, "Temperature", (23 + _rng.NextDouble() * 3).ToString("0.0"));
        }

        private void SimulateLeaveRequest()
        {
            Set(2100, "Request", "true");
            Set(2100, "PLCFlag", "1");
            Set(2100, "MESFlag", "0");
        }

        private void MesRespond(bool isNg)
        {
            Set(2100, "MESFlag", "2");
            for (int i = 0; i < DbLayout.CellCount; i++)
            {
                if (isNg)
                {
                    Set(2100, $"CellData[{i}].IsNG", "2");
                    Set(2100, $"CellData[{i}].ErrorMessage", "K value out of range");
                    Set(2100, $"CellData[{i}].Errorinfo", "1");
                }
                else
                {
                    Set(2100, $"CellData[{i}].IsNG", "1");
                    Set(2100, $"CellData[{i}].ErrorMessage", "");
                    Set(2100, $"CellData[{i}].Errorinfo", "0");
                }
            }
        }

        private void Set(int db, string field, string value)
        {
            if (!_rows.TryGetValue((db, field), out var row)) return;
            _server.WriteFieldValue(row.Def, value);
            row.EditValue = value;
            row.LiveValue = _server.ReadFieldValue(row.Def);
        }

        // ---------------- 服务启停 ----------------

        private async void BtnImportDb_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入 Siemens TIA Portal DB（可多选）",
                Filter = "Siemens DB/SCL (*.db;*.scl;*.txt)|*.db;*.scl;*.txt|所有文件 (*.*)|*.*",
                Multiselect = true,
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var imported = SiemensDbSimulationImporter.ImportFiles(dialog.FileNames);
                if (!ShowImportPreview($"{dialog.FileNames.Length} 个 Siemens DB 文件", imported.Project, imported.Warnings)) return;
                await ApplyImportedProjectAsync(imported.Project);
                AppendLog($"DB 导入完成：{imported.DataBlockName}，共 {imported.Project.Devices.Single().Groups.Count} 个 DB、{imported.Project.EnumerateTags().Count()} 点，自动识别={imported.Project.Devices.Single().SiemensProfile}");
                foreach (string item in imported.Warnings.Take(30)) AppendLog("导入警告：" + item);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Siemens DB 导入失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async Task ApplyImportedProjectAsync(SimulationProject project)
        {
            await StopImportedServersAsync();
            if (_server.IsRunning) { _server.Stop(); UpdateRunningUi(false); }
            _simulationProject = project;
            _simulationStore = new SimulationValueStore(project);
            BuildProjectWorkspace();
        }

        private async void BtnImportKep_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "导入 KEPServerEX 原生配置", Filter = "KEP JSON (*.json)|*.json|所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var imported = KepJsonSimulationImporter.ImportFile(dialog.FileName);
                if (!ShowImportPreview(System.IO.Path.GetFileName(dialog.FileName), imported.Project, imported.Warnings)) return;
                await StopImportedServersAsync();
                if (_server.IsRunning) { _server.Stop(); UpdateRunningUi(false); }
                _simulationProject = imported.Project;
                _simulationStore = new SimulationValueStore(imported.Project);
                BuildProjectWorkspace();
                string warning = imported.Warnings.Count == 0 ? "" : $"，警告 {imported.Warnings.Count} 条（已写入日志）";
                AppendLog($"KEP 导入完成：{imported.ChannelCount} 个通道、{imported.Project.Devices.Count} 台设备、{imported.Project.EnumerateTags().Count()} 个点位{warning}");
                foreach (string item in imported.Warnings.Take(30)) AppendLog("导入警告：" + item);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "KEP JSON 导入失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private DataGrid CreateImportedTagGrid()
        {
            var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Visible };
            VirtualizingPanel.SetIsVirtualizing(grid, true); VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
            grid.Columns.Add(new DataGridTextColumn { Header = "名称", Binding = new System.Windows.Data.Binding(nameof(SimulationTagRow.Name)), Width = 175, IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "协议地址", Binding = new System.Windows.Data.Binding(nameof(SimulationTagRow.Address)), Width = 200, IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new System.Windows.Data.Binding(nameof(SimulationTagRow.Type)), Width = 78, IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "当前值", Binding = new System.Windows.Data.Binding(nameof(SimulationTagRow.LiveValue)), Width = 95, IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "写入值", Binding = new System.Windows.Data.Binding(nameof(SimulationTagRow.EditValue)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 125 });
            var write = new DataGridTemplateColumn { Header = "", Width = 54 }; var button = new FrameworkElementFactory(typeof(Button)); button.SetValue(Button.ContentProperty, "写入"); button.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnSimulationApplyClick)); write.CellTemplate = new DataTemplate { VisualTree = button }; grid.Columns.Add(write);
            grid.Columns.Add(new DataGridTextColumn { Header = "注释", Binding = new System.Windows.Data.Binding(nameof(SimulationTagRow.Comment)), Width = 300, IsReadOnly = true });
            return grid;
        }

        private void OnSimulationApplyClick(object sender, RoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not SimulationTagRow row) return;
            try { row.Write(); } catch (Exception ex) { MessageBox.Show(ex.Message, "写入失败"); }
        }

        private async void BtnStartAll_Click(object sender, RoutedEventArgs e)
        {
            if (IsSiemensRunning || _opcUaServer != null || _cipServers.Count > 0 || _finsServers.Count > 0)
            {
                await StopSiemensServersAsync();
                await StopImportedServersAsync();
                BtnStartAll.Content = "启动全部协议";
                AppendLog("全部协议服务已停止");
                return;
            }

            await StartSiemensAsync(false);
            await StartOpcUaAsync(true);
            StartOmronCip(false);
            StartOmronFins(false);
            BtnStartAll.Content = "停止全部协议";
        }

        private async void BtnStartOpcUa_Click(object sender, RoutedEventArgs e)
        {
            if (_opcUaServer != null)
            {
                await _opcUaServer.DisposeAsync(); _opcUaServer = null;
                OpcUaStatusText.Text = "已停止"; BtnStartOpcUa.Content = "启动 OPC UA"; OpcUaPortBox.IsEnabled = true;
                AppendLog("OPC UA 服务已停止"); return;
            }
            await StartOpcUaAsync(true);
        }

        private async Task<bool> StartOpcUaAsync(bool showError)
        {
            if (_opcUaServer != null) return true;
            if (_simulationProject == null || _simulationStore == null) return false;
            if (!int.TryParse(OpcUaPortBox.Text, out int port) || port is <= 0 or > 65535)
            {
                if (showError) MessageBox.Show("OPC UA 端口无效"); return false;
            }
            try
            {
                _opcUaServer = new OpcUaSimulationServer();
                string pki = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlcSimulator", "pki");
                // 按现场 PLC 型号的容量上限和实测处理耗时模拟，避免在模拟器上测出比现场乐观的结果。
                var profiles = ParseOpcUaProfiles(OpcUaProfileBox.Text);
                OpcUaSimulationLimits LimitsFor(string channel) =>
                    profiles.TryGetValue(channel, out var own) ? own : profiles.GetValueOrDefault("*");
                await _opcUaServer.StartAsync(_simulationProject, _simulationStore, port, pki, LimitsFor);
                foreach (var endpoint in _opcUaServer.Endpoints)
                {
                    var limits = LimitsFor(endpoint.ChannelName);
                    if (limits != null)
                        AppendLog($"{endpoint.ChannelName} 按 {limits.Name} 性能模拟：会话 ≤{limits.MaxSessions}，大订阅 ≤{limits.MaxLargeSubscriptions}，小订阅 ≤{limits.MaxSmallSubscriptions}，建议监视项 ≤{limits.RecommendedMonitoredItems}，读取 ≈{limits.ReadBaseMs:0} ms + {limits.ReadPerNodeMs:0.#} ms/点");
                }
                OpcUaStatusText.Text = "运行中"; OpcUaStatusText.Foreground = Brushes.ForestGreen;
                var endpoints = _opcUaServer.Endpoints;
                // 多个 PLC(通道)各占一个端口，逐行列出，网关按通道把地址改成对应端点即可。
                OpcUaEndpointText.Text = string.Join(Environment.NewLine, endpoints.Select(x =>
                    string.IsNullOrEmpty(x.ChannelName) || endpoints.Count == 1 ? x.Url : $"{x.ChannelName} → {x.Url}"));
                BtnStartOpcUa.Content = "停止 OPC UA"; OpcUaPortBox.IsEnabled = false; StartUiTimers();
                foreach (var endpoint in endpoints)
                    AppendLog($"OPC UA 已启动：{endpoint.Url}{(string.IsNullOrEmpty(endpoint.ChannelName) ? "" : $"（{endpoint.ChannelName}，{endpoint.DeviceCount} 台设备，{endpoint.TagCount} 个标记）")}（匿名、无安全策略、可浏览和读写）");
                return true;
            }
            catch (Exception ex)
            {
                if (_opcUaServer != null) await _opcUaServer.DisposeAsync(); _opcUaServer = null;
                OpcUaStatusText.Text = "启动失败";
                if (showError) MessageBox.Show(ex.Message, "OPC UA 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>解析“PLC7=1517,*=1515”；未写的通道使用 * 的档位，写 none 表示不限速。</summary>
        private static Dictionary<string, OpcUaSimulationLimits> ParseOpcUaProfiles(string text)
        {
            var result = new Dictionary<string, OpcUaSimulationLimits>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in (text ?? "").Split(new[] { ',', '，', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = item.Split('=');
                if (pair.Length != 2) throw new ArgumentException($"性能档位格式应为 通道=型号，如 PLC7=1517：{item}");
                var limits = OpcUaSimulationLimits.ByName(pair[1]);
                if (limits != null) result[pair[0].Trim()] = limits;
            }
            return result;
        }

        private void BtnStartOmronCip_Click(object sender, RoutedEventArgs e)
        {
            if (_cipServers.Count > 0) { StopCipServers(); return; }
            StartOmronCip(true);
        }

        private bool StartOmronCip(bool showError)
        {
            if (_simulationProject == null || _simulationStore == null) return false;
            var matches = _simulationProject.Devices.SelectMany(d => d.Endpoints.Where(x => x.Enabled && x.Protocol == SimulationProtocol.OmronCip).Select(x => (Device: d, Endpoint: x))).ToList();
            if (matches.Count == 0)
            {
                OmronCipStatusText.Text = "无 CIP 设备";
                if (showError) MessageBox.Show("当前项目没有 Omron NJ/NX CIP 设备。请导入包含 Omron NJ/NX Ethernet 驱动的 KEP JSON。", "Omron CIP");
                return false;
            }
            try
            {
                if (matches.Count == 1 && int.TryParse(CipPortBox.Text, out int port) && port is > 0 and <= 65535) matches[0].Endpoint.ListenPort = port;
                foreach (var match in matches) { var server = new OmronCipSimulationServer(match.Device, _simulationStore, match.Endpoint.ListenPort); server.Start(); _cipServers.Add(server); }
                OmronCipStatusText.Text = $"运行中 · {_cipServers.Count}"; OmronCipStatusText.Foreground = Brushes.ForestGreen;
                BtnStartOmronCip.Content = "停止 CIP"; CipPortBox.IsEnabled = false; StartUiTimers();
                AppendLog($"Omron CIP 已启动：{string.Join(", ", matches.Select(x => $"{x.Device.Name}:{x.Endpoint.ListenPort}"))}"); return true;
            }
            catch (Exception ex) { StopCipServers(); if (showError) MessageBox.Show(ex.Message, "Omron CIP 启动失败"); return false; }
        }

        private void StopCipServers()
        {
            foreach (var server in _cipServers) server.DisposeAsync().GetAwaiter().GetResult();
            _cipServers.Clear(); OmronCipStatusText.Text = "已停止"; OmronCipStatusText.Foreground = Brushes.Gray;
            BtnStartOmronCip.Content = "启动 CIP"; CipPortBox.IsEnabled = true; AppendLog("Omron CIP 已停止");
        }

        private void BtnStartOmronFins_Click(object sender, RoutedEventArgs e)
        {
            if (_finsServers.Count > 0) { StopFinsServers(); return; }
            StartOmronFins(true);
        }

        private bool StartOmronFins(bool showError)
        {
            if (_simulationProject == null || _simulationStore == null) return false;
            var matches = _simulationProject.Devices.SelectMany(d => d.Endpoints.Where(x => x.Enabled && x.Protocol is SimulationProtocol.OmronFinsTcp or SimulationProtocol.OmronFinsUdp).Select(x => (Device: d, Endpoint: x))).ToList();
            if (matches.Count == 0)
            {
                OmronFinsStatusText.Text = "无 FINS 设备";
                if (showError) MessageBox.Show("当前项目没有 Omron FINS 设备。请导入包含 Omron FINS 驱动的 KEP JSON。", "Omron FINS");
                return false;
            }
            try
            {
                if (matches.Count == 1 && int.TryParse(FinsPortBox.Text, out int port) && port is > 0 and <= 65535) matches[0].Endpoint.ListenPort = port;
                foreach (var match in matches) { var server = new OmronFinsSimulationServer(match.Device, _simulationStore, match.Endpoint.ListenPort); server.Start(); _finsServers.Add(server); }
                OmronFinsStatusText.Text = $"运行中 · {_finsServers.Count}"; OmronFinsStatusText.Foreground = Brushes.ForestGreen;
                BtnStartOmronFins.Content = "停止 FINS"; FinsPortBox.IsEnabled = false; StartUiTimers();
                AppendLog($"Omron FINS 已启动：{string.Join(", ", matches.Select(x => $"{x.Device.Name}:{x.Endpoint.ListenPort}"))}"); return true;
            }
            catch (Exception ex) { StopFinsServers(); if (showError) MessageBox.Show(ex.Message, "Omron FINS 启动失败"); return false; }
        }

        private void StopFinsServers()
        {
            foreach (var server in _finsServers) server.DisposeAsync().GetAwaiter().GetResult();
            _finsServers.Clear(); OmronFinsStatusText.Text = "已停止"; OmronFinsStatusText.Foreground = Brushes.Gray;
            BtnStartOmronFins.Content = "启动 FINS"; FinsPortBox.IsEnabled = true; AppendLog("Omron FINS 已停止");
        }

        private async Task StopImportedServersAsync()
        {
            foreach (var server in _classicServers) await server.DisposeAsync(); _classicServers.Clear();
            foreach (var server in _plusServers) await server.DisposeAsync(); _plusServers.Clear();
            foreach (var server in _cipServers) await server.DisposeAsync(); _cipServers.Clear();
            foreach (var server in _finsServers) await server.DisposeAsync(); _finsServers.Clear();
            if (_opcUaServer != null) await _opcUaServer.DisposeAsync(); _opcUaServer = null;
            if (BtnStartOpcUa != null)
            {
                OpcUaStatusText.Text = "已停止"; OpcUaStatusText.Foreground = Brushes.Gray; BtnStartOpcUa.Content = "启动 OPC UA"; OpcUaPortBox.IsEnabled = true;
                OmronCipStatusText.Text = "已停止"; OmronCipStatusText.Foreground = Brushes.Gray; BtnStartOmronCip.Content = "启动 CIP"; CipPortBox.IsEnabled = true;
                OmronFinsStatusText.Text = "已停止"; OmronFinsStatusText.Foreground = Brushes.Gray; BtnStartOmronFins.Content = "启动 FINS"; FinsPortBox.IsEnabled = true;
            }
            if (!_server.IsRunning) UpdateRunningUi(false);
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (IsSiemensRunning)
            {
                StopUiTimers();
                _trafficLogs.Clear();
                await StopSiemensServersAsync();
                AppendLog("服务已停止");
                return;
            }

            await StartSiemensAsync(true);
        }

        private async Task StopSiemensServersAsync()
        {
            if (_server.IsRunning) _server.Stop();
            foreach (var server in _classicServers) await server.DisposeAsync();
            _classicServers.Clear();
            foreach (var server in _plusServers) await server.DisposeAsync();
            _plusServers.Clear();
            UpdateRunningUi(false);
        }

        private Task<bool> StartSiemensAsync(bool showError)
        {
            if (IsSiemensRunning) return Task.FromResult(true);

            if (!int.TryParse(PortBox.Text.Trim(), out int port) || port <= 0 || port > 65535)
            {
                if (showError) MessageBox.Show("端口号无效。", "提示");
                return Task.FromResult(false);
            }

            if (_activeTagGrid == null)
            {
                var err = _server.Start(port);
                if (err != null)
                {
                    AppendLog(err);
                    if (showError) MessageBox.Show(err, "启动失败");
                    return Task.FromResult(false);
                }
                _server.EnableWrite = ChkEnableWrite.IsChecked == true;
                UpdateRunningUi(true);
                StartUiTimers();
                AppendLog($"服务已启动，自动识别 Classic S7（S7-1500 兼容，Rack 0 Slot 1），监听端口 {port}，DB2100~2103 已就绪");
                return Task.FromResult(true);
            }

            if (_simulationProject == null || _simulationStore == null) return Task.FromResult(false);
            var classicMatches = _simulationProject.Devices
                .SelectMany(device => device.Endpoints
                    .Where(endpoint => endpoint.Enabled && endpoint.Protocol == SimulationProtocol.SiemensS7)
                    .Select(endpoint => (Device: device, Endpoint: endpoint)))
                .ToList();
            var plusMatches = _simulationProject.Devices
                .SelectMany(device => device.Endpoints
                    .Where(endpoint => endpoint.Enabled && endpoint.Protocol == SimulationProtocol.SiemensS7Plus)
                    .Select(endpoint => (Device: device, Endpoint: endpoint)))
                .ToList();
            if (classicMatches.Count + plusMatches.Count == 0)
            {
                StatusText.Text = "无 Siemens 设备";
                if (showError) MessageBox.Show("当前项目没有可启动的 Siemens 设备。", "Siemens S7");
                return Task.FromResult(false);
            }

            try
            {
                if (classicMatches.Count + plusMatches.Count == 1)
                {
                    if (classicMatches.Count == 1) classicMatches[0].Endpoint.ListenPort = port;
                    else plusMatches[0].Endpoint.ListenPort = port;
                }

                var usedPorts = new HashSet<int>();
                foreach (var match in classicMatches)
                {
                    while (!usedPorts.Add(match.Endpoint.ListenPort)) match.Endpoint.ListenPort++;
                    var server = new SiemensClassicSimulationServer(match.Device, _simulationStore, match.Endpoint.ListenPort)
                    {
                        EnableWrite = ChkEnableWrite.IsChecked == true,
                    };
                    server.Start();
                    _classicServers.Add(server);
                }
                foreach (var match in plusMatches)
                {
                    while (!usedPorts.Add(match.Endpoint.ListenPort)) match.Endpoint.ListenPort++;
                    var server = new SiemensPlusSimulationServer(match.Device, _simulationStore, match.Endpoint.ListenPort)
                    {
                        EnableWrite = ChkEnableWrite.IsChecked == true,
                    };
                    server.Start();
                    _plusServers.Add(server);
                }
                UpdateRunningUi(true);
                StartUiTimers();
                if (classicMatches.Count > 0)
                    AppendLog($"Classic S7 已按导入项目启动：{string.Join(", ", classicMatches.Select(x => $"{x.Device.Name}:{x.Endpoint.ListenPort}"))}（Rack 0 Slot 1）");
                if (plusMatches.Count > 0)
                    AppendLog($"S7 Plus 已按导入项目启动：{string.Join(", ", plusMatches.Select(x => $"{x.Device.Name}:{x.Endpoint.ListenPort}"))}（匿名、无需证书配置、支持符号浏览与读写）");
                if (classicMatches.Count > 0 && plusMatches.Count > 0)
                    AppendLog("混合项目在本机使用独立监听端口，端口映射见上方日志；两种协议共享同一份实时值。");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                foreach (var server in _classicServers) server.DisposeAsync().GetAwaiter().GetResult();
                _classicServers.Clear();
                foreach (var server in _plusServers) server.DisposeAsync().GetAwaiter().GetResult();
                _plusServers.Clear();
                UpdateRunningUi(false);
                AppendLog($"Siemens S7 启动失败（端口 {port}）：{ex}");
                if (showError) MessageBox.Show(ex.Message, "Siemens S7 启动失败");
                return Task.FromResult(false);
            }
        }

        private void UpdateRunningUi(bool running)
        {
            StatusDot.Fill = running ? Brushes.LimeGreen : Brushes.Gray;
            StatusText.Text = running ? "运行中" : "已停止";
            BtnStart.Content = running ? "停止服务" : "启动服务";
            PortBox.IsEnabled = !running;
            if (!running) OnlineText.Text = "连接数: 0";
            RefreshSiemensEndpoints();
        }

        private void ChkEnableWrite_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = ChkEnableWrite.IsChecked == true;
            _server.EnableWrite = enabled;
            foreach (var server in _classicServers) server.EnableWrite = enabled;
            foreach (var server in _plusServers) server.EnableWrite = enabled;
        }

        private void StartUiTimers()
        {
            _valueTimer.Start();
            _logTimer.Start();
        }

        private void StopUiTimers()
        {
            _valueTimer.Stop();
            _logTimer.Stop();
        }

        // ---------------- 定时刷新 ----------------

        private void RefreshLiveValues()
        {
            if (!IsSiemensRunning && _opcUaServer == null && _cipServers.Count == 0 && _finsServers.Count == 0) return;
            OnlineText.Text = $"连接数: {SiemensOnlineCount + _cipServers.Sum(x => x.OnlineCount) + _finsServers.Sum(x => x.OnlineCount)}";
            RefreshSiemensEndpoints();

            if (_simulationStore != null && _importedRows.Count > 0) foreach (var row in _importedRows) row.Refresh();

            if (!IsSiemensRunning) return;
            if (!_server.IsRunning) return;
            if (TabDbs.SelectedItem is not TabItem { Tag: int dbNumber }) return;
            if (!_rowsByDb.TryGetValue(dbNumber, out var rows)) return;
            foreach (var row in rows)
                row.LiveValue = _server.ReadFieldValue(row.Def);
        }

        // ---------------- 日志 ----------------

        private void OnTraffic(TrafficEntry entry)
        {
            bool isRead = entry.Text.StartsWith("读");
            bool isWrite = entry.Text.StartsWith("写");
            bool isConnect = entry.Text.Contains("连接");
            if (isRead && !_logRead) return;
            if (isWrite && !_logWrite) return;
            if (isConnect && !_logConnect) return;
            _trafficLogs.Enqueue(entry.ToString());
        }

        private void LogFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkLogRead == null || ChkLogWrite == null || ChkLogConnect == null) return;
            _logRead = ChkLogRead.IsChecked == true;
            _logWrite = ChkLogWrite.IsChecked == true;
            _logConnect = ChkLogConnect.IsChecked == true;
            _server.LogReads = _logRead;
            _server.LogWrites = _logWrite;
            _server.LogConnections = _logConnect;
        }

        private void FlushTrafficLogs()
        {
            var batch = _trafficLogs.Drain(100);
            if (batch.Lines.Count == 0 && batch.Dropped == 0) return;

            var lines = new List<string>(batch.Lines.Count + 1);
            if (batch.Dropped > 0)
                lines.Add($"[{DateTime.Now:HH:mm:ss}] 高频日志已限流，跳过 {batch.Dropped} 条旧记录");
            lines.AddRange(batch.Lines.Select(line => $"[{DateTime.Now:HH:mm:ss}] {line}"));
            AddVisibleLogLines(lines);
        }

        private void AppendLog(string line)
        {
            AddVisibleLogLines(new[] { $"[{DateTime.Now:HH:mm:ss}] {line}" });
        }

        private void AddVisibleLogLines(IEnumerable<string> lines)
        {
            var appended = new System.Text.StringBuilder();
            bool trimmed = false;
            foreach (string line in lines)
            {
                _visibleLogLines.Enqueue(line);
                appended.AppendLine(line);
                if (_visibleLogLines.Count > 800)
                {
                    _visibleLogLines.Dequeue();
                    trimmed = true;
                }
            }

            if (trimmed)
                LogBox.Text = string.Join(Environment.NewLine, _visibleLogLines) + Environment.NewLine;
            else
                LogBox.AppendText(appended.ToString());
            LogBox.ScrollToEnd();
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _trafficLogs.Clear();
            _visibleLogLines.Clear();
            LogBox.Clear();
        }

        // ---------------- 本地自测 ----------------

        private async void BtnSelfTest_Click(object sender, RoutedEventArgs e)
        {
            if (!IsSiemensRunning)
            {
                MessageBox.Show("服务未启动，请先启动。", "提示");
                return;
            }
            BtnSelfTest.IsEnabled = false;
            try
            {
                int port = int.Parse(PortBox.Text.Trim());
                var sample = _simulationProject?.Devices
                    .Where(x => x.SiemensProfile is SiemensSimulationProfile.Classic or SiemensSimulationProfile.Hybrid)
                    .SelectMany(x => x.EnumerateTags())
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ClassicAddress));
                var result = await Task.Run(() =>
                {
                    var client = new HslCommunication.Profinet.Siemens.SiemensS7Net(HslCommunication.Profinet.Siemens.SiemensPLCS.S1500, "127.0.0.1");
                    client.Port = port;
                    client.Rack = 0;
                    client.Slot = 1;
                    var r1 = client.ConnectServer();
                    if (!r1.IsSuccess) return "连接失败：" + r1.Message;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("自动识别 Classic S7，兼容 S7-1500，Rack 0 Slot 1");
                    if (sample != null)
                    {
                        var value = client.Read(sample.ClassicAddress, 1);
                        sb.AppendLine($"读 {sample.ClassicAddress} ({sample.SymbolPath}) = {(value.IsSuccess ? BitConverter.ToString(value.Content) : value.Message)}");
                    }
                    client.ConnectClose();
                    return "自测结果：\n" + sb;
                });
                MessageBox.Show(result, "本地自测");
            }
            catch (Exception ex)
            {
                MessageBox.Show("自测异常：" + ex.Message, "本地自测");
            }
            finally
            {
                BtnSelfTest.IsEnabled = true;
            }
        }
    }
}
