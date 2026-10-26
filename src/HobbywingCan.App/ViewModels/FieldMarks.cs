namespace HobbywingCan.App.ViewModels;

/// <summary>
/// Highlight of each settings field, bound as <c>Marks[PropertyName]</c>:
/// yellow = changed but not written, green = written to the ESC.
/// </summary>
public sealed class FieldMarks : ObservableObject
{
    // translucent so they read on both light and dark theme
    public static readonly Color Edited = Color.FromArgb("#66FFC107");
    public static readonly Color Written = Color.FromArgb("#664CAF50");

    private readonly Func<string, object?> _current;
    // last value known to be in the ESC: reported by it or written by us
    private readonly Dictionary<string, object?> _baseline = new();
    private readonly HashSet<string> _written = new();

    public FieldMarks(Func<string, object?> current) => _current = current;

    public Color this[string field] =>
        !Same(_current(field), _baseline.GetValueOrDefault(field)) ? Edited
        : _written.Contains(field) ? Written
        : Colors.Transparent;

    // an empty Entry is "nothing known", like a Picker with no selection
    private static bool Same(object? a, object? b) => Equals(a is "" ? null : a, b is "" ? null : b);

    /// <summary>The ESC reported <paramref name="value"/> for the field.</summary>
    public T Loaded<T>(string field, T value)
    {
        if (!Same(_baseline.GetValueOrDefault(field), value))
        {
            // the ESC does not hold what was written
            _written.Remove(field);
        }
        _baseline[field] = value;
        Changed();
        return value;
    }

    /// <summary>The field's current value was written successfully.</summary>
    public void MarkWritten(string field)
    {
        _baseline[field] = _current(field);
        _written.Add(field);
        Changed();
    }

    public void Clear()
    {
        _baseline.Clear();
        _written.Clear();
        Changed();
    }

    // empty name = every indexer binding re-reads
    public void Changed() => OnPropertyChanged(string.Empty);
}
