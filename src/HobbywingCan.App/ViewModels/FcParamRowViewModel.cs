using System.Globalization;
using System.Windows.Input;
using HobbywingCan.Core.Autopilot;
using HobbywingCan.Core.Mavlink;
using static HobbywingCan.App.Localization.Localizer;

namespace HobbywingCan.App.ViewModels;

/// <summary>A value of an enumerated flight controller parameter in a Picker.</summary>
public sealed record FcOption(double Value, string Text)
{
    // equal by value, like Option: the list is rebuilt when the flight controller reports another value
    public bool Equals(FcOption? other) => other is not null && other.Value == Value;

    public override int GetHashCode() => Value.GetHashCode();

    // Picker shows ToString()
    public override string ToString() => Text;

    public static FcOption For(ParamOption o)
    {
        string value = ParamSpec.Format(o.Value);
        return new FcOption(o.Value, o.Name.StartsWith(value, StringComparison.Ordinal) ? o.Name : $"{value} {o.Name}");
    }
}

/// <summary>
/// One flight controller parameter in the ArduPilot popup: the new value (Picker or Entry), what the flight controller
/// holds and what is recommended. The background follows the ESC editor: yellow = edited, green = written.
/// </summary>
public sealed class FcParamRowViewModel : ObservableObject
{
    private static readonly Color DiffersColor = Color.FromArgb("#E0A000");
    private static readonly Color MatchesColor = Color.FromArgb("#43A047");

    private IReadOnlyList<FcOption> _options = [];
    private FcOption? _selectedOption;
    private string _editText = "";
    private double? _current;
    private bool _missing;
    private bool _written;

    public FcParamRowViewModel(ParamSpec spec, Func<FcParamRowViewModel, Task> set, Func<bool> canSet)
    {
        Spec = spec;
        SetCommand = new Command(async () => await set(this), canSet);
        RebuildOptions();
    }

    public ParamSpec Spec { get; }
    public string Name => Spec.Name;
    public ICommand SetCommand { get; }

    public bool HasOptions => Spec.Options != null;
    public bool HasEntry => !HasOptions;

    public IReadOnlyList<FcOption> Options { get => _options; private set => SetProperty(ref _options, value); }

    public FcOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            // the Picker sets null for a moment while its list is replaced
            if (value != null && SetProperty(ref _selectedOption, value))
            {
                Changed();
            }
        }
    }

    public string EditText
    {
        get => _editText;
        set
        {
            if (SetProperty(ref _editText, value))
            {
                Changed();
            }
        }
    }

    /// <summary>The value in the editor; null when the Entry does not hold a number.</summary>
    public double? EditValue => HasOptions
        ? SelectedOption?.Value
        : double.TryParse(EditText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>What the flight controller holds; null when not read (or it has no such parameter).</summary>
    public double? Current => _current;

    /// <summary>The flight controller did not answer for this name: it does not have the parameter (yet).</summary>
    public bool Missing => _missing;

    public string CurrentText => Missing ? Tr("Txt_NA") : Current is { } c ? ParamSpec.Format(c) : "";

    public double? Recommended => Spec.RecommendedFor(Current);

    public string RecommendedText => Recommended is { } r ? ParamSpec.Format(r) : "—";

    /// <summary>Green when the flight controller holds the recommended value, amber when not.</summary>
    public Color RecommendedColor => Current is not { } c || Recommended is not { } r ? Colors.Transparent
        : c == r ? MatchesColor : DiffersColor;

    public Color MarkColor =>
        Current is { } c && EditValue != c ? FieldMarks.Edited
        : _written ? FieldMarks.Written
        : Colors.Transparent;

    /// <summary>The help text, and what the value in the editor means (bits set, option name).</summary>
    public string Note
    {
        get
        {
            string help = Tr(Spec.HelpKey);
            if (Spec.RebootRequired)
            {
                help += " " + Tr("Fc_NeedsReboot");
            }
            string? meaning = EditValue is { } v && !HasOptions ? Spec.Describe(v) : null;
            return meaning switch
            {
                null => help,
                "" => $"{help} [{Tr("Fc_NoBits")}]",
                _ when Spec.OutputMask => $"{help} [{Tr("Fc_Outputs", meaning)}]",
                _ => $"{help} [{meaning}]",
            };
        }
    }

    /// <summary>What the flight controller reported for this parameter (null = no answer, it has no such parameter).</summary>
    public void Loaded(AutopilotParam? param)
    {
        _written = false;
        Apply(param);
    }

    /// <summary>The flight controller took the written value; <paramref name="param"/> is its echo.</summary>
    public void Written(AutopilotParam param)
    {
        _written = true;
        Apply(param);
    }

    /// <summary>Nothing is known (not connected): the editor shows the recommended value.</summary>
    public void Clear()
    {
        _current = null;
        _missing = false;
        _written = false;
        SetEditor(Spec.Recommended);
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(CurrentText));
    }

    private void Apply(AutopilotParam? param)
    {
        _current = param?.Value;
        _missing = param == null;
        SetEditor(_current ?? Spec.Recommended);
    }

    private void SetEditor(double? value)
    {
        RebuildOptions();
        if (HasOptions)
        {
            _selectedOption = Options.FirstOrDefault(o => o.Value == value);
            OnPropertyChanged(nameof(SelectedOption));
        }
        else
        {
            _editText = value is { } v ? ParamSpec.Format(v) : "";
            OnPropertyChanged(nameof(EditText));
        }
        Changed();
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Missing));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(Recommended));
        OnPropertyChanged(nameof(RecommendedText));
        OnPropertyChanged(nameof(RecommendedColor));
    }

    /// <summary>The named values, plus the flight controller's value when it is not one of them.</summary>
    private void RebuildOptions()
    {
        if (Spec.Options is not { } named)
        {
            return;
        }
        var options = named.Select(FcOption.For).ToList();
        if (_current is { } c && options.All(o => o.Value != c))
        {
            options.Add(new FcOption(c, ParamSpec.Format(c)));
        }
        Options = options;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(MarkColor));
        OnPropertyChanged(nameof(Note));
    }

    public void RefreshCommand() => ((Command)SetCommand).ChangeCanExecute();
}
