using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HobbywingCan.Core.Messages;
using HobbywingCan.Core.Presets;

namespace HobbywingCan.App.ViewModels;

/// <summary>The values edited in the presets popup, and which of them belong to the preset.</summary>
public sealed class PresetDraft : ObservableObject
{
    private Option? _canBaud;
    private string _nodeIdText = "";
    private string _throttleChannelText = "";
    private Option? _throttleSource;
    private Option? _msg1Rate;
    private Option? _msg2Rate;
    private Option? _msg3Rate;
    private Option? _ledColor;
    private Option? _ledBlink;
    private bool _ledSave = true;
    private Option? _direction;
    // what identifies one ESC, or can cut it off the bus, is only taken when ticked
    private bool _includeCanBaud;
    private bool _includeNodeId;
    private bool _includeThrottleChannel;
    private bool _includeThrottleSource = true;
    private bool _includeMsg1Rate = true;
    private bool _includeMsg2Rate = true;
    private bool _includeMsg3Rate = true;
    private bool _includeLed = true;
    private bool _includeDirection = true;

    public Option? CanBaud { get => _canBaud; set => SetProperty(ref _canBaud, value); }
    public string NodeIdText { get => _nodeIdText; set => SetProperty(ref _nodeIdText, value); }
    public string ThrottleChannelText { get => _throttleChannelText; set => SetProperty(ref _throttleChannelText, value); }
    public Option? ThrottleSource { get => _throttleSource; set => SetProperty(ref _throttleSource, value); }
    public Option? Msg1Rate { get => _msg1Rate; set => SetProperty(ref _msg1Rate, value); }
    public Option? Msg2Rate { get => _msg2Rate; set => SetProperty(ref _msg2Rate, value); }
    public Option? Msg3Rate { get => _msg3Rate; set => SetProperty(ref _msg3Rate, value); }
    public Option? LedColor { get => _ledColor; set => SetProperty(ref _ledColor, value); }
    public Option? LedBlink { get => _ledBlink; set => SetProperty(ref _ledBlink, value); }
    public bool LedSave { get => _ledSave; set => SetProperty(ref _ledSave, value); }
    public Option? Direction { get => _direction; set => SetProperty(ref _direction, value); }

    public bool IncludeCanBaud { get => _includeCanBaud; set => SetProperty(ref _includeCanBaud, value); }
    public bool IncludeNodeId { get => _includeNodeId; set => SetProperty(ref _includeNodeId, value); }
    public bool IncludeThrottleChannel { get => _includeThrottleChannel; set => SetProperty(ref _includeThrottleChannel, value); }
    public bool IncludeThrottleSource { get => _includeThrottleSource; set => SetProperty(ref _includeThrottleSource, value); }
    public bool IncludeMsg1Rate { get => _includeMsg1Rate; set => SetProperty(ref _includeMsg1Rate, value); }
    public bool IncludeMsg2Rate { get => _includeMsg2Rate; set => SetProperty(ref _includeMsg2Rate, value); }
    public bool IncludeMsg3Rate { get => _includeMsg3Rate; set => SetProperty(ref _includeMsg3Rate, value); }
    public bool IncludeLed { get => _includeLed; set => SetProperty(ref _includeLed, value); }
    public bool IncludeDirection { get => _includeDirection; set => SetProperty(ref _includeDirection, value); }
}

/// <summary>Named sets of ESC settings: edited in a popup, saved, written to an ESC in one go.</summary>
public sealed partial class MainViewModel
{
    private PresetStore _presetStore = null!;
    private SettingsPreset? _selectedPreset;
    private string _presetName = "";
    private bool _showPresets;

    /// <summary>Asks the user to confirm something other than a write (title, message, accept button) → accepted; set by the page.</summary>
    public Func<string, string, string, Task<bool>>? Ask { get; set; }

    public ObservableCollection<SettingsPreset> Presets { get; } = new();

    public PresetDraft Draft { get; } = new();

    public ICommand OpenPresetsCommand { get; private set; } = null!;
    public ICommand ClosePresetsCommand { get; private set; } = null!;
    public ICommand DraftFromEditorCommand { get; private set; } = null!;
    public ICommand SavePresetCommand { get; private set; } = null!;
    public ICommand ApplyPresetCommand { get; private set; } = null!;
    public ICommand DeletePresetCommand { get; private set; } = null!;

