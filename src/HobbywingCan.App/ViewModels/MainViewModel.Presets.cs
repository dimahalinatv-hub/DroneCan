using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HobbywingCan.Core.Messages;
using HobbywingCan.Core.Presets;

namespace HobbywingCan.App.ViewModels;

/// <summary>A settings field in the presets popup: whether Save takes it, and what the editor holds now.</summary>
public sealed class PresetFieldRow(string field, string title, bool include) : ObservableObject
{
    private bool _include = include;
    private string _currentText = "";

    /// <summary>Name of the settings editor property.</summary>
    public string Field { get; } = field;
    public string Title { get; } = title;
    public bool Include { get => _include; set => SetProperty(ref _include, value); }
    public string CurrentText { get => _currentText; set => SetProperty(ref _currentText, value); }
}

/// <summary>Named sets of ESC settings: saved from the settings editors, written to an ESC in one go.</summary>
public sealed partial class MainViewModel
{
    private PresetStore _presetStore = null!;
    private SettingsPreset? _selectedPreset;
    private string _presetName = "";
    private bool _showPresets;

    /// <summary>Asks the user to confirm something other than a write (title, message, accept button) → accepted; set by the page.</summary>
    public Func<string, string, string, Task<bool>>? Ask { get; set; }

    public ObservableCollection<SettingsPreset> Presets { get; } = new();

    // what identifies one ESC, or can cut it off the bus, is only taken when ticked
    public IReadOnlyList<PresetFieldRow> PresetFields { get; } =
    [
        new(nameof(SelectedCanBaud), "CAN speed", false),
        new(nameof(NewNodeIdText), "Node ID", false),
        new(nameof(ThrottleChannelText), "Throttle channel", false),
        new(nameof(SelectedThrottleSource), "Throttle source", true),
        new(nameof(SelectedMsg1Rate), "StatusMsg1 rate", true),
        new(nameof(SelectedMsg2Rate), "StatusMsg2 rate", true),
        new(nameof(SelectedMsg3Rate), "StatusMsg3 rate", true),
        new(nameof(SelectedLedColor), "LED", true),
        new(nameof(SelectedDirection), "Direction", true),
    ];

    public ICommand OpenPresetsCommand { get; private set; } = null!;
    public ICommand ClosePresetsCommand { get; private set; } = null!;
    public ICommand SavePresetCommand { get; private set; } = null!;
    public ICommand ApplyPresetCommand { get; private set; } = null!;
    public ICommand DeletePresetCommand { get; private set; } = null!;

    public bool ShowPresets { get => _showPresets; set => SetProperty(ref _showPresets, value); }

