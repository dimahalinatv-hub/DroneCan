using System.Globalization;
using System.Windows.Input;
using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Messages;
using HobbywingCan.Core.Monitoring;

namespace HobbywingCan.App.ViewModels;

public sealed record Option(byte Value, string Text)
{
    // Picker shows ToString()
    public override string ToString() => Text;
}

/// <summary>Writing Hobbywing ESC settings (throttle source, rates, direction, IDs, LED, CAN speed).</summary>
public sealed partial class MainViewModel
{
    private readonly HashSet<byte> _majorConfigRequested = new();
    private EscConfigWriter? _writer;
    private bool _isWriting;
    private bool _showSettings;
    private (HwMajorConfig?, byte, byte?, byte?, HwSetLed?)? _loadedSettings;
    private byte? _settingsNodeId;
    private string _settingsHint = "";

    private Option? _selectedThrottleSource;
    private Option? _selectedDirection;
    private Option? _selectedMsg1Rate;
    private Option? _selectedMsg2Rate;
    private Option? _selectedMsg3Rate;
    private string _throttleChannelText = "";
    private string _newNodeIdText = "";
    private Option? _selectedLedColor;
    private Option? _selectedLedBlink;
    private bool _ledSave = true;
    private Option? _selectedCanBaud;

    /// <summary>Asks the user to confirm a write; set by the page (title, message) → accepted.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Shows an important message the user must acknowledge (title, message); set by the page.</summary>
    public Func<string, string, Task>? Alert { get; set; }

    public IReadOnlyList<Option> ThrottleSourceOptions { get; } =
        [new(HwSetThrottleSource.Can, "CAN (digital)"), new(HwSetThrottleSource.Pwm, "PWM")];

    public IReadOnlyList<Option> DirectionOptions { get; } =
        [new(HwSetDirection.Clockwise, "CW"), new(HwSetDirection.CounterClockwise, "CCW")];

    public IReadOnlyList<Option> RateOptions { get; } =
        HwRate.Options.Select(o => new Option(o.Code, o.Hz == 0 ? "Off (0 Hz)" : $"{o.Hz} Hz")).ToList();

    public IReadOnlyList<Option> LedColorOptions { get; } = HwSetLed.Colors.Select(c => new Option(c.Color, c.Name)).ToList();

    public IReadOnlyList<Option> LedBlinkOptions { get; } = HwSetLed.BlinkModes.Select(b => new Option(b.Blink, b.Name)).ToList();

    public IReadOnlyList<Option> CanBaudOptions { get; } = HwSetBaud.Options.Select(o => new Option(o.Code, o.Name)).ToList();

    public ICommand ToggleViewCommand { get; private set; } = null!;
    public ICommand SetThrottleSourceCommand { get; private set; } = null!;
    public ICommand SetDirectionCommand { get; private set; } = null!;
    public ICommand SetRateCommand { get; private set; } = null!;
    public ICommand SetThrottleChannelCommand { get; private set; } = null!;
    public ICommand SetNodeIdCommand { get; private set; } = null!;
    public ICommand SetLedCommand { get; private set; } = null!;
    public ICommand SetCanBaudCommand { get; private set; } = null!;

    public bool ShowSettings
    {
        get => _showSettings;
        set
        {
            if (SetProperty(ref _showSettings, value))
            {
                OnPropertyChanged(nameof(ShowReport));
                OnPropertyChanged(nameof(ToggleViewText));
            }
        }
    }

    public bool ShowReport => !ShowSettings;

    /// <summary>The view the toggle button switches to.</summary>
    public string ToggleViewText => ShowSettings ? "Report" : "Edit";

    public bool IsWriting
    {
        get => _isWriting;
        private set
        {
            if (SetProperty(ref _isWriting, value))
            {
                RefreshCommands();
            }
        }
    }

