using WinterDesktop.Core.Geometry;
using WinterDesktop.Core.Models;
using WinterDesktop.SnowEngine;
using Xunit;

namespace WinterDesktop.Core.Tests;

/// <summary>
/// Гирлянды: раскладка по видимым верхним кромкам, перекрытие окнами выше по z-order,
/// лимит лампочек и чистая математика мигания.
/// </summary>
public sealed class GarlandTests
{
    private static readonly MonitorDescriptor Monitor =
        new("DISPLAY1", "Test", RectI.FromSize(0, 0, 800, 600), RectI.FromSize(0, 0, 800, 560), 96, true);

    private static readonly MonitorDescriptor LowerMonitor =
        new("DISPLAY2", "Lower", RectI.FromSize(0, 600, 800, 600), RectI.FromSize(0, 600, 800, 560), 96, false);

    private static SceneSnapshot Scene(params WindowDescriptor[] windows) =>
        new(new[] { Monitor }, windows, 1);

    private static WindowDescriptor Window(int left, int top, int right, int bottom, int z = 0, int id = 1) =>
        new(new IntPtr(0x2000 + id), RectI.FromLtrb(left, top, right, bottom), WindowKind.Normal, z);

    private static SimulationSettings Settings(
        bool enabled = true,
        float spacing = 20f,
        float size = 6f,
        GarlandAnimation animation = GarlandAnimation.Twinkle,
        float speed = 1f,
        float glow = 0.5f) => new()
    {
        SnowEnabled = false,
        AccumulationEnabled = false,
        GarlandsEnabled = enabled,
        GarlandPalette = GarlandPalette.Festive,
        GarlandAnimation = animation,
        GarlandSpeed = speed,
        GarlandSpacingDip = spacing,
        GarlandBulbSizeDip = size,
        GarlandGlow = glow,
        MaxParticles = 0,
    };

    [Fact]
    public void GarlandsDisabled_ProducesNoStrings()
    {
        var sim = new SnowSimulation(seed: 50);
        sim.UpdateScene(Scene(Window(100, 300, 700, 500)), null, Settings(enabled: false));

        Assert.Empty(sim.Garlands);
        Assert.Equal(0, sim.GarlandBulbCount);
    }

    [Fact]
    public void Garland_IsBuiltAlongWindowTop_WithEvenSpacing()
    {
        var sim = new SnowSimulation(seed: 51);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, Settings());

        var garland = Assert.Single(sim.Garlands);
        Assert.Equal(new IntPtr(0x2001), garland.Owner);
        Assert.Equal(300f, garland.EdgeY, 1);
        Assert.True(garland.WireY > garland.EdgeY, "Провод должен висеть ниже верхней кромки.");
        Assert.True(garland.BulbRadius > 1f);

        Assert.True(garland.BulbCount >= 15, $"Слишком мало лампочек: {garland.BulbCount}");
        Assert.Equal(garland.BulbCount, sim.GarlandBulbCount);

