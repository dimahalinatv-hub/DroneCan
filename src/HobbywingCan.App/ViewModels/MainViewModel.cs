using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Link;
using HobbywingCan.Core.Mavlink;
using HobbywingCan.Core.Monitoring;
using HobbywingCan.Core.Simulation;

namespace HobbywingCan.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 300;

    private readonly IDispatcher _dispatcher;
    private readonly IDispatcherTimer _timer;
    private readonly HashSet<byte> _nodeInfoRequested = new();

    private IDisposable? _connection;
    private MavlinkCanLink? _link;
    private DroneCanNode? _node;
    private EscMonitor? _monitor;
    private EscConfigReader? _reader;
    private CancellationTokenSource? _readCts;
    private EscConfigReport? _lastReport;
    private byte? _lastForwardAckResult;
    private bool _disconnecting;
    // node whose configuration was already read for the current selection; null → read on the next tick
    private byte? _autoReadNodeId;
    private byte? _readingNodeId;

    private string? _selectedPort;
    private int _selectedBaud = 115200;
    private int _selectedCanBus = 1;
    private string _localNodeIdText = "126";
    private bool _useFilter;
    private bool _pollEscIds = true;
    private bool _isConnected;
    private bool _isBusy;
    private string _vehicleText = "Not connected";
    private string _statsText = "";
    private NodeChoice? _selectedConfigNode;
    private bool _includeParameters = true;
    private bool _isReading;
    private string _configProgress = "";

    public MainViewModel(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Refresh();

        RefreshPortsCommand = new Command(RefreshPorts, () => !IsConnected);
        ConnectCommand = new Command(async () => await ToggleConnectionAsync(), () => !_isBusy);
        ReadConfigCommand = new Command(() => _autoReadNodeId = null, () => IsConnected && !IsReading && SelectedConfigNode != null);
        CopyReportCommand = new Command(async () => await CopyReportAsync(), () => _lastReport != null);
        ClearLogCommand = new Command(() => Log.Clear());
        InitSettings();

        RefreshPorts();

        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--simulate"))
        {
            SelectedPort = SimulatedAutopilot.PortName;
            _dispatcher.Dispatch(() => ConnectCommand.Execute(null));
        }
        if (args.Contains("--settings"))
        {
            ShowSettings = true;
        }
    }

    public ObservableCollection<string> Ports { get; } = new();
    public IReadOnlyList<int> BaudRates { get; } = [57600, 115200, 230400, 460800, 921600, 1500000];
    public IReadOnlyList<int> CanBuses { get; } = [1, 2, 3];
    public ObservableCollection<EscRowViewModel> EscRows { get; } = new();
    public ObservableCollection<AutopilotEscRowViewModel> AutopilotRows { get; } = new();
    public ObservableCollection<TrafficRowViewModel> TrafficRows { get; } = new();
    public ObservableCollection<NodeChoice> ConfigNodes { get; } = new();
    public ObservableCollection<ConfigItem> ConfigItems { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    public ICommand RefreshPortsCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ReadConfigCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand ClearLogCommand { get; }

    public string? SelectedPort { get => _selectedPort; set => SetProperty(ref _selectedPort, value); }
    public int SelectedBaud { get => _selectedBaud; set => SetProperty(ref _selectedBaud, value); }
    public int SelectedCanBus { get => _selectedCanBus; set => SetProperty(ref _selectedCanBus, value); }
    public string LocalNodeIdText { get => _localNodeIdText; set => SetProperty(ref _localNodeIdText, value); }
    public bool UseFilter { get => _useFilter; set => SetProperty(ref _useFilter, value); }

    public bool PollEscIds
    {
        get => _pollEscIds;
        set
        {
            if (SetProperty(ref _pollEscIds, value) && _monitor != null)
            {
                _monitor.PollEscIds = value;
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsDisconnected));
                OnPropertyChanged(nameof(ConnectButtonText));
                RefreshCommands();
            }
        }
    }

    public bool IsDisconnected => !IsConnected;
    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";
    public string VehicleText { get => _vehicleText; private set => SetProperty(ref _vehicleText, value); }
    public string StatsText { get => _statsText; private set => SetProperty(ref _statsText, value); }

    public NodeChoice? SelectedConfigNode
    {
        get => _selectedConfigNode;
        set
        {
            // no read handling here: the Picker sets null for a moment whenever the selected item is
            // replaced (node name arrives), so AutoReadConfig() follows the selection on the timer tick
            if (SetProperty(ref _selectedConfigNode, value))
            {
                RefreshCommands();
                LoadSettingsFromNode();
            }
        }
    }

    public bool IncludeParameters
    {
        get => _includeParameters;
        set
        {
            if (SetProperty(ref _includeParameters, value) && value)
            {
                _autoReadNodeId = null;
            }
        }
    }

    public bool IsReading
    {
        get => _isReading;
        private set
        {
            if (SetProperty(ref _isReading, value))
            {
                RefreshCommands();
            }
        }
    }

    public string ConfigProgress { get => _configProgress; private set => SetProperty(ref _configProgress, value); }

    private void RefreshCommands()
    {
        foreach (var c in new[] { RefreshPortsCommand, ConnectCommand, ReadConfigCommand, CopyReportCommand })
        {
            ((Command)c).ChangeCanExecute();
        }
        RefreshSettingsCommands();
    }

    private void RefreshPorts()
    {
        var current = SelectedPort;
        Ports.Clear();
        foreach (var p in SerialConnection.GetPortNames())
        {
            Ports.Add(p);
        }
        Ports.Add(SimulatedAutopilot.PortName);
        SelectedPort = current != null && Ports.Contains(current) ? current : Ports.FirstOrDefault();
    }

    private async Task ToggleConnectionAsync()
    {
        _isBusy = true;
        RefreshCommands();
        try
        {
            if (IsConnected)
            {
                await DisconnectAsync("Disconnected");
            }
            else
            {
                await ConnectAsync();
            }
        }
        finally
        {
            _isBusy = false;
            RefreshCommands();
        }
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrEmpty(SelectedPort))
        {
            AddLog("Select a COM port first");
            return;
        }
        if (!byte.TryParse(LocalNodeIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var localId) || localId is < 1 or > 127)
        {
            AddLog("App node ID must be 1..127 and must not collide with the autopilot or ESC node IDs");
            return;
        }

        var port = SelectedPort;
        Stream stream;
        if (port == SimulatedAutopilot.PortName)
        {
            var sim = new SimulatedAutopilot([1, 2, 3, 4], autoRun: true, setIdIgnoresDestination: true);
            _connection = sim;
            stream = sim;
        }
        else
        {
            try
            {
                var serial = await Task.Run(() => SerialConnection.Open(port, SelectedBaud));
                _connection = serial;
                stream = serial.Stream;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                AddLog($"Cannot open {port}: {ex.Message}");
                return;
            }
        }

        _link = new MavlinkCanLink(stream, new MavlinkCanLinkOptions
        {
            CanBus = (byte)SelectedCanBus,
            UseForwardFilter = UseFilter,
        });
        _link.StatusTextReceived += text => Post(() => AddLog($"FC: {text}"));
        _link.CommandAckReceived += OnCommandAck;
        _link.Faulted += ex => Post(async () => await DisconnectAsync($"Link error: {ex.Message}"));
        _node = new DroneCanNode(_link, localId);
        _monitor = new EscMonitor(_node, _link) { PollEscIds = PollEscIds };
        _reader = new EscConfigReader(_node);
        _writer = new EscConfigWriter(_node);
        _link.Start();

        EscRows.Clear();
        AutopilotRows.Clear();
        TrafficRows.Clear();
        ConfigNodes.Clear();
        _nodeInfoRequested.Clear();
        _majorConfigRequested.Clear();
        _autoReadNodeId = null;
        _lastReport = null;
        ConfigItems.Clear();
        // a new connection may be a different ESC with the same node ID
        _settingsNodeId = null;
        _loadedSettings = null;
        _lastForwardAckResult = null;
        IsConnected = true;
        _timer.Start();
        AddLog($"Opened {port} @ {SelectedBaud}, CAN{SelectedCanBus}, app node ID {localId}. Waiting for heartbeat…");
    }

    private async Task DisconnectAsync(string reason)
    {
        if (!IsConnected || _disconnecting)
        {
            return;
        }
        _disconnecting = true;
        _timer.Stop();
        _readCts?.Cancel();
        _monitor?.Dispose();
        _node?.Dispose();
        if (_link != null)
        {
            await _link.DisposeAsync();
        }
        _connection?.Dispose();
        _monitor = null;
        _node = null;
        _link = null;
        _connection = null;
        _reader = null;
        _writer = null;
        IsConnected = false;
        VehicleText = "Not connected";
        _disconnecting = false;
        AddLog(reason);
    }

    private void OnCommandAck(ushort command, byte result)
    {
        if (command != MavlinkIds.MavCmdCanForward || result == _lastForwardAckResult)
        {
            return;
        }
        _lastForwardAckResult = result;
        Post(() => AddLog(result == 0
            ? $"CAN forwarding enabled on CAN{SelectedCanBus}"
            : $"Autopilot rejected CAN forwarding (MAV_RESULT {result}). Check the bus number and that CAN_P{SelectedCanBus}_DRIVER is set."));
    }

    private void Refresh()
    {
        if (_link == null || _monitor == null || _node == null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_link.Vehicle is { } v)
        {
            double age = (now - _link.LastHeartbeatUtc).TotalSeconds;
            VehicleText = age > 3
                ? $"Heartbeat lost ({age:F0} s)"
                : $"FC sys {v.SystemId} — {(v.IsArmed ? "ARMED (requests disabled)" : "disarmed")}";
        }
        StatsText = $"MAVLink {_link.MavlinkPackets} pkts, CRC err {_link.MavlinkCrcErrors} | CAN rx {_link.CanFramesReceived} tx {_link.CanFramesSent} | transfers {_node.TransfersReceived}, CRC err {_node.CrcErrors}, incomplete answers {_node.IncompleteResponses}";

        foreach (var state in _monitor.Nodes)
        {
            var row = EscRows.FirstOrDefault(r => r.NodeId == state.NodeId);
            if (row == null)
            {
                row = new EscRowViewModel(state.NodeId);
                int index = EscRows.TakeWhile(r => r.NodeId < state.NodeId).Count();
                EscRows.Insert(index, row);
            }
            row.Update(state, now);
            UpdateConfigNode(state);
            RequestNodeInfoOnce(state);
            RequestMajorConfigOnce(state);
        }
        SyncSettingsEditors();
        AutoReadConfig();

        foreach (var e in _monitor.Traffic)
        {
            var row = TrafficRows.FirstOrDefault(r => r.NodeId == e.SourceNodeId && r.Kind == e.Kind && r.DataTypeId == e.DataTypeId);
            if (row == null)
            {
                row = new TrafficRowViewModel(e.SourceNodeId, e.Kind, e.DataTypeId, e.TypeName);
                int index = TrafficRows.TakeWhile(r => (r.NodeId, r.Kind, r.DataTypeId).CompareTo((e.SourceNodeId, e.Kind, e.DataTypeId)) < 0).Count();
                TrafficRows.Insert(index, row);
            }
            row.Update(e, now);
        }

        foreach (var t in _monitor.AutopilotEsc)
        {
            var row = AutopilotRows.FirstOrDefault(r => r.Index == t.Index);
            if (row == null)
            {
                row = new AutopilotEscRowViewModel(t.Index);
                int index = AutopilotRows.TakeWhile(r => r.Index < t.Index).Count();
                AutopilotRows.Insert(index, row);
            }
            row.Update(t);
        }
    }

    private void UpdateConfigNode(NodeState state)
    {
        var display = state.Info != null ? $"{state.NodeId} — {state.Info.Name}" : $"Node {state.NodeId}";
        int i = ConfigNodes.ToList().FindIndex(n => n.NodeId == state.NodeId);
        if (i >= 0 && ConfigNodes[i].Display == display)
        {
            return;
        }
        var choice = new NodeChoice(state.NodeId, display);
        bool wasSelected = SelectedConfigNode?.NodeId == state.NodeId;
        if (i >= 0)
        {
            ConfigNodes[i] = choice;
        }
        else
        {
            ConfigNodes.Insert(ConfigNodes.TakeWhile(n => n.NodeId < state.NodeId).Count(), choice);
        }
        if (wasSelected || SelectedConfigNode == null && state.IsHobbywing)
        {
            SelectedConfigNode = choice;
        }
    }

    /// <summary>Fetch node names once (only while disarmed) so the table shows what each node is.</summary>
    private void RequestNodeInfoOnce(NodeState state)
    {
        if (state.Info != null || _link is not { Vehicle: not null, IsArmed: false } || _node == null ||
            !_nodeInfoRequested.Add(state.NodeId))
        {
            return;
        }
        var node = _node;
        _ = Task.Run(() => node.RequestWithRetryAsync(DataTypes.GetNodeInfo, state.NodeId, [], TimeSpan.FromMilliseconds(500), 2));
    }

    /// <summary>Read the selected ESC's configuration once per selection, as soon as the vehicle is known and disarmed.</summary>
    private void AutoReadConfig()
    {
        var selected = SelectedConfigNode?.NodeId;
        if (_lastReport != null && _lastReport.NodeId != selected)
        {
            // never show another ESC's report
            _lastReport = null;
            ConfigItems.Clear();
            RefreshCommands();
        }
        if (IsReading)
        {
            if (_readingNodeId != selected)
            {
                _readCts?.Cancel();
            }
            return;
        }
        if (SelectedConfigNode is not { } target || _autoReadNodeId == target.NodeId || IsWriting ||
            _link is not { Vehicle: not null, IsArmed: false })
        {
            return;
        }
        _autoReadNodeId = target.NodeId;
        _ = ReadConfigAsync(target);
    }

    private async Task ReadConfigAsync(NodeChoice target)
    {
        if (_reader == null)
        {
            return;
        }

        IsReading = true;
        _readingNodeId = target.NodeId;
        _readCts = new CancellationTokenSource();
        var progress = new Progress<string>(text => ConfigProgress = text);
        bool shown = false;
        try
        {
            AddLog($"Reading configuration of node {target.NodeId}…");
            var report = await _reader.ReadAsync(target.NodeId, IncludeParameters, progress, _readCts.Token);

            if (SelectedConfigNode?.NodeId != target.NodeId)
            {
                return;
            }
            shown = true;
            _lastReport = report;
            // replace only now, so a re-read keeps showing the previous values meanwhile
            ConfigItems.Clear();
            // errors only go to the log and the copied report
            foreach (var item in report.ToItems(includeErrors: false))
            {
                ConfigItems.Add(item);
            }
            AddLog($"Node {target.NodeId}: read finished, {report.Errors.Count} error(s), {report.Parameters.Count} parameter(s)");
            LoadSettingsFromNode();
        }
        catch (OperationCanceledException)
        {
            AddLog($"Node {target.NodeId}: read cancelled");
        }
        finally
        {
            if (!shown && _autoReadNodeId == target.NodeId)
            {
                // not shown (cancelled / selection moved away): read again when this node is selected
                _autoReadNodeId = null;
            }
            ConfigProgress = "";
            _readingNodeId = null;
            _readCts.Dispose();
            _readCts = null;
            IsReading = false;
        }
    }

    private async Task CopyReportAsync()
    {
        if (_lastReport == null)
        {
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"Hobbywing ESC node {_lastReport.NodeId} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var group in _lastReport.ToItems().GroupBy(i => i.Group))
        {
            sb.AppendLine($"[{group.Key}]");
            foreach (var item in group)
            {
                sb.AppendLine($"  {item.Name}: {item.Value}");
            }
        }
        await Clipboard.Default.SetTextAsync(sb.ToString());
        AddLog("Report copied to clipboard");
    }

    private void AddLog(string text)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }

    private void Post(Action action) => _dispatcher.Dispatch(action);
}