    public Option? SelectedThrottleSource { get => _selectedThrottleSource; set => SetProperty(ref _selectedThrottleSource, value); }
    public Option? SelectedDirection { get => _selectedDirection; set => SetProperty(ref _selectedDirection, value); }
    public Option? SelectedMsg1Rate { get => _selectedMsg1Rate; set => SetProperty(ref _selectedMsg1Rate, value); }
    public Option? SelectedMsg2Rate { get => _selectedMsg2Rate; set => SetProperty(ref _selectedMsg2Rate, value); }
    public Option? SelectedMsg3Rate { get => _selectedMsg3Rate; set => SetProperty(ref _selectedMsg3Rate, value); }
    public string ThrottleChannelText { get => _throttleChannelText; set => SetProperty(ref _throttleChannelText, value); }
    public string NewNodeIdText { get => _newNodeIdText; set => SetProperty(ref _newNodeIdText, value); }
    public Option? SelectedLedColor { get => _selectedLedColor; set => SetProperty(ref _selectedLedColor, value); }
    public Option? SelectedLedBlink { get => _selectedLedBlink; set => SetProperty(ref _selectedLedBlink, value); }
    public bool LedSave { get => _ledSave; set => SetProperty(ref _ledSave, value); }
    public Option? SelectedCanBaud { get => _selectedCanBaud; set => SetProperty(ref _selectedCanBaud, value); }

    /// <summary>Which settings the ESC could not report; empty when all are known.</summary>
    public string SettingsHint
    {
        get => _settingsHint;
        private set
        {
            if (SetProperty(ref _settingsHint, value))
            {
                OnPropertyChanged(nameof(HasSettingsHint));
            }
        }
    }

    public bool HasSettingsHint => SettingsHint.Length > 0;

    private void InitSettings()
    {
        ToggleViewCommand = new Command(() =>
        {
            ShowSettings = !ShowSettings;
            if (ShowSettings)
            {
                LoadSettingsFromNode();
            }
        });

        SetThrottleSourceCommand = new Command(async () => await WriteAsync(
            SelectedThrottleSource, o => $"Set throttle source to {o.Text}",
            o => o.Value == HwSetThrottleSource.Pwm
                ? "With PWM the ESC ignores DroneCAN throttle from ArduPilot and only follows the PWM signal wire. The motor will not respond to the flight controller over CAN."
                : "The ESC will take throttle from DroneCAN (ArduPilot) again.",
            (w, node, o) => w.SetThrottleSourceAsync(node, o.Value)), CanWrite);

        SetDirectionCommand = new Command(async () => await WriteAsync(
            SelectedDirection, o => $"Set rotation direction to {o.Text}",
            _ => "The motor will spin the other way. Check the frame's motor order and propellers before flying.",
            (w, node, o) => w.SetDirectionAsync(node, o.Value)), CanWrite);

        SetRateCommand = new Command<string>(async msg => await SetRateAsync(msg), _ => CanWrite());

        SetThrottleChannelCommand = new Command(async () => await SetThrottleChannelAsync(), CanWrite);
        SetNodeIdCommand = new Command(async () => await SetNodeIdAsync(), CanWrite);

        SetLedCommand = new Command(async () => await WriteAsync(
            SelectedLedColor, o => $"Set LED to {o.Text}, {SelectedLedBlink?.Text ?? "steady"}{(LedSave ? " (saved)" : "")}",
            _ => "Only changes the ESC's status LED.",
            (w, node, o) => w.SetLedAsync(node, o.Value, SelectedLedBlink?.Value ?? 0, LedSave)), CanWrite);

        // no read exists, and after the change the ESC is off this bus until the FC runs at the same speed
        SetCanBaudCommand = new Command(async () => await WriteAsync(
            SelectedCanBaud, o => $"Set CAN speed to {o.Text}",
            o => $"The ESC will only talk at {o.Text}. Unless the flight controller's CAN_Px_BITRATE is set to the same speed, " +
                "the ESC drops off the bus: no throttle, no telemetry, and this app cannot reach it to change it back. " +
                "Some ESCs apply it only after a power cycle.",
            (w, node, o) => w.SetBaudAsync(node, o.Value),
            refreshAfter: false), CanWrite);
    }

    private bool CanWrite() => IsConnected && SelectedConfigNode != null && !IsReading && !IsWriting;

    private void RefreshSettingsCommands()
    {
        foreach (var c in new[] { SetThrottleSourceCommand, SetDirectionCommand, SetThrottleChannelCommand, SetNodeIdCommand, SetLedCommand, SetCanBaudCommand })
        {
            ((Command?)c)?.ChangeCanExecute();
        }
        ((Command<string>?)SetRateCommand)?.ChangeCanExecute();
    }