    public string PresetName { get => _presetName; set => SetProperty(ref _presetName, value); }

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
                // so saving under the same name again keeps the same fields
                PresetName = value.Name;
                foreach (var row in PresetFields)
                {
                    row.Include = PresetHas(value, row.Field);
                }
            }
            OnPropertyChanged(nameof(PresetSummary));
            ((Command)ApplyPresetCommand).ChangeCanExecute();
            ((Command)DeletePresetCommand).ChangeCanExecute();
        }
    }

    public string PresetSummary => SelectedPreset is { } p ? PresetText(p, "\n") : "No preset selected";

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
            RefreshPresetFields();
            ShowPresets = true;
        });
        ClosePresetsCommand = new Command(() => ShowPresets = false);
        SavePresetCommand = new Command(async () => await SavePresetAsync());
        ApplyPresetCommand = new Command(async () => await ApplyPresetAsync(), () => CanWrite() && SelectedPreset != null);
        DeletePresetCommand = new Command(async () => await DeletePresetAsync(), () => SelectedPreset != null);
        RefreshPresetFields();
    }

    private void RefreshPresetFields()
    {
        foreach (var row in PresetFields)
        {
            string text = row.Field == nameof(SelectedLedColor)
                ? SelectedLedColor is { } color ? LedText(color, SelectedLedBlink, LedSave) : ""
                : FieldValue(row.Field)?.ToString() ?? "";
            row.CurrentText = text.Length == 0 ? "–" : text;
        }
    }

    private static bool PresetHas(SettingsPreset p, string field) => field switch
    {
        nameof(SelectedCanBaud) => p.CanBaud != null,
        nameof(NewNodeIdText) => p.NodeId != null,
        nameof(ThrottleChannelText) => p.ThrottleChannel != null,
        nameof(SelectedThrottleSource) => p.ThrottleSource != null,
        nameof(SelectedMsg1Rate) => p.Msg1Rate != null,
        nameof(SelectedMsg2Rate) => p.Msg2Rate != null,
        nameof(SelectedMsg3Rate) => p.Msg3Rate != null,
        nameof(SelectedLedColor) => p.LedColor != null,
        nameof(SelectedDirection) => p.Direction != null,
        _ => false,
    };

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
        Add("CAN speed", Opt(CanBaudOptions, p.CanBaud));
        if (p.NodeId is { } id)
        {
            parts.Add($"Node ID: {id}");
        }
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
        bool Take(string field) => PresetFields.First(r => r.Field == field).Include;

        byte? nodeId = null;
        if (Take(nameof(NewNodeIdText)) && NewNodeIdText.Trim().Length > 0)
        {
            if (!byte.TryParse(NewNodeIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id is < 1 or > 127)
            {
                AddLog("Node ID must be 1..127");
                return;
            }
            nodeId = id;
        }
        byte? throttleChannel = null;
        if (Take(nameof(ThrottleChannelText)) && ThrottleChannelText.Trim().Length > 0)
        {
            if (!byte.TryParse(ThrottleChannelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel) || channel is < 1 or > 63)
            {
                AddLog("Throttle channel must be 1..63");
                return;
            }
            throttleChannel = channel;
        }
        bool led = Take(nameof(SelectedLedColor)) && SelectedLedColor != null;
        var preset = new SettingsPreset(name,
            CanBaud: Take(nameof(SelectedCanBaud)) ? SelectedCanBaud?.Value : null,
            NodeId: nodeId,
            ThrottleChannel: throttleChannel,
            ThrottleSource: Take(nameof(SelectedThrottleSource)) ? SelectedThrottleSource?.Value : null,
            Msg1Rate: Take(nameof(SelectedMsg1Rate)) ? SelectedMsg1Rate?.Value : null,
            Msg2Rate: Take(nameof(SelectedMsg2Rate)) ? SelectedMsg2Rate?.Value : null,
            Msg3Rate: Take(nameof(SelectedMsg3Rate)) ? SelectedMsg3Rate?.Value : null,
            LedColor: led ? SelectedLedColor!.Value : null,
            LedBlink: led ? SelectedLedBlink?.Value ?? 0 : null,
            LedSave: LedSave,
            Direction: Take(nameof(SelectedDirection)) ? SelectedDirection?.Value : null);

        var empty = PresetFields.Where(r => r.Include && !PresetHas(preset, r.Field)).Select(r => r.Title).ToList();
        if (PresetFields.All(r => !PresetHas(preset, r.Field)))
        {
            AddLog("Nothing to save: tick at least one field that has a value");
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

    /// <summary>
    /// Puts the preset into the settings editors and writes every field the ESC does not already report
    /// with that value, after one confirmation.
    /// </summary>
    private async Task ApplyPresetAsync()
    {
        if (SelectedPreset is not { } preset || SelectedConfigNode is not { } target)
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

        // SetID carries both, so one request; it moves the ESC to the new node ID, so only CAN speed comes after it
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
            bool idChanges = newId != target.NodeId;
            bool channelChanges = channel != state?.ThrottleChannel;
            if (!idChanges && !channelChanges)
            {
                same.Add(preset.NodeId != null && preset.ThrottleChannel != null ? "node ID and throttle channel"
                    : preset.NodeId != null ? "node ID" : "throttle channel");
            }
            else
            {
                if (idChanges && !await ConfirmNodeIdFreeAsync(newId))
                {
                    return;
                }
                var fields = new List<string>();
                var warnings = new List<string>();
                if (idChanges)
                {
                    fields.Add(nameof(NewNodeIdText));
                    warnings.Add(NodeIdWarning);
                }
                if (channelChanges)
                {
                    fields.Add(nameof(ThrottleChannelText));
                    warnings.Add(ThrottleChannelWarning);
                }
                steps.Add(new(fields.ToArray(), $"Set node ID {newId}, throttle channel {channel}", string.Join(" ", warnings),
                    (w, node) => w.SetIdAsync(node, newId, channel),
                    RefreshAfter: !idChanges, SetsId: true, NewNodeId: idChanges ? newId : null));
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
