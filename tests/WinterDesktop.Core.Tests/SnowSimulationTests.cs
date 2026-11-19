using WinterDesktop.Core.Geometry;
using WinterDesktop.Core.Models;
using WinterDesktop.SnowEngine;
using Xunit;

namespace WinterDesktop.Core.Tests;

public sealed class SnowSimulationTests
{
    private static readonly MonitorDescriptor Monitor =
        new("DISPLAY1", "Test", RectI.FromSize(0, 0, 800, 600), RectI.FromSize(0, 0, 800, 560), 96, true);

    private static SceneSnapshot Scene(params WindowDescriptor[] windows) =>
        new(new[] { Monitor }, windows, 1);

    private static SimulationSettings Settings(
        bool accumulation = true,
        bool screenBottom = false,
        bool windowTops = true,
        bool windowBottoms = false,
        float intensity = 1f,
        float foregroundShare = 0f,
        int maxParticles = 800) => new()
    {
        SnowEnabled = true,
        Intensity = intensity,
        MaxParticles = maxParticles,
        ForegroundShare = foregroundShare,
        Turbulence = 0f,
        Spread = 0f,
        AccumulationEnabled = accumulation,
        WindowTops = windowTops,
        WindowBottoms = windowBottoms,
        ScreenBottom = screenBottom,
        Taskbar = false,
        MaxThicknessDip = 18f,
        DepositRate = 1f,
    };

    private static WindowDescriptor Window(int left, int top, int right, int bottom, int z = 0) =>
        new(new IntPtr(0x1000 + z), RectI.FromLtrb(left, top, right, bottom), WindowKind.Normal, z);

    private static void Run(SnowSimulation sim, SimulationSettings settings, double seconds, SnowInput input = default)
    {
        const double step = 1.0 / 60.0;
        int steps = (int)(seconds / step);
        for (int i = 0; i < steps; i++) sim.Update(step, input, settings);
    }

    [Fact]
    public void SnowSpawns_AndStaysWithinBudget()
    {
        var sim = new SnowSimulation(seed: 1);
        var settings = Settings(screenBottom: true, maxParticles: 500);
        sim.UpdateScene(Scene(), null, settings);

        Run(sim, settings, 3);

        Assert.True(sim.ParticleCount > 0);
        Assert.True(sim.ParticleCount <= 500);
    }

    [Fact]
    public void Snow_StaysInsideVirtualDesktop_NoNaN()
    {
        var sim = new SnowSimulation(seed: 2);
        var settings = Settings(screenBottom: true, intensity: 1f, maxParticles: 1200)
            with { WindStrength = 0.6f, Turbulence = 0.9f };
        sim.UpdateScene(Scene(), null, settings);

        Run(sim, settings, 20);

        foreach (var p in sim.ActiveParticles)
        {
            Assert.False(float.IsNaN(p.X) || float.IsNaN(p.Y));
            Assert.InRange(p.X, -200f, 1000f);
            Assert.InRange(p.Y, -400f, 700f);
        }
    }

    [Fact]
    public void Wind_PushesParticlesInRequestedDirection()
    {
        var settings = Settings(screenBottom: true) with { WindStrength = 0.9f, WindAngleDegrees = 0f };
        var sim = new SnowSimulation(seed: 3);
        sim.UpdateScene(Scene(), null, settings);
        sim.WarmStart(settings, 1);

        float startX = sim.ActiveParticles[0].X;
        Run(sim, settings, 0.5);
        float endX = sim.ActiveParticles[0].X;
        float vx = sim.ActiveParticles[0].Vx;

        Assert.True(vx > 100f, $"Ожидалась положительная горизонтальная скорость, было {vx}");
        Assert.True(endX > startX, $"Ожидался снос вправо, было {startX} -> {endX}");

        var reversed = settings with { WindStrength = -0.9f };
        var sim2 = new SnowSimulation(seed: 3);
        sim2.UpdateScene(Scene(), null, reversed);
        sim2.WarmStart(reversed, 1);
        float startX2 = sim2.ActiveParticles[0].X;
        Run(sim2, reversed, 0.5);
        float endX2 = sim2.ActiveParticles[0].X;
        float vx2 = sim2.ActiveParticles[0].Vx;

        Assert.True(vx2 < -100f, $"Ожидалась отрицательная горизонтальная скорость, было {vx2}");
        Assert.True(endX2 < startX2, $"Ожидался снос влево, было {startX2} -> {endX2}");
    }