    public bool ShowPresets { get => _showPresets; set => SetProperty(ref _showPresets, value); }

    public string PresetName { get => _presetName; set => SetProperty(ref _presetName, value); }

    /// <summary>The saved preset chosen in the popup; choosing one loads it into <see cref="Draft"/>.</summary>
    public SettingsPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value))
            {
                return;
            }
            if (value != null)
            {
                PresetName = value.Name;
                LoadDraft(value);
            }
            ((Command)DeletePresetCommand).ChangeCanExecute();
        }
    }

    private void InitPresets()
    {
        _presetStore = new PresetStore(Path.Combine(FileSystem.AppDataDirectory, "presets.json"));
        var saved = _presetStore.Load(out var error);
        foreach (var preset in saved.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Presets.Add(preset);
        }
        if (error != null)
        {
            AddLog(error);
        }

        OpenPresetsCommand = new Command(() =>
        {
            // a fresh popup starts from the editor; a chosen preset stays as it was left
            if (SelectedPreset == null)
            {
                DraftFromEditor();
            }
            ShowPresets = true;
        });
        ClosePresetsCommand = new Command(() => ShowPresets = false);
        DraftFromEditorCommand = new Command(DraftFromEditor);
        SavePresetCommand = new Command(async () => await SavePresetAsync());
        ApplyPresetCommand = new Command(async () => await ApplyDraftAsync(), CanWrite);
        DeletePresetCommand = new Command(async () => await DeletePresetAsync(), () => SelectedPreset != null);
    }

    /// <summary>Copy the values of the settings editor into the popup; which fields are ticked stays.</summary>
    private void DraftFromEditor()
    {
        Draft.CanBaud = SelectedCanBaud;
        Draft.NodeIdText = NewNodeIdText;
        Draft.ThrottleChannelText = ThrottleChannelText;
        Draft.ThrottleSource = SelectedThrottleSource;
        Draft.Msg1Rate = SelectedMsg1Rate;
        Draft.Msg2Rate = SelectedMsg2Rate;
        Draft.Msg3Rate = SelectedMsg3Rate;
        Draft.LedColor = SelectedLedColor;
        Draft.LedBlink = SelectedLedBlink;
        Draft.LedSave = LedSave;
        Draft.Direction = SelectedDirection;
    }

    private void LoadDraft(SettingsPreset p)
    {
        Draft.CanBaud = Opt(CanBaudOptions, p.CanBaud);
        Draft.NodeIdText = p.NodeId?.ToString(CultureInfo.InvariantCulture) ?? "";
        Draft.ThrottleChannelText = p.ThrottleChannel?.ToString(CultureInfo.InvariantCulture) ?? "";
        Draft.ThrottleSource = Opt(ThrottleSourceOptions, p.ThrottleSource);
        Draft.Msg1Rate = Opt(RateOptions, p.Msg1Rate);
        Draft.Msg2Rate = Opt(RateOptions, p.Msg2Rate);
        Draft.Msg3Rate = Opt(RateOptions, p.Msg3Rate);
        Draft.LedColor = Opt(LedColorOptions, p.LedColor);
        Draft.LedBlink = Opt(LedBlinkOptions, p.LedBlink);
        Draft.LedSave = p.LedSave;
        Draft.Direction = Opt(DirectionOptions, p.Direction);
        Draft.IncludeCanBaud = p.CanBaud != null;
        Draft.IncludeNodeId = p.NodeId != null;
        Draft.IncludeThrottleChannel = p.ThrottleChannel != null;
        Draft.IncludeThrottleSource = p.ThrottleSource != null;
        Draft.IncludeMsg1Rate = p.Msg1Rate != null;
        Draft.IncludeMsg2Rate = p.Msg2Rate != null;
        Draft.IncludeMsg3Rate = p.Msg3Rate != null;
        Draft.IncludeLed = p.LedColor != null;
        Draft.IncludeDirection = p.Direction != null;
    }

    /// <summary>The ticked popup fields that have a value; null (and logged) when a number is invalid or nothing is left.</summary>
    /// <param name="empty">ticked fields left out because they have no value</param>
    private SettingsPreset? PresetFromDraft(string name, out List<string> empty)
    {
        var d = Draft;
        var missing = new List<string>();
        empty = missing;
        void Check(bool include, object? value, string title)
        {
            if (include && value is null or "")
            {
                missing.Add(title);
            }
        }
        Check(d.IncludeNodeId, d.NodeIdText.Trim(), "Node ID");
        Check(d.IncludeCanBaud, d.CanBaud, "CAN speed");
        Check(d.IncludeThrottleChannel, d.ThrottleChannelText.Trim(), "Throttle channel");
        Check(d.IncludeThrottleSource, d.ThrottleSource, "Throttle source");
        Check(d.IncludeMsg1Rate, d.Msg1Rate, "StatusMsg1 rate");
        Check(d.IncludeMsg2Rate, d.Msg2Rate, "StatusMsg2 rate");
        Check(d.IncludeMsg3Rate, d.Msg3Rate, "StatusMsg3 rate");
        Check(d.IncludeLed, d.LedColor, "LED");
        Check(d.IncludeDirection, d.Direction, "Direction");

        byte? nodeId = null;
        if (d.IncludeNodeId && d.NodeIdText.Trim().Length > 0)
        {
            if (!byte.TryParse(d.NodeIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id is < 1 or > 127)
            {
                AddLog("Preset: node ID must be 1..127");
                return null;
            }
            nodeId = id;
        }
        byte? throttleChannel = null;
        if (d.IncludeThrottleChannel && d.ThrottleChannelText.Trim().Length > 0)
        {
            if (!byte.TryParse(d.ThrottleChannelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel) || channel is < 1 or > 63)
            {
                AddLog("Preset: throttle channel must be 1..63");
                return null;
            }
            throttleChannel = channel;
        }
        bool led = d.IncludeLed && d.LedColor != null;
        var preset = new SettingsPreset(name,
            CanBaud: d.IncludeCanBaud ? d.CanBaud?.Value : null,
            NodeId: nodeId,
            ThrottleChannel: throttleChannel,
            ThrottleSource: d.IncludeThrottleSource ? d.ThrottleSource?.Value : null,
            Msg1Rate: d.IncludeMsg1Rate ? d.Msg1Rate?.Value : null,
            Msg2Rate: d.IncludeMsg2Rate ? d.Msg2Rate?.Value : null,
            Msg3Rate: d.IncludeMsg3Rate ? d.Msg3Rate?.Value : null,
            LedColor: led ? d.LedColor!.Value : null,
            LedBlink: led ? d.LedBlink?.Value ?? 0 : null,
            LedSave: d.LedSave,
            Direction: d.IncludeDirection ? d.Direction?.Value : null);
        if (preset == new SettingsPreset(name, LedSave: d.LedSave))
        {
            AddLog("Preset is empty: tick at least one field and give it a value");
            return null;
        }
        return preset;
    }

    private string PresetText(SettingsPreset p, string separator)
    {
        var parts = new List<string>();
        void Add(string title, Option? option)
        {
            if (option != null)
            {
                parts.Add($"{title}: {option.Text}");
            }
        }
        if (p.NodeId is { } id)
        {
            parts.Add($"Node ID: {id}");
        }
        Add("CAN speed", Opt(CanBaudOptions, p.CanBaud));
        if (p.ThrottleChannel is { } channel)
        {
            parts.Add($"Throttle channel: {channel}");
        }
        Add("Throttle source", Opt(ThrottleSourceOptions, p.ThrottleSource));
        Add("StatusMsg1 rate", Opt(RateOptions, p.Msg1Rate));
        Add("StatusMsg2 rate", Opt(RateOptions, p.Msg2Rate));
        Add("StatusMsg3 rate", Opt(RateOptions, p.Msg3Rate));
        if (Opt(LedColorOptions, p.LedColor) is { } color)
        {
            parts.Add($"LED: {LedText(color, Opt(LedBlinkOptions, p.LedBlink), p.LedSave)}");
        }
        Add("Direction", Opt(DirectionOptions, p.Direction));
        return string.Join(separator, parts);
    }

    private static Option? Opt(IEnumerable<Option> options, byte? value) => value is { } v ? Find(options, v) : null;

    private async Task SavePresetAsync()
    {
        string name = PresetName.Trim();
        if (name.Length == 0)
        {
            AddLog("Enter a preset name");
            return;
        }
        if (PresetFromDraft(name, out var empty) is not { } preset)
        {
            return;
        }

        int index = Presets.ToList().FindIndex(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (index >= 0)
        {
            if (Ask != null && !await Ask($"Replace preset \"{Presets[index].Name}\"?", PresetText(Presets[index], "\n"), "Replace"))
            {
                return;
            }
            Presets[index] = preset;
        }
        else
        {
            Presets.Insert(Presets.TakeWhile(p => StringComparer.CurrentCultureIgnoreCase.Compare(p.Name, name) < 0).Count(), preset);
        }
        SelectedPreset = preset;
        if (SavePresets())
        {
            AddLog($"Preset \"{name}\" saved: {PresetText(preset, "; ")}" +
                (empty.Count > 0 ? $". Not saved, no value: {string.Join(", ", empty)}" : ""));
        }
    }

    private async Task DeletePresetAsync()
    {
        if (SelectedPreset is not { } preset ||
            Ask != null && !await Ask($"Delete preset \"{preset.Name}\"?", PresetText(preset, "\n"), "Delete"))
        {
            return;
        }
        Presets.Remove(preset);
        SelectedPreset = null;
        PresetName = "";
        if (SavePresets())
        {
            AddLog($"Preset \"{preset.Name}\" deleted");
        }
    }

    private bool SavePresets()
    {
        try
        {
            _presetStore.Save(Presets);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog($"Cannot save presets to {_presetStore.Path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes the popup's values as they are now, saved or not.</summary>
    private async Task ApplyDraftAsync()
    {
        string name = PresetName.Trim();
        if (PresetFromDraft(name.Length > 0 ? name : "unsaved", out var empty) is not { } preset)
        {
            return;
        }
        if (empty.Count > 0)
        {
            AddLog($"Preset \"{preset.Name}\": no value, not written: {string.Join(", ", empty)}");
        }
        await ApplyPresetAsync(preset);
    }

    /// <summary>
    /// Puts the preset into the settings editors and writes every field the ESC does not already report
    /// with that value, after one confirmation.
    /// </summary>
    private async Task ApplyPresetAsync(SettingsPreset preset)
    {
        if (SelectedConfigNode is not { } target)
        {
            return;
        }
        var state = _monitor?.Get(target.NodeId);
        // the editors show what is written; each field turns green once it is
        ShowPresets = false;
        if (!ShowSettings)
        {
            ShowSettings = true;
            LoadSettingsFromNode();
        }

        var steps = new List<WriteStep>();
        var same = new List<string>();

        if (Opt(ThrottleSourceOptions, preset.ThrottleSource) is { } source)
        {
            SelectedThrottleSource = source;
            if (state?.ThrottleSourcePwm is { } pwm && (pwm ? HwSetThrottleSource.Pwm : HwSetThrottleSource.Can) == source.Value)
            {
                same.Add("throttle source");
            }
            else
            {
                steps.Add(new([nameof(SelectedThrottleSource)], $"Set throttle source to {source.Text}", ThrottleSourceWarning(source.Value),
                    (w, node) => w.SetThrottleSourceAsync(node, source.Value)));
            }
        }

        foreach (var (message, code) in new[] { ("1", preset.Msg1Rate), ("2", preset.Msg2Rate), ("3", preset.Msg3Rate) })
        {
            if (Opt(RateOptions, code) is not { } rate)
            {
                continue;
            }
            var (messageId, field, use) = RateInfo(message);
            switch (message)
            {
                case "1": SelectedMsg1Rate = rate; break;
                case "2": SelectedMsg2Rate = rate; break;
                default: SelectedMsg3Rate = rate; break;
            }
            if (state != null && state.ReportingRateCodes.TryGetValue(messageId, out var current) && current == rate.Value)
            {
                same.Add($"StatusMsg{message} rate");
            }
            else
            {
                steps.Add(new([field], $"Set StatusMsg{message} rate to {rate.Text}", use,
                    (w, node) => w.SetReportingRateAsync(node, messageId, rate.Value)));
            }
        }

        if (Opt(LedColorOptions, preset.LedColor) is { } color)
        {
            var blink = Opt(LedBlinkOptions, preset.LedBlink);
            SelectedLedColor = color;
            SelectedLedBlink = blink;
            LedSave = preset.LedSave;
            if (state?.LedColor == color.Value && state.LedBlink == (blink?.Value ?? 0))
            {
                same.Add("LED");
            }
            else
            {
                steps.Add(new([nameof(SelectedLedColor), nameof(SelectedLedBlink)], $"Set LED to {LedText(color, blink, preset.LedSave)}", LedWarning,
                    (w, node) => w.SetLedAsync(node, color.Value, blink?.Value ?? 0, preset.LedSave)));
            }
        }

        if (Opt(DirectionOptions, preset.Direction) is { } direction)
        {
            SelectedDirection = direction;
            if (state?.CounterClockwise is { } ccw && (ccw ? HwSetDirection.CounterClockwise : HwSetDirection.Clockwise) == direction.Value)
            {
                same.Add("direction");
            }
            else
            {
                steps.Add(new([nameof(SelectedDirection)], $"Set rotation direction to {direction.Text}", DirectionWarning,
                    (w, node) => w.SetDirectionAsync(node, direction.Value)));
            }
        }

        // the ESC's identity last of the settings: throttle channel, then node ID, which moves the ESC to another node;
        // SetID always carries both, so each write keeps the other one as it is by then; only CAN speed comes after
        if (preset.NodeId != null || preset.ThrottleChannel != null)
        {
            byte newId = preset.NodeId ?? target.NodeId;
            if (preset.NodeId is { } id)
            {
                NewNodeIdText = id.ToString(CultureInfo.InvariantCulture);
            }
            if (preset.ThrottleChannel is { } presetChannel)
            {
                ThrottleChannelText = presetChannel.ToString(CultureInfo.InvariantCulture);
            }
            if ((preset.ThrottleChannel ?? state?.ThrottleChannel) is not { } channel)
            {
                AddLog("Throttle channel of this ESC is unknown (enable Poll GetEscID or wait for the configuration read) — SetID needs it to keep it unchanged");
                return;
            }
            if (preset.ThrottleChannel != null)
            {
                if (channel == state?.ThrottleChannel)
                {
                    same.Add("throttle channel");
                }
                else
                {
                    // the node ID stays, so the ESC is still on the same node for the next step
                    steps.Add(new([nameof(ThrottleChannelText)], $"Set throttle channel to {channel}", ThrottleChannelWarning,
                        (w, node) => w.SetIdAsync(node, node, channel), SetsId: true));
                }
            }
            if (preset.NodeId != null)
            {
                if (newId == target.NodeId)
                {
                    same.Add("node ID");
                }
                else
                {
                    if (!await ConfirmNodeIdFreeAsync(newId))
                    {
                        return;
                    }
                    steps.Add(new([nameof(NewNodeIdText)], $"Change node ID to {newId}", NodeIdWarning,
                        (w, node) => w.SetIdAsync(node, newId, channel),
                        RefreshAfter: false, SetsId: true, NewNodeId: newId));
                }
            }
        }

        // cannot be read back, so always written; last, since the ESC may leave the bus with it
        if (Opt(CanBaudOptions, preset.CanBaud) is { } baud)
        {
            SelectedCanBaud = baud;
            steps.Add(new([nameof(SelectedCanBaud)], $"Set CAN speed to {baud.Text}", CanBaudWarning(baud),
                (w, node) => w.SetBaudAsync(node, baud.Value), RefreshAfter: false));
        }

        if (same.Count > 0)
        {
            AddLog($"Preset \"{preset.Name}\": node {target.NodeId} already has the {string.Join(", ", same)} — not written again");
        }
        if (steps.Count == 0)
        {
            AddLog($"Preset \"{preset.Name}\": nothing to write");
            return;
        }
        await WriteStepsAsync($"Apply preset \"{preset.Name}\" ({steps.Count} write{(steps.Count == 1 ? "" : "s")})", steps);
    }
}
