using System.Globalization;
using System.Resources;
using HobbywingCan.App.ViewModels;

namespace HobbywingCan.App.Localization;

public sealed record LanguageChoice(string Code, string Name)
{
    // Picker shows ToString()
    public override string ToString() => Name;
}

/// <summary>
/// The interface language: texts from Resources/Strings/AppResources*.resx, switched at run time and remembered.
/// XAML binds to the indexer through <see cref="TrExtension"/>; code calls <see cref="Tr(string)"/>.
/// </summary>
public sealed class Localizer : ObservableObject
{
    private const string PreferenceKey = "language";
    private static readonly ResourceManager Resources =
        new("HobbywingCan.App.Resources.Strings.AppResources", typeof(Localizer).Assembly);

    private LanguageChoice _language;

    private Localizer()
    {
        // first start: the Windows display language when there is a translation for it
        string code = Preferences.Default.Get(PreferenceKey, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        _language = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[0];
        Culture = new CultureInfo(_language.Code);
    }

    public static Localizer Instance { get; } = new();

    public IReadOnlyList<LanguageChoice> Languages { get; } = [new("en", "English"), new("uk", "Українська")];

    public CultureInfo Culture { get; private set; }

    /// <summary>Raised after the XAML texts were told to re-read, for the texts the view models build.</summary>
    public event Action? LanguageChanged;

    public LanguageChoice Language
    {
        get => _language;
        set
        {
            // the Picker sets null for a moment while it rebuilds
            if (value == null || !SetProperty(ref _language, value))
            {
                return;
            }
            Culture = new CultureInfo(value.Code);
            Preferences.Default.Set(PreferenceKey, value.Code);
            // empty name = every indexer binding re-reads
            OnPropertyChanged(string.Empty);
            LanguageChanged?.Invoke();
        }
    }

    public string this[string key] => Resources.GetString(key, Culture) ?? key;

    public static string Tr(string key) => Instance[key];

    // numbers look the same in every language, as in the rest of the app
    public static string Tr(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, Instance[key], args);
}
