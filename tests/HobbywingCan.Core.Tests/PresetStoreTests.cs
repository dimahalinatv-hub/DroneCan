using HobbywingCan.Core.Presets;

namespace HobbywingCan.Core.Tests;

public sealed class PresetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hw-presets-{Guid.NewGuid():N}");

    private string FilePath => Path.Combine(_dir, "presets.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoad_KeepsValuesAndMissingFields()
    {
        var store = new PresetStore(FilePath);
        SettingsPreset[] presets =
        [
            new("Quad", ThrottleSource: 0, Msg1Rate: 3, Msg2Rate: 5, LedColor: 2, LedBlink: 1, LedSave: false, Direction: 1),
            new("Ids", NodeId: 12, ThrottleChannel: 4, CanBaud: 1),
        ];

        store.Save(presets);
        var loaded = store.Load(out var error);

        Assert.Null(error);
        Assert.Equal(presets, loaded);
        Assert.Null(loaded[0].NodeId);
        Assert.Null(loaded[1].Msg3Rate);
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        var loaded = new PresetStore(FilePath).Load(out var error);

        Assert.Empty(loaded);
        Assert.Null(error);
    }

    [Fact]
    public void Load_BrokenFile_IsEmptyWithError()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        var loaded = new PresetStore(FilePath).Load(out var error);

        Assert.Empty(loaded);
        Assert.NotNull(error);
    }
}
