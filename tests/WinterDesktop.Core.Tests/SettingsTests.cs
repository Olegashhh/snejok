using WinterDesktop.Core.Geometry;
using WinterDesktop.Core.Models;
using WinterDesktop.Core.Performance;
using WinterDesktop.Core.Settings;
using Xunit;

namespace WinterDesktop.Core.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void SnowPresets_Blizzard_IsTheMostIntense()
    {
        var light = new SnowSettings();
        var normal = new SnowSettings();
        var blizzard = new SnowSettings();

        SnowPresets.Apply(light, SnowMode.Light);
        SnowPresets.Apply(normal, SnowMode.Normal);
        SnowPresets.Apply(blizzard, SnowMode.Blizzard);

        Assert.True(light.Intensity < normal.Intensity);
        Assert.True(normal.Intensity < blizzard.Intensity);
        Assert.True(light.WindStrength < blizzard.WindStrength);
        Assert.Equal(SnowMode.Blizzard, blizzard.Mode);
        Assert.Equal(SnowMode.Light, light.Mode);
    }

    [Fact]
    public void SnowPresets_Custom_KeepsUserValues()
    {
        var s = new SnowSettings { Intensity = 0.42, WindStrength = -0.7 };
        SnowPresets.Apply(s, SnowMode.Custom);

        Assert.Equal(0.42, s.Intensity, 3);
        Assert.Equal(-0.7, s.WindStrength, 3);
        Assert.Equal(SnowMode.Custom, s.Mode);
    }

    [Fact]
    public void Settings_ClampValuesInSetters()
    {
        var s = new SnowSettings
        {
            Intensity = 7,
            WindStrength = -9,
            Opacity = 0,
            LargeFlakeChance = 0.9,
            WindAngleDegrees = 720,
        };

        Assert.Equal(1.0, s.Intensity, 3);
        Assert.Equal(-1.0, s.WindStrength, 3);
        Assert.Equal(0.1, s.Opacity, 3);
        Assert.Equal(0.5, s.LargeFlakeChance, 3);
        Assert.Equal(0, s.WindAngleDegrees, 3);
    }

    [Fact]
    public void SettingsSerializer_RoundTrip_PreservesValues()
    {
        var original = new AppSettings();
        original.Snow.Intensity = 0.71;
        original.Snow.Mode = SnowMode.Custom;
        original.Tools.ActiveTool = BrushTool.Shovel;
        original.Monitors.GetOrCreate("DISPLAY2").Enabled = false;

        var json = SettingsSerializer.Serialize(original);
        var restored = SettingsSerializer.Deserialize(json);

        Assert.Equal(0.71, restored.Snow.Intensity, 3);
        Assert.Equal(SnowMode.Custom, restored.Snow.Mode);
        Assert.Equal(BrushTool.Shovel, restored.Tools.ActiveTool);
        Assert.False(restored.Monitors.Overrides["DISPLAY2"].Enabled);
    }

    [Fact]
    public void SettingsSerializer_EmptyObject_GivesDefaults()
    {
        var s = SettingsSerializer.Deserialize("{}");

        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
        Assert.NotNull(s.Snow);
        Assert.True(s.Snow.Enabled);
    }

    [Fact]
    public void SettingsSerializer_UpgradesLegacyThicknessDefault()
    {
        var legacy = SettingsSerializer.Deserialize(
            """{ "schemaVersion": 1, "accumulation": { "maxThicknessDip": 18 } }""");
        Assert.Equal(24.0, legacy.Accumulation.MaxThicknessDip, 3);

        var custom = SettingsSerializer.Deserialize(
            """{ "schemaVersion": 1, "accumulation": { "maxThicknessDip": 30 } }""");
        Assert.Equal(30.0, custom.Accumulation.MaxThicknessDip, 3);
    }

    [Fact]
    public void SettingsSerializer_InvalidJson_Throws()
    {
        Assert.ThrowsAny<Exception>(() => SettingsSerializer.Deserialize("{{{"));
    }

    [Fact]
    public void MonitorCollection_AllMonitors_OverridesIndividualFlags()
    {
        var collection = new MonitorCollectionSettings { AllMonitors = true };
        var monitor = new MonitorDescriptor("DISPLAY1", "Dell", RectI.FromSize(0, 0, 1920, 1080), RectI.FromSize(0, 0, 1920, 1040), 96, true);
        collection.GetOrCreate("DISPLAY1").Enabled = false;

        Assert.True(collection.IsEnabled(monitor));

        collection.AllMonitors = false;
        Assert.False(collection.IsEnabled(monitor));
    }

    [Fact]
    public void MonitorCollection_UnknownMonitor_DisabledWhenNotAll()
    {
        var collection = new MonitorCollectionSettings { AllMonitors = false };
        var monitor = new MonitorDescriptor("NEW", "New", RectI.FromSize(0, 0, 800, 600), RectI.FromSize(0, 0, 800, 600), 96, false);

        Assert.False(collection.IsEnabled(monitor));
    }

    [Fact]
    public void MonitorDescriptor_Scale_ReflectsDpi()
    {
        var monitor = new MonitorDescriptor("D", "D", RectI.FromSize(0, 0, 3840, 2160), RectI.FromSize(0, 0, 3840, 2160), 192, true);
        Assert.Equal(2f, monitor.Scale, 3);
    }

    [Fact]
    public void PerformanceBudget_FpsLimit_Applies()
    {
        var budget = PerformanceBudget.ForProfile(PerformanceProfile.High)
            .WithFpsLimit(FrameRateLimit.Fps30, 144);

        Assert.Equal(30, budget.TargetFps);
        Assert.True(budget.MaxParticles > 2600);
    }

    [Fact]
    public void PerformanceBudget_Degrade_ReducesParticlesButNotBelowFloor()
    {
        var budget = PerformanceBudget.ForProfile(PerformanceProfile.Balanced);

        var degraded = budget.Degrade(3);

        Assert.True(degraded.MaxParticles < budget.MaxParticles);
        Assert.True(degraded.MaxParticles >= 300);
        Assert.True(degraded.QualityScale < budget.QualityScale);
    }

    [Fact]
    public void PerformanceBudget_ManualParticleLimit_Wins()
    {
        var budget = PerformanceBudget.ForProfile(PerformanceProfile.Eco).WithParticles(5000);
        Assert.Equal(5000, budget.MaxParticles);
    }

    [Fact]
    public void SettingsTree_EnumeratesNestedNodes()
    {
        var settings = new AppSettings();
        settings.Monitors.GetOrCreate("A");
        var nodes = SettingsTree.Enumerate(settings).ToList();

        Assert.Contains(settings.Snow, nodes);
        Assert.Contains(settings.Monitors, nodes);
        Assert.Contains(settings.Monitors.Overrides["A"], nodes);
    }

    [Fact]
    public void SettingsSerializer_Snowmen_RoundTrip()
    {
        var original = new AppSettings();
        original.Snowmen.Items.Add(new SnowmanRecord
        {
            Monitor = "DISPLAY1",
            X = 123.5,
            BaseY = 456.25,
            Scale = 1.25,
            Balls = 2,
        });
        original.Snowmen.Items.Add(new SnowmanRecord
        {
            Monitor = "DISPLAY2",
            X = 900,
            BaseY = 1040,
            Scale = 1,
            Balls = 3,
        });

        var restored = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(original));

        Assert.Equal(2, restored.Snowmen.Items.Count);
        var first = restored.Snowmen.Items[0];
        Assert.Equal("DISPLAY1", first.Monitor);
        Assert.Equal(123.5, first.X, 3);
        Assert.Equal(456.25, first.BaseY, 3);
        Assert.Equal(1.25, first.Scale, 3);
        Assert.Equal(2, first.Balls);
        Assert.Equal(3, restored.Snowmen.Items[1].Balls);
    }

    [Fact]
    public void SettingsSerializer_SanitizesSnowmen()
    {
        var settings = new AppSettings();
        settings.Snowmen.Items.Add(new SnowmanRecord
        {
            Monitor = "   ",
            X = 10,
            BaseY = 20,
            Scale = 1,
            Balls = 3,
        });
        settings.Snowmen.Items.Add(new SnowmanRecord
        {
            Monitor = "DISPLAY1",
            X = 100,
            BaseY = 200,
            Scale = 9,
            Balls = 9,
        });

        for (int i = 0; i < 100; i++)
        {
            settings.Snowmen.Items.Add(new SnowmanRecord
            {
                Monitor = "DISPLAY1",
                X = 300 + i,
                BaseY = 300,
                Scale = 1,
                Balls = 1,
            });
        }

        var restored = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));

        Assert.Equal(64, restored.Snowmen.Items.Count);
        var first = restored.Snowmen.Items[0];
        Assert.Equal("DISPLAY1", first.Monitor);
        Assert.Equal(2.5, first.Scale, 3);
        Assert.Equal(3, first.Balls);
        Assert.All(restored.Snowmen.Items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Monitor));
            Assert.InRange(item.Balls, 1, 3);
            Assert.InRange(item.Scale, 0.5, 2.5);
        });
    }

    [Fact]
    public void SettingsSerializer_MissingSnowmen_GivesEmptyList()
    {
        var restored = SettingsSerializer.Deserialize("{}");

        Assert.NotNull(restored.Snowmen);
        Assert.Empty(restored.Snowmen.Items);
    }

    [Fact]
    public void Garlands_ClampValuesInSetters()
    {
        var garlands = new GarlandsSettings
        {
            Speed = 99,
            SpacingDip = 1,
            BulbSizeDip = 99,
            Glow = -5,
        };

        Assert.Equal(4.0, garlands.Speed, 3);
        Assert.Equal(12.0, garlands.SpacingDip, 3);
        Assert.Equal(12.0, garlands.BulbSizeDip, 3);
        Assert.Equal(0.0, garlands.Glow, 3);
    }

    [Fact]
    public void SettingsSerializer_Garlands_RoundTrip()
    {
        var original = new AppSettings();
        original.Garlands.Enabled = true;
        original.Garlands.Palette = GarlandPalette.Winter;
        original.Garlands.Animation = GarlandAnimation.Chase;
        original.Garlands.Speed = 2.25;
        original.Garlands.SpacingDip = 34;
        original.Garlands.BulbSizeDip = 7.5;
        original.Garlands.Glow = 0.8;

        var restored = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(original));

        Assert.True(restored.Garlands.Enabled);
        Assert.Equal(GarlandPalette.Winter, restored.Garlands.Palette);
        Assert.Equal(GarlandAnimation.Chase, restored.Garlands.Animation);
        Assert.Equal(2.25, restored.Garlands.Speed, 3);
        Assert.Equal(34.0, restored.Garlands.SpacingDip, 3);
        Assert.Equal(7.5, restored.Garlands.BulbSizeDip, 3);
        Assert.Equal(0.8, restored.Garlands.Glow, 3);
    }

    [Fact]
    public void SettingsSerializer_MissingGarlands_GivesDefaults()
    {
        var restored = SettingsSerializer.Deserialize("{}");

        Assert.NotNull(restored.Garlands);
        Assert.False(restored.Garlands.Enabled);
        Assert.Equal(GarlandPalette.Festive, restored.Garlands.Palette);
        Assert.Equal(GarlandAnimation.Twinkle, restored.Garlands.Animation);
        Assert.Equal(1.0, restored.Garlands.Speed, 3);
        Assert.Equal(26.0, restored.Garlands.SpacingDip, 3);
    }
}