    /// <summary>Fill the settings editors with what is currently known about the selected ESC.</summary>
    private void LoadSettingsFromNode()
    {
        // null is only a moment while the Picker replaces the selected item; keep the fields then
        if (SelectedConfigNode is not { } selected)
        {
            return;
        }
        if (selected.NodeId != _settingsNodeId)
        {
            // never leave another ESC's values in fields this ESC has not reported
            _settingsNodeId = selected.NodeId;
            ClearSettingsEditors();
        }
        if (_monitor?.Get(selected.NodeId) is not { } state)
        {
            return;
        }
        // GetMajorConfig is the only read for these; some X8 G2 firmware does not answer it, then only a write tells
        if (state.ThrottleSourcePwm is { } pwm)
        {
            SelectedThrottleSource = Find(ThrottleSourceOptions, pwm ? HwSetThrottleSource.Pwm : HwSetThrottleSource.Can);
        }
        if (state.LedColor is { } color)
        {
            SelectedLedColor = Find(LedColorOptions, color) ?? SelectedLedColor;
        }
        if (state.LedBlink is { } blink)
        {
            SelectedLedBlink = Find(LedBlinkOptions, blink) ?? SelectedLedBlink;
        }
        var unknown = new List<string>();
        if (state.ThrottleSourcePwm == null)
        {
            unknown.Add("throttle source");
        }
        if (state.LedColor == null)
        {
            unknown.Add("LED");
        }
        SettingsHint = unknown.Count == 0
            ? ""
            : $"Current {string.Join(" and ", unknown)} unknown: the ESC has not answered GetMajorConfig. Shown after a Set.";
        if (state.CounterClockwise is { } ccw)
        {
            SelectedDirection = Find(DirectionOptions, ccw ? HwSetDirection.CounterClockwise : HwSetDirection.Clockwise);
        }
        SelectedMsg1Rate = RateFor(state, DataTypes.HwStatusMsg1.Id) ?? SelectedMsg1Rate;
        SelectedMsg2Rate = RateFor(state, DataTypes.HwStatusMsg2.Id) ?? SelectedMsg2Rate;
        SelectedMsg3Rate = RateFor(state, DataTypes.HwStatusMsg3.Id) ?? SelectedMsg3Rate;
        ThrottleChannelText = state.ThrottleChannel?.ToString(CultureInfo.InvariantCulture) ?? ThrottleChannelText;
        NewNodeIdText = state.NodeId.ToString(CultureInfo.InvariantCulture);
    }

    private void ClearSettingsEditors()
    {
        SelectedCanBaud = null;
        NewNodeIdText = "";
        ThrottleChannelText = "";
        SelectedThrottleSource = null;
        SelectedMsg1Rate = null;
        SelectedMsg2Rate = null;
        SelectedMsg3Rate = null;
        SelectedLedColor = null;
        SelectedLedBlink = null;
        SelectedDirection = null;
        SettingsHint = "";
    }

    private Option? RateFor(NodeState state, ushort messageId) =>
        state.ReportingRateCodes.TryGetValue(messageId, out var code) ? Find(RateOptions, code) : null;

    private static Option? Find(IEnumerable<Option> options, byte value) => options.FirstOrDefault(o => o.Value == value);

    /// <summary>Read GetMajorConfig once per Hobbywing ESC so the table can show throttle source and configured rates.</summary>
    private void RequestMajorConfigOnce(NodeState state)
    {
        if (!state.IsHobbywing || state.MajorConfig != null || _link is not { Vehicle: not null, IsArmed: false } || _node == null ||
            !_majorConfigRequested.Add(state.NodeId))
        {
            return;
        }
        var node = _node;
        _ = Task.Run(() => RefreshNodeSettingsAsync(node, state.NodeId));
    }

    /// <summary>Reload the editors when a new config for the selected ESC arrives (first auto-read, after a write).</summary>
    private void SyncSettingsEditors()
    {
        if (SelectedConfigNode == null || _monitor?.Get(SelectedConfigNode.NodeId) is not { } state)
        {
            return;
        }
        var snapshot = (state.MajorConfig, state.ReportingRateCodes.GetValueOrDefault(DataTypes.HwStatusMsg3.Id), state.DirectionSetting,
            state.ThrottleSourceSetting, state.LedSetting);
        if (!Equals(snapshot, _loadedSettings))
        {
            _loadedSettings = snapshot;
            LoadSettingsFromNode();
        }
    }