    [Fact]
    public void Snow_AccumulatesOnWindowTopLedge()
    {
        var settings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 4);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, settings);

        Run(sim, settings, 25);

        Assert.Single(sim.Ledges);
        Assert.True(sim.Ledges[0].SumHeight() > 5f, "Снег не накопился на верхней кромке окна.");
        Assert.True(sim.TotalSnowMass > 0f);
    }

    [Fact]
    public void AccumulationDisabled_ProducesNoLedges()
    {
        var settings = Settings(accumulation: false);
        var sim = new SnowSimulation(seed: 5);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, settings);
        Run(sim, settings, 5);

        Assert.Empty(sim.Ledges);
        Assert.True(sim.ParticleCount > 0);
    }

    [Fact]
    public void SnowThickness_NeverExceedsMaximum()
    {
        var settings = Settings(intensity: 1f, maxParticles: 2000, screenBottom: true) with { MaxThicknessDip = 8f };
        var sim = new SnowSimulation(seed: 6, initialCapacity: 4096);
        sim.UpdateScene(Scene(Window(0, 200, 800, 500)), null, settings);

        Run(sim, settings, 90);

        foreach (var ledge in sim.Ledges)
        {
            foreach (var h in ledge.HeightSamples)
            {
                Assert.True(h <= 8f + 0.35f, $"Толщина {h} превысила предел 8px");
            }
        }
    }

    [Fact]
    public void ForegroundParticles_AccumulateOnBottomLedge()
    {
        var settings = Settings(windowTops: false, windowBottoms: true, foregroundShare: 1f, intensity: 1f);
        var sim = new SnowSimulation(seed: 7);
        sim.UpdateScene(Scene(Window(100, 200, 500, 400)), null, settings);

        Run(sim, settings, 25);

        Assert.Single(sim.Ledges);
        Assert.Equal(LedgeSide.Bottom, sim.Ledges[0].Side);
        Assert.True(sim.Ledges[0].SumHeight() > 2f);
    }

    [Fact]
    public void BackgroundParticles_DoNotLandOnBottomLedges()
    {
        var settings = Settings(windowTops: false, windowBottoms: true, foregroundShare: 0f);
        var sim = new SnowSimulation(seed: 8);
        sim.UpdateScene(Scene(Window(100, 200, 500, 400)), null, settings);

        Run(sim, settings, 15);

        Assert.All(sim.Ledges, ledge => Assert.Equal(0f, ledge.SumHeight(), 3));
        Assert.Equal(0f, sim.TotalSnowMass, 3);
    }

    [Fact]
    public void Mitten_ErodesAccumulatedSnow()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 9);
        var window = Window(100, 300, 500, 500);
        sim.UpdateScene(Scene(window), null, baseSettings);
        Run(sim, baseSettings, 25);

        var ledge = sim.Ledges[0];
        float before = ledge.SumHeight();
        Assert.True(before > 5f);

        var mitten = baseSettings with
        {
            InteractionMode = true,
            ActiveTool = BrushTool.Mitten,
            MittenRadiusDip = 120f,
            MittenStrength = 1f,
        };
        float x = (window.Bounds.Left + window.Bounds.Right) * 0.5f;
        float y = window.Bounds.Top;
        var input = new SnowInput(x, y, true, false, false);

        Run(sim, mitten, 4, input);

        Assert.True(sim.Ledges[0].SumHeight() < before * 0.8f,
            $"Варежка не сработала: было {before}, стало {sim.Ledges[0].SumHeight()}");
    }

    [Fact]
    public void Shovel_ClearsSnowCompletely()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 10);
        var window = Window(100, 300, 500, 500);
        sim.UpdateScene(Scene(window), null, baseSettings);
        Run(sim, baseSettings, 25);
        Assert.True(sim.Ledges[0].SumHeight() > 3f);

        var shovel = baseSettings with
        {
            InteractionMode = true,
            ActiveTool = BrushTool.Shovel,
            ShovelRadiusDip = 400f,
        };
        var input = new SnowInput(300, 300, true, true, true);
        sim.Update(1.0 / 60.0, input, shovel);

        Assert.Equal(0f, sim.Ledges[0].SumHeight(), 3);
    }

    [Fact]
    public void ClearAllSnow_RemovesEverything()
    {
        var settings = Settings(intensity: 1f, maxParticles: 1000, screenBottom: true);
        var sim = new SnowSimulation(seed: 11);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, settings);
        Run(sim, settings, 20);

        sim.ClearAllSnow();

        Assert.Equal(0f, sim.TotalSnowMass, 3);
        foreach (var ledge in sim.Ledges)
        {
            Assert.Equal(0f, ledge.SumHeight(), 3);
        }
    }

    [Fact]
    public void ClosedWindow_RemovesLedge()
    {
        var settings = Settings();
        var sim = new SnowSimulation(seed: 12);
        var window = Window(100, 300, 500, 500);
        sim.UpdateScene(Scene(window), null, settings);
        Run(sim, settings, 5);
        Assert.Single(sim.Ledges);

        sim.UpdateScene(Scene(), null, settings);

        Assert.Empty(sim.Ledges);
        Assert.True(sim.ParticleCount > 0, "После закрытия окна снег должен ссыпаться частицами.");
    }

    [Fact]
    public void Ledge_MovesWithWindow()
    {
        var settings = Settings();
        var sim = new SnowSimulation(seed: 13);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, settings);
        var ledge = sim.Ledges[0];

        sim.UpdateScene(Scene(Window(300, 300, 700, 500)), null, settings);

        Assert.Equal(300f, ledge.TargetLeft, 1);
        Run(sim, settings, 1);
        Assert.Equal(300f, ledge.Left, 0);
        Assert.Equal(700f, ledge.Right, 0);
    }

    [Fact]
    public void LedgePartiallyCoveredByWindowAbove_HasReducedVisibleSpans()
    {
        var settings = Settings();
        var sim = new SnowSimulation(seed: 14);
        var below = Window(100, 300, 700, 500, z: 1);
        var above = Window(0, 250, 400, 600, z: 0);
        sim.UpdateScene(Scene(above, below), null, settings);

        var ledge = sim.Ledges.Single(l => l.Owner == below.Handle && l.Side == LedgeSide.Top);

        // Видимой остаётся только часть правее перекрывающего окна.
        Assert.True(ledge.VisibleSpans.Length > 0);
        Assert.False(ledge.IsVisibleAt(250f));
        Assert.True(ledge.IsVisibleAt(600f));
    }

    [Fact]
    public void DesktopBottomSnow_IsHiddenWhereWindowCoversTheLine()
    {
        var settings = Settings(screenBottom: true, windowTops: false);
        var sim = new SnowSimulation(seed: 21);
        sim.UpdateScene(Scene(Window(100, 200, 500, 590)), null, settings);

        var ledge = Assert.Single(sim.Ledges);
        Assert.True(ledge.IsDesktopLedge);
        Assert.Equal(560f, ledge.EdgeY, 3);

        // Окно уходит под панель задач, поэтому низ рабочей области за ним не виден.
        Assert.Equal(4, ledge.VisibleSpans.Length);
        Assert.Equal(0f, ledge.VisibleSpans[0], 3);
        Assert.Equal(100f, ledge.VisibleSpans[1], 3);
        Assert.Equal(500f, ledge.VisibleSpans[2], 3);
        Assert.Equal(800f, ledge.VisibleSpans[3], 3);
    }

    [Fact]
    public void DesktopBottomSnow_IsHiddenBehindWindowRestingOnTheLine()
    {
        var settings = Settings(screenBottom: true, windowTops: false);
        var sim = new SnowSimulation(seed: 22);
        sim.UpdateScene(Scene(Window(100, 200, 500, 560)), null, settings);

        var ledge = Assert.Single(sim.Ledges);
        Assert.True(ledge.IsDesktopLedge);

        // Окно заканчивается ровно на линии: рабочего стола под ним не видно,
        // и снег над ним висел бы в воздухе.
        Assert.Equal(4, ledge.VisibleSpans.Length);
        Assert.Equal(0f, ledge.VisibleSpans[0], 3);
        Assert.Equal(100f, ledge.VisibleSpans[1], 3);
        Assert.Equal(500f, ledge.VisibleSpans[2], 3);
        Assert.Equal(800f, ledge.VisibleSpans[3], 3);
    }

    [Fact]
    public void IsInteractiveAt_OnlyNearVisibleLedges()
    {
        var settings = Settings();
        var sim = new SnowSimulation(seed: 31);
        sim.UpdateScene(Scene(Window(100, 200, 700, 500)), null, settings);

        // Верхняя кромка окна на y = 200: чуть выше неё инструмент активен.
        Assert.True(sim.IsInteractiveAt(300f, 190f, 20f));

        // Ниже кромки, далеко выше и левее окна кромки нет.
        Assert.False(sim.IsInteractiveAt(300f, 210f, 20f));
        Assert.False(sim.IsInteractiveAt(300f, 100f, 20f));
        Assert.False(sim.IsInteractiveAt(50f, 190f, 20f));
    }

    [Fact]
    public void DisabledMonitor_GetsNoParticles()
    {
        var second = new MonitorDescriptor("DISPLAY2", "Second",
            RectI.FromSize(800, 0, 800, 600), RectI.FromSize(800, 0, 800, 560), 96, false);
        var scene = new SceneSnapshot(new[] { Monitor, second }, Array.Empty<WindowDescriptor>(), 2);

        var settings = Settings(screenBottom: true, maxParticles: 800);
        var sim = new SnowSimulation(seed: 15);
        sim.UpdateScene(scene, new[] { 1f, 0f }, settings);
        sim.WarmStart(settings, 400);

        foreach (var p in sim.ActiveParticles)
        {
            Assert.True(p.X < 800f, "Снежинка попала на отключённый монитор.");
        }
    }

    [Fact]
    public void PerMonitorIntensity_ChangesSpawnDistribution()
    {
        var second = new MonitorDescriptor("DISPLAY2", "Second",
            RectI.FromSize(800, 0, 800, 600), RectI.FromSize(800, 0, 800, 560), 96, false);
        var scene = new SceneSnapshot(new[] { Monitor, second }, Array.Empty<WindowDescriptor>(), 2);

        var settings = Settings(screenBottom: true, maxParticles: 4000);
        var sim = new SnowSimulation(seed: 16);
        sim.UpdateScene(scene, new[] { 1f, 8f }, settings);
        sim.WarmStart(settings, 3000);

        int onSecond = 0;
        foreach (var p in sim.ActiveParticles)
        {
            if (p.X >= 800f) onSecond++;
        }

        Assert.True(onSecond > 1800, $"Ожидалось большинство частиц на усиленном мониторе, было {onSecond}.");
    }

    [Fact]
    public void SnowDisabled_RemovesParticles()
    {
        var settings = Settings();
        var sim = new SnowSimulation(seed: 17);
        sim.UpdateScene(Scene(), null, settings);
        Run(sim, settings, 2);
        Assert.True(sim.ParticleCount > 0);

        var disabled = settings with { SnowEnabled = false };
        sim.Update(1.0 / 60.0, default, disabled);

        Assert.Equal(0, sim.ParticleCount);
    }

    [Fact]
    public void WarmStart_FillsScreenImmediately()
    {
        var settings = Settings(maxParticles: 300);
        var sim = new SnowSimulation(seed: 18);
        sim.UpdateScene(Scene(), null, settings);

        sim.WarmStart(settings);

        Assert.Equal(300, sim.ParticleCount);

        int belowTop = 0;
        foreach (var p in sim.ActiveParticles)
        {
            if (p.Y > 0) belowTop++;
        }
        Assert.True(belowTop > 100, "Warm start должен распределять снег по всему экрану.");
    }

    [Fact]
    public void LongRun_DoesNotLeakParticlesBeyondBudget()
    {
        var settings = Settings(intensity: 1f, maxParticles: 700, screenBottom: true);
        var sim = new SnowSimulation(seed: 19);
        sim.UpdateScene(Scene(), null, settings);
        sim.WarmStart(settings);

        Run(sim, settings, 120);

        Assert.True(sim.ParticleCount <= 700 + 260); // 260 — запас на «брызги»
    }

    [Fact]
    public void OffscreenLedge_DoesNotConsumeParticles()
    {
        // Окно начинается выше рабочего стола: его верхняя кромка и сугроб на ней не видны.
        // Такая кромка не должна «съедать» фоновые снежинки сразу после спавна.
        var settings = Settings(intensity: 1f, maxParticles: 900);
        var sim = new SnowSimulation(seed: 21);
        sim.UpdateScene(Scene(Window(0, -40, 800, 500)), null, settings);

        Run(sim, settings, 12);

        Assert.True(sim.ParticleCount > 500,
            $"Кромка за экраном съела снежинки: осталось {sim.ParticleCount}");

        var ledge = sim.Ledges.Single(l => l.Side == LedgeSide.Top);
        Assert.Equal(0f, ledge.SumHeight(), 3);
        Assert.Equal(0f, sim.TotalSnowMass, 3);
    }

    [Fact]
    public void ClearMonitor_LeavesOtherMonitorSnow()
    {
        var second = new MonitorDescriptor("DISPLAY2", "Second",
            RectI.FromSize(800, 0, 800, 600), RectI.FromSize(800, 0, 800, 560), 96, false);
        var scene = new SceneSnapshot(
            new[] { Monitor, second },
            new[] { Window(100, 300, 500, 500, z: 0), Window(900, 300, 1300, 500, z: 1) },
            3);

        var settings = Settings(intensity: 1f, maxParticles: 2000);
        var sim = new SnowSimulation(seed: 22, initialCapacity: 4096);
        sim.UpdateScene(scene, new[] { 1f, 1f }, settings);

        Run(sim, settings, 25);

        var leftLedge = sim.Ledges.Single(l => l.Owner == new IntPtr(0x1000));
        var rightLedge = sim.Ledges.Single(l => l.Owner == new IntPtr(0x1001));
        float rightBefore = rightLedge.SumHeight();
        Assert.True(leftLedge.SumHeight() > 1f);
        Assert.True(rightBefore > 1f);

        int cleared = sim.ClearMonitor(Monitor);

        Assert.True(cleared > 0);
        Assert.Equal(0f, leftLedge.SumHeight(), 3);
        Assert.Equal(rightBefore, rightLedge.SumHeight(), 3);
    }

    [Fact]
    public void FindLedgeAt_WindowEdge_ThenClearWindow_LeavesNeighbour()
    {
        var settings = Settings(intensity: 1f, maxParticles: 2200);
        var sim = new SnowSimulation(seed: 23, initialCapacity: 4096);
        var first = Window(100, 300, 500, 500, z: 0);
        var second = Window(550, 300, 790, 500, z: 1);
        sim.UpdateScene(Scene(first, second), null, settings);

        Run(sim, settings, 25);

        var firstLedge = sim.Ledges.Single(l => l.Owner == first.Handle);
        var secondLedge = sim.Ledges.Single(l => l.Owner == second.Handle);
        float secondBefore = secondLedge.SumHeight();
        Assert.True(firstLedge.SumHeight() > 1f);
        Assert.True(secondBefore > 1f);

        // Курсор на верхней кромке первого окна: «очистить под курсором» должно
        // найти именно его кромку и снять снег только с этого окна.
        var hit = sim.FindLedgeAt(300f, 300f);
        Assert.NotNull(hit);
        Assert.Equal(first.Handle, hit!.Owner);

        int cleared = sim.ClearWindow(hit.Owner);

        Assert.True(cleared > 0);
        Assert.Equal(0f, firstLedge.SumHeight(), 3);
        Assert.Equal(secondBefore, secondLedge.SumHeight(), 3);
    }

    [Fact]
    public void FindLedgeAt_DesktopEdge_ClearsWholeMonitor()
    {
        var settings = Settings(screenBottom: true, windowTops: false, intensity: 1f, maxParticles: 2000);
        var sim = new SnowSimulation(seed: 24, initialCapacity: 4096);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, settings);

        Run(sim, settings, 25);

        var ledge = Assert.Single(sim.Ledges);
        Assert.True(ledge.IsDesktopLedge);
        Assert.True(ledge.SumHeight() > 1f);

        var hit = sim.FindLedgeAt(400f, ledge.EdgeY);
        Assert.NotNull(hit);
        Assert.True(hit!.IsDesktopLedge);

        int cleared = sim.ClearMonitor(Monitor);

        Assert.True(cleared > 0);
        Assert.Equal(0f, sim.TotalSnowMass, 3);
    }

    [Fact]
    public void FindLedgeAt_ReturnsNull_FarFromAnyEdge()
    {
        var settings = Settings(screenBottom: true, intensity: 1f, maxParticles: 600);
        var sim = new SnowSimulation(seed: 25);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, settings);
        Run(sim, settings, 3);

        Assert.Null(sim.FindLedgeAt(300f, 100f));
        Assert.Null(sim.FindLedgeAt(50f, 300f)); // X вне обеих кромок
    }

    [Fact]
    public void Update_IsFastEnoughFor60Fps()
    {
        var settings = Settings(intensity: 1f, maxParticles: 3000, screenBottom: true) with { ForegroundShare = 0.35f };
        var scene = Scene(
            Window(100, 100, 700, 400, 0),
            Window(50, 300, 500, 590, 1),
            Window(200, 50, 790, 300, 2));
        var sim = new SnowSimulation(seed: 20, initialCapacity: 4096);
        sim.UpdateScene(scene, null, settings);
        sim.WarmStart(settings);

        // Прогрев JIT.
        for (int i = 0; i < 30; i++) sim.Update(1.0 / 60.0, default, settings);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int frames = 300;
        for (int i = 0; i < frames; i++) sim.Update(1.0 / 60.0, default, settings);
        sw.Stop();

        double msPerFrame = sw.Elapsed.TotalMilliseconds / frames;
        Assert.True(msPerFrame < 8.0, $"Симуляция слишком медленная: {msPerFrame:0.00} мс/кадр");
    }

    // ==================== Этап 3: снежки и снеговики ====================

    private static SimulationSettings SnowballSettings() =>
        Settings(intensity: 1f, maxParticles: 1500) with
        {
            InteractionMode = true,
            ActiveTool = BrushTool.Snowball,
        };

    private static SimulationSettings SnowmanSettings() =>
        Settings(intensity: 1f, maxParticles: 1500) with
        {
            InteractionMode = true,
            ActiveTool = BrushTool.Snowman,
        };

    [Fact]
    public void Snowball_MoldingConsumesSnow_AndHoldsAtCursor()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 30);
        var window = Window(100, 300, 500, 500);
        sim.UpdateScene(Scene(window), null, baseSettings);
        Run(sim, baseSettings, 25);

        var ledge = sim.Ledges[0];
        float massBefore = sim.TotalSnowMass;
        float heightBefore = ledge.SumHeight();
        Assert.True(heightBefore > 5f);

        var snowball = SnowballSettings();
        var press = new SnowInput(300f, 300f, true, true, true);
        sim.Update(1.0 / 60.0, press, snowball);

        var ball = Assert.Single(sim.Snowballs);
        Assert.True(ball.Held, "Снежок должен удерживаться, пока зажата кнопка.");
        Assert.True(sim.TotalSnowMass < massBefore, "Лепка снежка должна расходовать снег.");
        Assert.True(ledge.SumHeight() < heightBefore, "Сугроб должен уменьшиться после лепки.");

        // Удержание: шар следует за курсором и не участвует в физике.
        var move = new SnowInput(340f, 300f, true, true, false);
        for (int i = 0; i < 6; i++) sim.Update(1.0 / 60.0, move, snowball);

        Assert.Single(sim.Snowballs);
        Assert.True(sim.Snowballs[0].Held);
        Assert.True(sim.Snowballs[0].X > 300f);
    }

    [Fact]
    public void Snowball_ThrowFliesWithGravity_AndDisappears()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 31);
        var window = Window(100, 300, 500, 500);
        sim.UpdateScene(Scene(window), null, baseSettings);
        Run(sim, baseSettings, 25);

        var snowball = SnowballSettings();
        sim.Update(1.0 / 60.0, new SnowInput(300f, 300f, true, true, true), snowball);
        var ball = Assert.Single(sim.Snowballs);

        // Резкое движение вправо-вверх и отпускание кнопки.
        sim.Update(1.0 / 60.0, new SnowInput(500f, 300f, true, true, false), snowball);
        sim.Update(1.0 / 60.0, new SnowInput(500f, 260f, true, false, false), snowball);

        Assert.False(ball.Held, "После отпускания снежок должен лететь.");
        Assert.True(ball.Vx > 100f, $"Ожидался бросок вправо, Vx={ball.Vx}");
        Assert.True(ball.Vy < -100f, $"Ожидался бросок вверх, Vy={ball.Vy}");

        float yBefore = ball.Y;
        sim.Update(1.0 / 60.0, default, snowball);
        Assert.True(ball.Y < yBefore, "На подъёме снежок должен двигаться вверх несмотря на гравитацию.");

        // Дальше он падает за пределы окна и удаляется: либо по низу стола, либо по времени жизни.
        Run(sim, snowball, 6);
        Assert.Equal(0, sim.SnowballCount);
    }

    [Fact]
    public void Snowball_DroppedOnLedge_SplatsAndDisappears()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 32);
        var window = Window(100, 300, 500, 500);
        sim.UpdateScene(Scene(window), null, baseSettings);
        Run(sim, baseSettings, 25);

        var snowball = SnowballSettings();
        sim.Update(1.0 / 60.0, new SnowInput(300f, 300f, true, true, true), snowball);
        Assert.Single(sim.Snowballs);

        // Отпускание без движения: снежок падает обратно на кромку и разбивается.
        sim.Update(1.0 / 60.0, new SnowInput(300f, 300f, true, false, false), snowball);
        Run(sim, snowball, 0.5);

        Assert.Equal(0, sim.SnowballCount);
        Assert.True(sim.Ledges[0].SumHeight() > 0f);
    }

    [Fact]
    public void Snowball_RequiresInteractionMode()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 33);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, baseSettings);
        Run(sim, baseSettings, 25);

        var passive = SnowballSettings() with { InteractionMode = false };
        sim.Update(1.0 / 60.0, new SnowInput(300f, 300f, true, true, true), passive);

        Assert.Equal(0, sim.SnowballCount);
    }

    [Fact]
    public void Snowball_MoldingWorksOnThinLedge()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 35);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, baseSettings);
        Run(sim, baseSettings, 12);

        var ledge = sim.Ledges[0];
        float thickness = ledge.HeightAt(300f);
        Assert.InRange(thickness, 0.1f, 8f);

        // Клик по видимой тонкой полоске снега: снег подгребается с более широкой полосы.
        sim.Update(
            1.0 / 60.0,
            new SnowInput(300f, ledge.EdgeY - thickness, true, true, true),
            SnowballSettings());

        var ball = Assert.Single(sim.Snowballs);
        Assert.True(ball.Held);
    }

    [Fact]
    public void Snowman_FirstPressWorksOnThinLedge()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 36);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, baseSettings);
        Run(sim, baseSettings, 12);

        var ledge = sim.Ledges[0];
        float thickness = ledge.HeightAt(300f);
        Assert.InRange(thickness, 0.1f, 8f);

        sim.Update(
            1.0 / 60.0,
            new SnowInput(300f, ledge.EdgeY - thickness, true, true, true),
            SnowmanSettings());

        Assert.Equal(1, sim.SnowmanCount);
    }

    [Fact]
    public void Snowman_ThreePresses_BuildThreeBalls_AndRaiseEvent()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 34);
        var window = Window(100, 300, 500, 500);
        sim.UpdateScene(Scene(window), null, baseSettings);
        Run(sim, baseSettings, 25);

        var events = new List<IReadOnlyList<SnowmanRecord>>();
        sim.SnowmenChanged += snapshot => events.Add(snapshot);

        float massBefore = sim.TotalSnowMass;
        var snowman = SnowmanSettings();

        for (int i = 0; i < 3; i++)
        {
            sim.Update(1.0 / 60.0, new SnowInput(300f, 300f, true, true, true), snowman);
            Assert.Equal(i + 1, sim.Figures.Single().Balls);
        }

        // Четвёртое нажатие не должно добавлять шары.
        sim.Update(1.0 / 60.0, new SnowInput(300f, 300f, true, true, true), snowman);

        var figure = Assert.Single(sim.Figures);
        Assert.Equal(3, figure.Balls);
        Assert.Equal(300f, figure.BaseY, 1);
        Assert.True(sim.TotalSnowMass < massBefore, "Сборка снеговика должна расходовать снег.");
        Assert.Equal(3, events.Count);
        Assert.Single(events[^1]);
        Assert.Equal(3, events[^1][0].Balls);
    }

    [Fact]
    public void Snowman_RequiresInteractionMode()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 35);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, baseSettings);
        Run(sim, baseSettings, 25);

        var passive = SnowmanSettings() with { InteractionMode = false };
        sim.Update(1.0 / 60.0, new SnowInput(300f, 300f, true, true, true), passive);

        Assert.Equal(0, sim.SnowmanCount);
    }

    [Fact]
    public void Snowman_RestoredFromSettings_OnlyOnce()
    {
        var record = new SnowmanRecord
        {
            Monitor = "DISPLAY1",
            X = 300,
            BaseY = 300,
            Scale = 1,
            Balls = 3,
        };

        var settings = Settings() with { Snowmen = new[] { record } };
        var sim = new SnowSimulation(seed: 36);
        var scene = Scene(Window(100, 300, 500, 500));
        sim.UpdateScene(scene, null, settings);

        var figure = Assert.Single(sim.Figures);
        Assert.Equal(3, figure.Balls);
        Assert.Equal(300f, figure.BaseY, 1);

        // Повторный снимок сцены не должен создавать дубликат.
        sim.UpdateScene(scene, null, settings);
        Assert.Single(sim.Figures);

        // Даже если настройки опустеют, движок остаётся источником истины.
        sim.UpdateScene(scene, null, settings with { Snowmen = Array.Empty<SnowmanRecord>() });
        Assert.Single(sim.Figures);
    }

    [Fact]
    public void Snowman_RestoredWithoutLedge_SitsOnWorkAreaBottom()
    {
        var record = new SnowmanRecord
        {
            Monitor = "DISPLAY1",
            X = 300,
            BaseY = 300,
            Scale = 1,
            Balls = 2,
        };

        // Накопление выключено: кромок нет, снеговик ставится на низ рабочей области.
        var settings = Settings(accumulation: false) with { Snowmen = new[] { record } };
        var sim = new SnowSimulation(seed: 37);
        sim.UpdateScene(Scene(), null, settings);

        var figure = Assert.Single(sim.Figures);
        Assert.Equal(0, figure.LedgeKey);
        Assert.Equal(Monitor.WorkArea.Bottom, figure.BaseY, 1);

        Run(sim, settings, 0.2);
        Assert.Equal(Monitor.WorkArea.Bottom, figure.BaseY, 1);
    }

    [Fact]
    public void Snowman_ClosedWindow_RemovesFigureWithEvent()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 38);
        var window = Window(100, 300, 500, 500);
        sim.UpdateScene(Scene(window), null, baseSettings);
        Run(sim, baseSettings, 25);

        var snowman = SnowmanSettings();
        sim.Update(1.0 / 60.0, new SnowInput(300f, 300f, true, true, true), snowman);
        Assert.Single(sim.Figures);

        var events = new List<IReadOnlyList<SnowmanRecord>>();
        sim.SnowmenChanged += snapshot => events.Add(snapshot);

        sim.UpdateScene(Scene(), null, snowman);
        sim.Update(1.0 / 60.0, default, snowman);

        Assert.Equal(0, sim.SnowmanCount);
        Assert.NotEmpty(events);
        Assert.Empty(events[^1]);
    }

    [Fact]
    public void ClearAllSnow_RemovesSnowmenAndSnowballs()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1500);
        var sim = new SnowSimulation(seed: 39);
        sim.UpdateScene(Scene(Window(100, 300, 500, 500)), null, baseSettings);
        Run(sim, baseSettings, 25);

        var snowman = SnowmanSettings();
        sim.Update(1.0 / 60.0, new SnowInput(300f, 300f, true, true, true), snowman);
        Assert.Single(sim.Figures);

        var snowball = SnowballSettings();
        sim.Update(1.0 / 60.0, new SnowInput(400f, 300f, true, true, true), snowball);
        Assert.Single(sim.Snowballs);

        bool emptied = false;
        sim.SnowmenChanged += snapshot => emptied = snapshot.Count == 0;

        sim.ClearAllSnow();

        Assert.Equal(0, sim.SnowmanCount);
        Assert.Equal(0, sim.SnowballCount);
        Assert.True(emptied, "Очистка должна уведомить о пустом списке снеговиков.");
    }

    [Fact]
    public void ClearWindow_RemovesSnowmanOnThatWindow()
    {
        var baseSettings = Settings(intensity: 1f, maxParticles: 1800);
        var sim = new SnowSimulation(seed: 40);
        var window = Window(100, 300, 500, 500);
        sim.UpdateScene(Scene(window), null, baseSettings);
        Run(sim, baseSettings, 25);

        var snowman = SnowmanSettings();
        sim.Update(1.0 / 60.0, new SnowInput(300f, 300f, true, true, true), snowman);
        Assert.Single(sim.Figures);

        sim.ClearWindow(window.Handle);

        Assert.Equal(0, sim.SnowmanCount);
    }

    [Fact]
    public void ClearedSnowmen_AreNotRestoredFromStaleSettings()
    {
        var record = new SnowmanRecord
        {
            Monitor = "DISPLAY1",
            X = 300,
            BaseY = 300,
            Scale = 1,
            Balls = 3,
        };

        var settings = Settings() with { Snowmen = new[] { record } };
        var sim = new SnowSimulation(seed: 41);
        var scene = Scene(Window(100, 300, 500, 500));
        sim.UpdateScene(scene, null, settings);
        Assert.Single(sim.Figures);

        sim.ClearAllSnow();
        Assert.Equal(0, sim.SnowmanCount);

        // Настройки могут ещё содержать старый снимок: восстановление выполняется один раз.
        sim.UpdateScene(scene, null, settings);
        Assert.Equal(0, sim.SnowmanCount);
    }
}




