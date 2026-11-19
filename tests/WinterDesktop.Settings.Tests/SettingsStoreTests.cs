using WinterDesktop.Core.Models;
using WinterDesktop.Core.Settings;
using Xunit;

namespace WinterDesktop.Settings.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        using var temp = new TempDirectory();
        using var store = new SettingsStore(new TestLog(), temp.File("settings.json"));

        var settings = store.Load();

        Assert.False(store.LoadedFromDisk);
        Assert.True(settings.Snow.Enabled);
        Assert.Equal(SnowMode.Normal, settings.Snow.Mode);
        Assert.Equal(0.55, settings.Snow.Intensity, 3);
    }

    [Fact]
    public void SaveNow_ThenLoad_RoundTripsAllValues()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");

        using (var store = new SettingsStore(new TestLog(), path))
        {
            var settings = store.Load();
            settings.Snow.Intensity = 0.83;
            settings.Snow.WindStrength = -0.42;
            settings.Snow.WindAngleDegrees = 315;
            settings.Snow.Mode = SnowMode.Custom;
            settings.General.RunAtStartup = true;
            settings.Performance.Profile = Core.Models.PerformanceProfile.High;
            settings.Monitors.GetOrCreate("\\\\.\\DISPLAY1").IntensityScale = 1.7;
            Assert.True(store.SaveNow());
        }

        using var reopened = new SettingsStore(new TestLog(), path);
        var loaded = reopened.Load();

        Assert.True(reopened.LoadedFromDisk);
        Assert.Equal(0.83, loaded.Snow.Intensity, 3);
        Assert.Equal(-0.42, loaded.Snow.WindStrength, 3);
        Assert.Equal(315, loaded.Snow.WindAngleDegrees, 3);
        Assert.Equal(SnowMode.Custom, loaded.Snow.Mode);
        Assert.True(loaded.General.RunAtStartup);
        Assert.Equal(Core.Models.PerformanceProfile.High, loaded.Performance.Profile);
        Assert.Equal(1.7, loaded.Monitors.Overrides["\\\\.\\DISPLAY1"].IntensityScale, 3);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults_AndKeepsCorruptCopy()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, "{ this is not json ]");

        using var store = new SettingsStore(new TestLog(), path);
        var settings = store.Load();

        Assert.False(store.LoadedFromDisk);
        Assert.NotNull(store.LastLoadWarning);
        Assert.True(settings.Snow.Enabled);
        Assert.Single(Directory.GetFiles(temp.Path, "settings.corrupt-*.json"));
    }

    [Fact]
    public void CorruptFile_UsesBackupWhenAvailable()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");

        using (var store = new SettingsStore(new TestLog(), path))
        {
            var s = store.Load();
            s.Snow.Intensity = 0.91;
            store.SaveNow();
            // Второе сохранение создаёт .bak через File.Replace.
            s.Snow.Intensity = 0.11;
            store.SaveNow();
        }

        Assert.True(File.Exists(path + ".bak"));
        File.WriteAllText(path, "garbage");

        using var reopened = new SettingsStore(new TestLog(), path);
        var loaded = reopened.Load();

        Assert.True(reopened.LoadedFromDisk);
        Assert.Equal(0.91, loaded.Snow.Intensity, 3);
    }

    [Fact]
    public void OutOfRangeValues_AreClampedOnLoad()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "snow": { "intensity": 5.0, "fallSpeedScale": -3.0, "windAngleDegrees": -90, "opacity": 0.0 },
          "accumulation": { "maxThicknessDip": 1000 },
          "performance": { "maxParticles": -50 }
        }
        """);

        using var store = new SettingsStore(new TestLog(), path);
        var s = store.Load();

        Assert.Equal(1.0, s.Snow.Intensity, 3);
        Assert.Equal(0.2, s.Snow.FallSpeedScale, 3);
        Assert.Equal(270, s.Snow.WindAngleDegrees, 3);
        Assert.Equal(0.1, s.Snow.Opacity, 3);
        Assert.Equal(60, s.Accumulation.MaxThicknessDip, 3);
        Assert.Equal(0, s.Performance.MaxParticles);
    }

    [Fact]
    public void Load_LegacySchema_IsMigratedAndWrittenBack()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "accumulation": { "maxThicknessDip": 18 } }""");

        using var store = new SettingsStore(new TestLog(), path);
        var s = store.Load();

        Assert.Equal(24.0, s.Accumulation.MaxThicknessDip, 3);
        Assert.True(store.SaveNow());

        var json = File.ReadAllText(path);
        Assert.Contains($"\"schemaVersion\": {AppSettings.CurrentSchemaVersion}", json);
        Assert.Contains("\"maxThicknessDip\": 24", json);
    }

    [Fact]
    public void UnknownFields_AreIgnored()    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "futureThing": { "a": 1 }, "snow": { "intensity": 0.4 } }""");

        using var store = new SettingsStore(new TestLog(), path);
        var s = store.Load();

        Assert.Equal(0.4, s.Snow.Intensity, 3);
    }

    [Fact]
    public void NullSections_AreReplacedWithDefaults()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "snow": null, "general": null, "monitors": null }""");

        using var store = new SettingsStore(new TestLog(), path);
        var s = store.Load();

        Assert.NotNull(s.Snow);
        Assert.NotNull(s.General);
        Assert.NotNull(s.Monitors);
        Assert.Equal(0.55, s.Snow.Intensity, 3);
    }

    [Fact]
    public void PropertyChange_SchedulesDebouncedSave()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");

        using var store = new SettingsStore(new TestLog(), path, TimeSpan.FromMilliseconds(80));
        var s = store.Load();
        s.Snow.Intensity = 0.33;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !File.Exists(path)) Thread.Sleep(20);

        Assert.True(File.Exists(path), "Файл настроек не был создан после изменения свойства.");
        Assert.Contains("0.33", File.ReadAllText(path));
    }

    [Fact]
    public void Export_Import_RoundTrips()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var exportPath = temp.File("export.json");

        using var store = new SettingsStore(new TestLog(), path);
        var s = store.Load();
        s.Snow.Intensity = 0.77;
        s.Tools.ActiveTool = Core.Models.BrushTool.Shovel;
        store.Export(exportPath);

        store.Reset();
        Assert.Equal(0.55, store.Current.Snow.Intensity, 3);

        var imported = store.Import(exportPath);
        Assert.Equal(0.77, imported.Snow.Intensity, 3);
        Assert.Equal(Core.Models.BrushTool.Shovel, imported.Tools.ActiveTool);
    }

    [Fact]
    public void Import_InvalidFile_DoesNotChangeCurrentSettings()
    {
        using var temp = new TempDirectory();
        using var store = new SettingsStore(new TestLog(), temp.File("settings.json"));
        var s = store.Load();
        s.Snow.Intensity = 0.66;

        var bad = temp.File("bad.json");
        File.WriteAllText(bad, "not a json");

        Assert.ThrowsAny<Exception>(() => store.Import(bad));
        Assert.Equal(0.66, store.Current.Snow.Intensity, 3);
    }

    [Fact]
    public void Save_WritesSchemaVersion()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        using var store = new SettingsStore(new TestLog(), path);
        store.Load();
        store.SaveNow();

        var json = File.ReadAllText(path);
        Assert.Contains($"\"schemaVersion\": {AppSettings.CurrentSchemaVersion}", json);
    }

    [Fact]
    public void SaveSoon_AfterDispose_DoesNotThrow()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(new TestLog(), temp.File("settings.json"));
        store.Load();
        store.Dispose();
        store.SaveSoon();
        Assert.False(store.SaveNow());
    }
}