    /// <summary>Re-read the settings that GetMajorConfig does not cover; the monitor stores the responses.</summary>
    private static async Task RefreshNodeSettingsAsync(DroneCanNode node, byte nodeId)
    {
        var timeout = TimeSpan.FromMilliseconds(400);
        await node.RequestWithRetryAsync(DataTypes.HwGetMajorConfig, nodeId, HwMajorConfig.EncodeRequest(), timeout, 3);
        await node.RequestWithRetryAsync(DataTypes.HwSetReportingFrequency, nodeId,
            HwReportingFrequency.EncodeReadRequest(DataTypes.HwStatusMsg3.Id), timeout, 3);
        // DIRECTION_QUERY (255) only reads the setting back
        await node.RequestWithRetryAsync(DataTypes.HwSetDirection, nodeId, HwSetDirection.EncodeQuery(), timeout, 3);
    }

    private async Task SetRateAsync(string? message)
    {
        var (messageId, option, use) = message switch
        {
            "1" => (DataTypes.HwStatusMsg1.Id, SelectedMsg1Rate,
                "StatusMsg1 carries RPM. ArduPilot uses it for ESC RPM telemetry and the RPM-based harmonic notch; a low rate or Off degrades that."),
            "2" => (DataTypes.HwStatusMsg2.Id, SelectedMsg2Rate,
                "StatusMsg2 carries voltage, current and temperature used by ArduPilot's ESC telemetry."),
            _ => (DataTypes.HwStatusMsg3.Id, SelectedMsg3Rate,
                "StatusMsg3 carries MOSFET / capacitor / motor temperatures (ArduPilot does not use it)."),
        };
        await WriteAsync(option, o => $"Set StatusMsg{message} rate to {o.Text}", _ => use,
            (w, node, o) => w.SetReportingRateAsync(node, messageId, o.Value));
    }

    private async Task SetThrottleChannelAsync()
    {
        if (!byte.TryParse(ThrottleChannelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel) || channel is < 1 or > 63)
        {
            AddLog("Throttle channel must be 1..63");
            return;
        }
        var option = new Option(channel, channel.ToString(CultureInfo.InvariantCulture));
        await WriteAsync(option, o => $"Set throttle channel to {o.Text}",
            _ => "ArduPilot maps Hobbywing ESCs by throttle channel (= motor output number). A wrong channel makes this ESC follow another motor's throttle.",
            (w, node, o) => w.SetIdAsync(node, node, o.Value),
            setsId: true);
    }

    private async Task SetNodeIdAsync()
    {
        if (!byte.TryParse(NewNodeIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newId) || newId is < 1 or > 127)
        {
            AddLog("Node ID must be 1..127");
            return;
        }
        if (SelectedConfigNode is { } target && _monitor?.Get(target.NodeId)?.ThrottleChannel is not { } channel)
        {
            AddLog("Throttle channel of this ESC is unknown (enable Poll GetEscID or wait for the configuration read) — SetID needs it to keep it unchanged");
            return;
        }
        if (_monitor?.Get(newId) != null && newId != SelectedConfigNode?.NodeId && Confirm != null &&
            !await Confirm($"Node ID {newId} is already used on the bus. Continue?",
                "Two ESCs with the same node ID cannot be told apart: they show as one row and both take every request sent to that ID."))
        {
            AddLog($"Cancelled: node ID {newId} is already used on the bus");
            return;
        }
        var option = new Option(newId, newId.ToString(CultureInfo.InvariantCulture));
        await WriteAsync(option, o => $"Change node ID to {o.Text}",
            _ => "The ESC will answer on the new node ID and show up as a new row; the old row goes stale. Some ESCs need a power cycle.",
            (w, node, o) => w.SetIdAsync(node, o.Value, _monitor!.Get(node)!.ThrottleChannel!.Value),
            refreshAfter: false, setsId: true);
    }

