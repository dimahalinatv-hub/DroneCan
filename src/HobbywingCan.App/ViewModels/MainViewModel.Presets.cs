using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HobbywingCan.Core.DroneCan;
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

/// <summary>What the selected ESC reports for each presets popup row, bound as <c>PresetCurrent[DraftProperty]</c>; N/A when unknown.</summary>
public sealed class PresetCurrentValues : ObservableObject
{
    private Dictionary<string, object?> _values = new();

    public string this[string field] => _values.GetValueOrDefault(field) switch
    {
        null => "N/A",
        Option option => option.Text,
        var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "N/A",
    };

    /// <summary>The reported value (an <see cref="Option"/> or a number); null when unknown.</summary>
    public object? Value(string field) => _values.GetValueOrDefault(field);

    public void Set(Dictionary<string, object?> values)
    {
        if (_values.Count == values.Count && _values.All(kv => Equals(kv.Value, values.GetValueOrDefault(kv.Key))))
        {
            return;
        }
        _values = values;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// Highlight of each presets popup row, bound as <c>DraftMarks[DraftProperty]</c>:
/// yellow = the preset differs from what the ESC reports, green = written by the last Apply.
/// </summary>
public sealed class PresetMarks : ObservableObject
{
    private readonly Func<string, bool> _differs;
    private readonly HashSet<string> _written = new();

    public PresetMarks(Func<string, bool> differs) => _differs = differs;

    public Color this[string field] =>
        _differs(field) ? FieldMarks.Edited
        : _written.Contains(field) ? FieldMarks.Written
        : Colors.Transparent;

    public void MarkWritten(string field)
    {
        _written.Add(field);
        Changed();
    }

    public void Clear()
    {
        _written.Clear();
        Changed();
    }

    // empty name = every indexer binding re-reads
    public void Changed() => OnPropertyChanged(string.Empty);
}

/// <summary>Named sets of ESC settings: edited in a popup, saved, written to an ESC in one go.</summary>
public sealed partial class MainViewModel
{
    private PresetStore _presetStore = null!;
    private SettingsPreset? _selectedPreset;
    private string _presetName = "";
    private bool _showPresets;
    private string _presetChanges = "";
    private PresetMarks? _draftMarks;
    // the node the green marks were written to; another node chosen clears them
    private byte? _presetWrittenNode;

    /// <summary>Asks the user to confirm something other than a write (title, message, accept button) → accepted; set by the page.</summary>
    public Func<string, string, string, Task<bool>>? Ask { get; set; }

    public ObservableCollection<SettingsPreset> Presets { get; } = new();

    public PresetDraft Draft { get; } = new();

    public PresetCurrentValues PresetCurrent { get; } = new();

    public PresetMarks DraftMarks => _draftMarks ??= new PresetMarks(DraftDiffers);

    public ICommand OpenPresetsCommand { get; private set; } = null!;
    public ICommand ClosePresetsCommand { get; private set; } = null!;
    public ICommand DraftFromEditorCommand { get; private set; } = null!;
    public ICommand SavePresetCommand { get; private set; } = null!;
    public ICommand ApplyPresetCommand { get; private set; } = null!;
    public ICommand DeletePresetCommand { get; private set; } = null!;

    public bool ShowPresets { get => _showPresets; set => SetProperty(ref _showPresets, value); }

    public string PresetName { get => _presetName; set => SetProperty(ref _presetName, value); }

    /// <summary>How the popup's values change the chosen saved preset ("Field: old → new" per line); empty when they don't.</summary>
    public string PresetChanges
    {
        get => _presetChanges;
        private set
        {
            if (SetProperty(ref _presetChanges, value))
            {
                OnPropertyChanged(nameof(HasPresetChanges));
            }
        }
    }

    public bool HasPresetChanges => PresetChanges.Length > 0;

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
            RefreshPresetChanges();
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

        Draft.PropertyChanged += (_, _) =>
        {
            DraftMarks.Changed();
            RefreshPresetChanges();
        };

        OpenPresetsCommand = new Command(() =>
        {
            // with no preset chosen the popup starts empty ("From editor" fills it); a chosen preset stays as it was left
            if (SelectedPreset == null)
            {
                ClearDraft();
            }
            RefreshPresetCurrent();
            ShowPresets = true;
        });
        ClosePresetsCommand = new Command(() => ShowPresets = false);
        DraftFromEditorCommand = new Command(DraftFromEditor);
        SavePresetCommand = new Command(async () => await SavePresetAsync());
        ApplyPresetCommand = new Command(async () => await ApplyDraftAsync(), CanWrite);
        DeletePresetCommand = new Command(async () => await DeletePresetAsync(), () => SelectedPreset != null);
    }

    /// <summary>Show in the popup what the selected ESC reports now.</summary>
    private void RefreshPresetCurrent()
    {
        var state = SelectedConfigNode is { } node ? _monitor?.Get(node.NodeId) : null;
        PresetCurrent.Set(new Dictionary<string, object?>
        {
            [nameof(PresetDraft.NodeIdText)] = state?.NodeId,
            // no read exists for CAN speed
            [nameof(PresetDraft.CanBaud)] = null,
            [nameof(PresetDraft.ThrottleChannelText)] = state?.ThrottleChannel,
            [nameof(PresetDraft.ThrottleSource)] = state?.ThrottleSourcePwm is { } pwm
                ? Find(ThrottleSourceOptions, pwm ? HwSetThrottleSource.Pwm : HwSetThrottleSource.Can)
                : null,
            [nameof(PresetDraft.Msg1Rate)] = state != null ? RateFor(state, DataTypes.HwStatusMsg1.Id) : null,
            [nameof(PresetDraft.Msg2Rate)] = state != null ? RateFor(state, DataTypes.HwStatusMsg2.Id) : null,
            [nameof(PresetDraft.Msg3Rate)] = state != null ? RateFor(state, DataTypes.HwStatusMsg3.Id) : null,
            [nameof(PresetDraft.LedColor)] = Opt(LedColorOptions, state?.LedColor),
            [nameof(PresetDraft.LedBlink)] = Opt(LedBlinkOptions, state?.LedBlink),
            [nameof(PresetDraft.Direction)] = state?.CounterClockwise is { } ccw
                ? Find(DirectionOptions, ccw ? HwSetDirection.CounterClockwise : HwSetDirection.Clockwise)
                : null,
        });
        DraftMarks.Changed();
    }

    /// <summary>The row is ticked and its value differs from what the ESC reports; unknown values never differ.</summary>
    private bool DraftDiffers(string field)
    {
        var d = Draft;
        bool include = field switch
        {
            nameof(PresetDraft.NodeIdText) => d.IncludeNodeId,
            nameof(PresetDraft.CanBaud) => d.IncludeCanBaud,
            nameof(PresetDraft.ThrottleChannelText) => d.IncludeThrottleChannel,
            nameof(PresetDraft.ThrottleSource) => d.IncludeThrottleSource,
            nameof(PresetDraft.Msg1Rate) => d.IncludeMsg1Rate,
            nameof(PresetDraft.Msg2Rate) => d.IncludeMsg2Rate,
            nameof(PresetDraft.Msg3Rate) => d.IncludeMsg3Rate,
            nameof(PresetDraft.LedColor) or nameof(PresetDraft.LedBlink) => d.IncludeLed,
            nameof(PresetDraft.Direction) => d.IncludeDirection,
            _ => false,
        };
        object? value = field switch
        {
            nameof(PresetDraft.NodeIdText) => ParseByte(d.NodeIdText),
            nameof(PresetDraft.CanBaud) => d.CanBaud,
            nameof(PresetDraft.ThrottleChannelText) => ParseByte(d.ThrottleChannelText),
            nameof(PresetDraft.ThrottleSource) => d.ThrottleSource,
            nameof(PresetDraft.Msg1Rate) => d.Msg1Rate,
            nameof(PresetDraft.Msg2Rate) => d.Msg2Rate,
            nameof(PresetDraft.Msg3Rate) => d.Msg3Rate,
            nameof(PresetDraft.LedColor) => d.LedColor,
            // no mode is written as steady (0)
            nameof(PresetDraft.LedBlink) => d.LedColor == null ? null : d.LedBlink ?? Find(LedBlinkOptions, 0),
            nameof(PresetDraft.Direction) => d.Direction,
            _ => null,
        };
        return include && value != null && PresetCurrent.Value(field) is { } current && !Equals(value, current);
    }

    private static byte? ParseByte(string text) =>
        byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : null;

    /// <summary>The popup row of a settings editor field.</summary>
    private static string DraftField(string editorField) => editorField switch
    {
        nameof(NewNodeIdText) => nameof(PresetDraft.NodeIdText),
        nameof(SelectedCanBaud) => nameof(PresetDraft.CanBaud),
        nameof(ThrottleChannelText) => nameof(PresetDraft.ThrottleChannelText),
        nameof(SelectedThrottleSource) => nameof(PresetDraft.ThrottleSource),
        nameof(SelectedMsg1Rate) => nameof(PresetDraft.Msg1Rate),
        nameof(SelectedMsg2Rate) => nameof(PresetDraft.Msg2Rate),
        nameof(SelectedMsg3Rate) => nameof(PresetDraft.Msg3Rate),
        nameof(SelectedLedColor) => nameof(PresetDraft.LedColor),
        nameof(SelectedLedBlink) => nameof(PresetDraft.LedBlink),
        nameof(SelectedDirection) => nameof(PresetDraft.Direction),
        _ => editorField,
    };

    /// <summary>The selected ESC changed: its values in the popup, and green marks only for the node they were written to.</summary>
    private void PresetNodeChanged()
    {
        if (SelectedConfigNode is { } node && node.NodeId != _presetWrittenNode)
        {
            _presetWrittenNode = null;
            DraftMarks.Clear();
        }
        RefreshPresetCurrent();
    }

    /// <summary>Copy the values of the settings editor into the popup; which fields are ticked stays.</summary>
    private void DraftFromEditor()
    {
        DraftMarks.Clear();
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
        DraftMarks.Clear();
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

    /// <summary>Empty values in every popup field; which fields are ticked stays.</summary>
    private void ClearDraft()
    {
        DraftMarks.Clear();
        Draft.CanBaud = null;
        Draft.NodeIdText = "";
        Draft.ThrottleChannelText = "";
        Draft.ThrottleSource = null;
        Draft.Msg1Rate = null;
        Draft.Msg2Rate = null;
        Draft.Msg3Rate = null;
        Draft.LedColor = null;
        Draft.LedBlink = null;
        Draft.LedSave = true;
        Draft.Direction = null;
    }

    /// <summary>The ticked popup fields that have a value; null (and logged) when a number is invalid or nothing is left.</summary>
    /// <param name="empty">ticked fields left out because they have no value</param>
    /// <param name="quiet">don't log why there is no preset</param>
    private SettingsPreset? PresetFromDraft(string name, out List<string> empty, bool quiet = false)
    {
        void Log(string message)
        {
            if (!quiet)
            {
                AddLog(message);
            }
        }

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
                Log("Preset: node ID must be 1..127");
                return null;
            }
            nodeId = id;
        }
        byte? throttleChannel = null;
        if (d.IncludeThrottleChannel && d.ThrottleChannelText.Trim().Length > 0)
        {
            if (!byte.TryParse(d.ThrottleChannelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel) || channel is < 1 or > 63)
            {
                Log("Preset: throttle channel must be 1..63");
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
            Log("Preset is empty: tick at least one field and give it a value");
            return null;
        }
        return preset;
    }

    /// <summary>Each field of the preset as (title, value text), in the popup's order; null value = not in the preset.</summary>
    private List<(string Title, string? Text)> PresetFields(SettingsPreset p) =>
    [
        ("Node ID", p.NodeId?.ToString(CultureInfo.InvariantCulture)),
        ("CAN speed", Opt(CanBaudOptions, p.CanBaud)?.Text),
        ("Throttle channel", p.ThrottleChannel?.ToString(CultureInfo.InvariantCulture)),
        ("Throttle source", Opt(ThrottleSourceOptions, p.ThrottleSource)?.Text),
        ("StatusMsg1 rate", Opt(RateOptions, p.Msg1Rate)?.Text),
        ("StatusMsg2 rate", Opt(RateOptions, p.Msg2Rate)?.Text),
        ("StatusMsg3 rate", Opt(RateOptions, p.Msg3Rate)?.Text),
        ("LED", Opt(LedColorOptions, p.LedColor) is { } color ? LedText(color, Opt(LedBlinkOptions, p.LedBlink), p.LedSave) : null),
        ("Direction", Opt(DirectionOptions, p.Direction)?.Text),
    ];

    private string PresetText(SettingsPreset p, string separator) =>
        string.Join(separator, PresetFields(p).Where(f => f.Text != null).Select(f => $"{f.Title}: {f.Text}"));

    /// <summary>"Field: old → new" for each field that differs; "—" = not in the preset.</summary>
    private List<string> PresetChangeLines(SettingsPreset from, SettingsPreset to) =>
        PresetFields(from).Zip(PresetFields(to))
            .Where(f => f.First.Text != f.Second.Text)
            .Select(f => $"{f.First.Title}: {f.First.Text ?? "—"} → {f.Second.Text ?? "—"}")
            .ToList();

    /// <summary>Show how the popup's values would change the chosen saved preset.</summary>
    private void RefreshPresetChanges()
    {
        PresetChanges = SelectedPreset is { } saved && PresetFromDraft(saved.Name, out _, quiet: true) is { } edited
            ? string.Join("\n", PresetChangeLines(saved, edited))
            : "";
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
            var changes = PresetChangeLines(Presets[index], preset);
            if (Ask != null && !await Ask($"Replace preset \"{Presets[index].Name}\"?",
                    changes.Count > 0 ? string.Join("\n", changes) : "No value changes.", "Replace"))
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
        // the popup stays open: each of its rows turns green once written; the editors behind it get the values too
        _presetWrittenNode = null;
        DraftMarks.Clear();
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
                const string unknown = "Throttle channel of this ESC is unknown (enable Poll GetEscID or wait for the configuration read) — SetID needs it to keep it unchanged";
                AddLog(unknown);
                await ShowAlert($"Preset \"{preset.Name}\" not applied", $"Nothing was written to node {target.NodeId}.\n\n{unknown}");
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
            await ShowAlert($"Preset \"{preset.Name}\" applied", $"Nothing to write: node {target.NodeId} already has {string.Join(", ", same)}.");
            return;
        }
        if (await WriteStepsAsync($"Apply preset \"{preset.Name}\" ({steps.Count} write{(steps.Count == 1 ? "" : "s")})", steps) is not { } results)
        {
            // cancelled or blocked, already logged
            return;
        }

        byte nodeId = target.NodeId;
        var lines = new List<string>();
        for (int i = 0; i < steps.Count; i++)
        {
            if (results[i] is { Success: true })
            {
                nodeId = steps[i].NewNodeId ?? nodeId;
                foreach (var field in steps[i].Fields)
                {
                    DraftMarks.MarkWritten(DraftField(field));
                }
            }
            lines.Add(results[i] switch
            {
                { Success: true } => $"✓ {steps[i].Description}",
                { } failed => $"✗ {steps[i].Description}: {failed.Message}",
                null => $"– {steps[i].Description}: not written (stopped after the failure)",
            });
        }
        if (same.Count > 0)
        {
            lines.Add($"= Already set, not written again: {string.Join(", ", same)}");
        }
        _presetWrittenNode = nodeId;
        // after a node ID change the ESC answers on the new node: follow it once it is on the bus
        if (nodeId != target.NodeId && ConfigNodes.FirstOrDefault(n => n.NodeId == nodeId) is { } moved)
        {
            SelectedConfigNode = moved;
        }
        RefreshPresetCurrent();

        int written = results.Count(r => r is { Success: true });
        string outcome = written == steps.Count ? "applied"
            : written == 0 ? "not applied"
            : "partly applied";
        await ShowAlert($"Preset \"{preset.Name}\" {outcome}",
            $"Node {target.NodeId}: {written} of {steps.Count} write{(steps.Count == 1 ? "" : "s")} succeeded.\n\n{string.Join("\n", lines)}");
    }
}