        for (int i = 0; i < garland.Bulbs.Length; i++)
        {
            var bulb = garland.Bulbs[i];
            Assert.InRange(bulb.X, 100f, 500f);
            Assert.True(bulb.Y > garland.EdgeY, "Лампочки должны свисать вниз, в сторону окна.");

            if (i > 0)
            {
                float step = bulb.X - garland.Bulbs[i - 1].X;
                Assert.Equal(20f, step, 2);
                Assert.Equal(garland.Bulbs[i - 1].Index + 1, bulb.Index);
            }
        }
    }

    [Fact]
    public void Garland_MovesWithWindow()
    {
        var sim = new SnowSimulation(seed: 52);
        var settings = Settings();

        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, settings);
        float firstX = sim.Garlands.Single().Bulbs[4].X;

        sim.UpdateScene(Scene(Window(140, 300, 540, 500)), null, settings);
        float movedX = sim.Garlands.Single().Bulbs[4].X;

        Assert.Equal(40f, movedX - firstX, 1);
    }

    [Fact]
    public void Garland_HiddenBehindWindowAbove_KeepsOnlyVisibleBulbs()
    {
        var sim = new SnowSimulation(seed: 53);
        var below = Window(100, 300, 700, 500, z: 1, id: 1);
        var above = Window(0, 250, 400, 400, z: 0, id: 2);

        sim.UpdateScene(Scene(above, below), null, Settings());

        var garland = sim.Garlands.Single(g => g.Owner == below.Handle);
        Assert.NotEmpty(garland.Bulbs);
        Assert.All(garland.Bulbs, bulb => Assert.True(bulb.X >= 400f,
            $"Лампочка на X={bulb.X} должна быть скрыта окном выше."));
    }

    [Fact]
    public void Garland_IgnoresTaskbarAndShellWindows()
    {
        var normal = Window(100, 300, 700, 500, z: 1, id: 1);
        var taskbar = new WindowDescriptor(new IntPtr(0x3001), RectI.FromLtrb(0, 560, 800, 600), WindowKind.Taskbar, 2);
        var shell = new WindowDescriptor(new IntPtr(0x3002), RectI.FromLtrb(0, 0, 800, 200), WindowKind.Shell, 3);

        var sim = new SnowSimulation(seed: 54);
        sim.UpdateScene(Scene(normal, taskbar, shell), null, Settings());

        var garland = Assert.Single(sim.Garlands);
        Assert.Equal(normal.Handle, garland.Owner);
    }

    [Fact]
    public void Garland_BulbCount_HasHardLimit()
    {
        // Много широких окон на двух мониторах: суммарно больше лимита лампочек.
        var monitors = new[] { Monitor, LowerMonitor };
        var windows = new WindowDescriptor[50];
        for (int i = 0; i < windows.Length; i++)
        {
            int top = 20 + (i * 22);
            windows[i] = Window(20, top, 780, top + 18, z: i, id: i + 10);
        }

        var scene = new SceneSnapshot(monitors, windows, 2);
        var sim = new SnowSimulation(seed: 55);
        sim.UpdateScene(scene, new[] { 1f, 1f }, Settings(spacing: 8f));

        Assert.Equal(SnowSimulation.MaxGarlandBulbs, sim.GarlandBulbCount);
        Assert.True(sim.GarlandCount > 0);
    }

    [Fact]
    public void Garlands_CanBeToggledWithoutRebuildingScene()
    {
        var sim = new SnowSimulation(seed: 56);
        var scene = Scene(Window(100, 300, 700, 500));

        sim.UpdateScene(scene, null, Settings());
        Assert.NotEmpty(sim.Garlands);

        sim.UpdateScene(scene, null, Settings(enabled: false));
        Assert.Empty(sim.Garlands);

        sim.UpdateScene(scene, null, Settings());
        Assert.NotEmpty(sim.Garlands);
    }

    [Fact]
    public void GarlandBrightness_IsBoundedAndAnimated()
    {
        var modes = new[]
        {
            GarlandAnimation.Twinkle,
            GarlandAnimation.Wave,
            GarlandAnimation.Chase,
            GarlandAnimation.Steady,
        };

        float[] phases = { 0f, 1.7f, 4.2f };
        double[] times = { 0d, 0.5d, 3.3d, 11.9d };
        float[] speeds = { 0.1f, 1f, 4f };

        foreach (var mode in modes)
        {
            foreach (float phase in phases)
            {
                foreach (double time in times)
                {
                    foreach (float speed in speeds)
                    {
                        float value = GarlandTiming.Brightness(mode, phase, (float)time, speed);
                        Assert.InRange(value, 0f, 1f);
                    }
                }
            }
        }

        Assert.Equal(1f, GarlandTiming.Brightness(GarlandAnimation.Steady, 2.3f, 7.1f, 3f), 3);
    }

    [Fact]
    public void GarlandBrightness_ChangesOverTime_AndSpeedScalesIt()
    {
        foreach (var mode in new[] { GarlandAnimation.Twinkle, GarlandAnimation.Wave, GarlandAnimation.Chase })
        {
            float start = GarlandTiming.Brightness(mode, 0f, 0f, 1f);
            float later = GarlandTiming.Brightness(mode, 0f, 0.37f, 1f);
            Assert.True(MathF.Abs(start - later) > 0.01f, $"{mode}: яркость не меняется со временем.");
        }

        float normal = GarlandTiming.Brightness(GarlandAnimation.Twinkle, 1.3f, 1f, 1f);
        float doubled = GarlandTiming.Brightness(GarlandAnimation.Twinkle, 1.3f, 0.5f, 2f);
        Assert.Equal(normal, doubled, 4);
    }
}