    private bool EnsureDisarmed()
    {
        if (_link?.Vehicle == null)
        {
            AddLog("No heartbeat from the flight controller yet");
            return false;
        }
        if (_link.IsArmed)
        {
            AddLog("Vehicle is ARMED — settings changes are blocked for safety");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Hobbywing X8 G2 applies SetID to every ESC on the bus, whichever node it is addressed to.
    /// Returns a warning naming the other ESCs that would take node ID / throttle channel too, or null.
    /// </summary>
    private string? SetIdReachWarning(byte target)
    {
        var now = DateTime.UtcNow;
        var others = _monitor!.Nodes
            .Where(n => n.NodeId != target && n.IsHobbywing && now - n.LastSeenUtc < TimeSpan.FromSeconds(3))
            .Select(n => n.NodeId)
            .ToList();
        bool shared = _monitor.Get(target)?.DuplicateSuspected == true;
        if (others.Count == 0 && !shared)
        {
            return null;
        }
        string who = others.Count > 0
            ? $"Other ESCs on the bus: node {string.Join(", ", others)}."
            : $"Node {target} looks like several ESCs sharing one node ID.";
        return "Hobbywing X8 G2 applies SetID to EVERY ESC on the CAN bus, whichever node it is addressed to. " +
            $"{who} They will all get the same node ID and throttle channel.";
    }

    private Task ShowAlert(string title, string message) => Alert?.Invoke(title, message) ?? Task.CompletedTask;

    private async Task WriteAsync(Option? value, Func<Option, string> describe, Func<Option, string> warning,
        Func<EscConfigWriter, byte, Option, Task<WriteResult>> write, bool refreshAfter = true, bool setsId = false)
    {
        if (_writer == null || _node == null || _monitor == null || SelectedConfigNode is not { } target)
        {
            return;
        }
        if (value == null)
        {
            AddLog("Choose a value first");
            return;
        }
        if (!EnsureDisarmed())
        {
            return;
        }

        IsWriting = true;
        try
        {
            // harmless read first: if anyone but the target answers, a write would change them too
            var probe = await _writer.ProbeAddressingAsync(target.NodeId);
            string? unverified = null;
            if (probe.Inconclusive)
            {
                // not every firmware answers the read requests; that must not stop the write
                unverified = $"Could not check which ESCs this reaches: node {target.NodeId} did not answer any read request " +
                    "(rate read, GetMajorConfig, GetNodeInfo).";
                AddLog(unverified);
            }

            // writing to several ESCs at once is allowed, but only knowingly
            var reach = new List<string>();
            if (setsId && SetIdReachWarning(target.NodeId) is { } idWarning)
            {
                reach.Add(idWarning);
            }
            if (probe.DuplicateNodeId)
            {
                reach.Add($"More than one ESC answers as node {target.NodeId} (shared node ID) — all of them will be changed.");
            }
            if (probe.OtherResponders.Count > 0)
            {
                reach.Add($"Node(s) {string.Join(", ", probe.OtherResponders)} also answer requests addressed to node {target.NodeId} — they will be changed too.");
            }

            string title = reach.Count == 0
                ? $"{describe(value)} on node {target.NodeId}?"
                : $"{describe(value)} — this will affect SEVERAL ESCs. Continue?";
            var notes = reach.Select(r => $"⚠ {r}").ToList();
            if (unverified != null)
            {
                notes.Add($"ℹ {unverified}");
            }
            string message = notes.Count == 0
                ? warning(value)
                : $"{string.Join("\n\n", notes)}\n\n{warning(value)}";
            if (Confirm != null && !await Confirm(title, message))
            {
                AddLog($"Cancelled: {describe(value)}");
                return;
            }
            if (reach.Count > 0)
            {
                AddLog($"Writing to several ESCs on purpose: {string.Join(" ", reach)}");
            }

            var result = await write(_writer, target.NodeId, value);
            AddLog(result.Message);
            if (result.AffectedOthers && reach.Count == 0)
            {
                // nothing suggested this beforehand, so make sure it is noticed
                await ShowAlert("Other ESCs changed too",
                    $"{result.Message}\n\nCheck every ESC in the table (Report) before flying.");
            }
            if (result.Success && refreshAfter)
            {
                await RefreshNodeSettingsAsync(_node, target.NodeId);
                LoadSettingsFromNode();
            }
            if (result.Success)
            {
                // the report shows the old values now; read it again
                _autoReadNodeId = null;
            }
        }
        finally
        {
            IsWriting = false;
        }
    }
}
