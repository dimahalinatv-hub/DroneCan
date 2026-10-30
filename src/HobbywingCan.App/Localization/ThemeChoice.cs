namespace HobbywingCan.App.Localization;

public sealed record ThemeChoice(AppTheme Theme, string Name)
{
    // equal by theme: the name follows the language, the theme it stands for does not
    public bool Equals(ThemeChoice? other) => other is not null && other.Theme == Theme;

    public override int GetHashCode() => Theme.GetHashCode();

    // Picker shows ToString()
    public override string ToString() => Name;
}

/// <summary>Light or dark colors regardless of Windows, or as in Windows (Unspecified); remembered.</summary>
public static class AppThemes
{
    private const string PreferenceKey = "theme";

    public static AppTheme Saved =>
        Enum.TryParse(Preferences.Default.Get(PreferenceKey, nameof(AppTheme.Unspecified)), out AppTheme theme)
            ? theme
            : AppTheme.Unspecified;

    public static void Apply(AppTheme theme)
    {
        Application.Current!.UserAppTheme = theme;
        Preferences.Default.Set(PreferenceKey, theme.ToString());
    }
}
