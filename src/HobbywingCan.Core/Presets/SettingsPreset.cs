using System.Text.Json;

namespace HobbywingCan.Core.Presets;

/// <summary>
/// A named set of Hobbywing ESC settings, as the codes the Set* services take.
/// A null field is not part of the preset and is left unchanged when it is applied.
/// </summary>
public sealed record SettingsPreset(
    string Name,
    byte? CanBaud = null,
    byte? NodeId = null,
    byte? ThrottleChannel = null,
    byte? ThrottleSource = null,
    byte? Msg1Rate = null,
    byte? Msg2Rate = null,
    byte? Msg3Rate = null,
    byte? LedColor = null,
    byte? LedBlink = null,
    bool LedSave = true,
    byte? Direction = null)
{
    // Picker shows ToString()
    public override string ToString() => Name;
}

/// <summary>Keeps presets in one JSON file.</summary>
public sealed class PresetStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Path { get; } = path;

    /// <summary>Saved presets; empty when the file is missing. <paramref name="error"/> tells why an existing file could not be read.</summary>
    public List<SettingsPreset> Load(out string? error)
    {
        error = null;
        if (!File.Exists(Path))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<SettingsPreset>>(File.ReadAllText(Path), Options)?
                .Where(p => !string.IsNullOrWhiteSpace(p?.Name)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"Cannot read presets from {Path}: {ex.Message}";
            return [];
        }
    }

    public void Save(IEnumerable<SettingsPreset> presets)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        // write aside first so a failed write never leaves a half file
        string temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(presets.ToList(), Options));
        File.Move(temp, Path, overwrite: true);
    }
}
