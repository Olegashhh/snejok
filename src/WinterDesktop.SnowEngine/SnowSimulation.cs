using System.Numerics;
using WinterDesktop.Core.Geometry;
using WinterDesktop.Core.Models;

namespace WinterDesktop.SnowEngine;

/// <summary>
/// Симуляция снега: генерация частиц, ветер, столкновения с кромками окон, накопление
/// сугробов и инструменты взаимодействия.
///
/// Инварианты:
/// * все координаты — физические пиксели виртуального рабочего стола;
/// * в кадре нет аллокаций (частицы живут в одном массиве, удаление — swap-remove);
/// * движок не знает о Win32 и графике, поэтому полностью тестируется в юнит-тестах.
/// </summary>
public sealed class SnowSimulation
{
    public const int HardParticleCap = 24000;

    /// <summary>Жёсткий предел лампочек на все нити: защита от сотен широких окон.</summary>
    public const int MaxGarlandBulbs = 4096;

    private const float DensityPerMegapixel = 120f;
    private const float BaseFallSpeed = 95f;
    private const float MaxWindSpeed = 280f;
    private const int MaxBurstParticles = 260;
    private const float BurstLifetimeSeconds = 1.2f;

    private const int MaxSnowballs = 32;
    private const int MaxFigures = 24;
    private const float SnowballRadiusDip = 15f;
    private const float SnowballCostFactor = 0.8f;
    private const float FigureCostFactor = 0.85f;
    private const float BallGravity = 950f;
    private const float MaxBallSpeed = 1800f;
    private const float BallLifetimeSeconds = 14f;
    private const float MinThrowSpeed = 90f;

    private readonly SnowRandom _rng;
    private readonly List<Ledge> _ledges = new();
    private readonly Dictionary<long, Ledge> _ledgesByKey = new();
    private readonly HashSet<long> _seenLedges = new();
    private readonly List<Vector2> _intervalScratch = new();
    private readonly List<Vector2> _garlandScratch = new();
    private readonly List<GarlandBulb> _garlandBulbsScratch = new();
    private readonly List<SnowBall> _balls = new();
    private readonly List<SnowFigure> _figures = new();
    private readonly List<GarlandString> _garlands = new();
    private int _garlandBulbCount;

    private SnowBall? _heldBall;
    private bool _primaryWasDown;
    private bool _figuresRestored;

    private SnowParticle[] _particles;
    private int _count;
    private int _burstCount;

    private SceneSnapshot _scene = SceneSnapshot.Empty;
    private MonitorDescriptor[] _monitors = Array.Empty<MonitorDescriptor>();
    private float[] _monitorScales = Array.Empty<float>();
    private float[] _spawnCumulative = Array.Empty<float>();
    private float _spawnTotal;

    private RectI _virtualBounds;
    private float _massCap = 200_000f;
    private float _totalMass;

    private double _time;
    private float _spawnAccumulator;

    private float _cursorPrevX;
    private float _cursorPrevY;
    private float _cursorVx;
    private float _cursorVy;
    private bool _hasCursorHistory;

    public SnowSimulation(int seed = 20240117, int initialCapacity = 4096)
    {
        _rng = new SnowRandom(seed);
        _particles = new SnowParticle[Math.Clamp(initialCapacity, 256, HardParticleCap)];
    }

    public int ParticleCount => _count;

    public int LedgeCount => _ledges.Count;

    public IReadOnlyList<Ledge> Ledges => _ledges;

    public ReadOnlySpan<SnowParticle> ActiveParticles => _particles.AsSpan(0, _count);

    /// <summary>Текущая масса накопленного снега в условных px².</summary>
    public float TotalSnowMass => _totalMass;

    public IReadOnlyList<SnowBall> Snowballs => _balls;

    public IReadOnlyList<SnowFigure> Figures => _figures;

    /// <summary>Нити гирлянд на видимых верхних кромках окон (раскладка пересобирается при смене сцены).</summary>
    public IReadOnlyList<GarlandString> Garlands => _garlands;

    public int GarlandCount => _garlands.Count;

    public int GarlandBulbCount => _garlandBulbCount;

    /// <summary>Время симуляции в секундах — из него рендер берёт фазу мигания.</summary>
    public float Time => (float)_time;

    public int SnowballCount => _balls.Count;

    public int SnowmanCount => _figures.Count;

    /// <summary>
    /// Снеговики изменились (собран новый шар, фигура исчезла, снег очищен).
    /// Событие поднимается на потоке эффектов и передаёт неизменяемый снимок для сохранения.
    /// </summary>
    public event Action<IReadOnlyList<SnowmanRecord>>? SnowmenChanged;

    public SceneSnapshot Scene => _scene;

    /// <summary>
    /// Пересобирает набор кромок под актуальную геометрию окон. Вызывается при изменении
    /// сцены (трекер) или настроек, влияющих на набор поверхностей. Сугробы существующих
    /// окон сохраняются — это и есть «логическая привязка» из §3.2.
    /// </summary>
    /// <param name="monitorScales">Множитель интенсивности на монитор (0 — монитор выключен).</param>
    public void UpdateScene(SceneSnapshot scene, float[]? monitorScales, SimulationSettings settings)
    {
        _scene = scene;
        _virtualBounds = scene.VirtualBounds;
        _massCap = MathF.Max(250_000f, _virtualBounds.Area * 0.35f);

        BuildMonitorTable(scene, monitorScales);

        _seenLedges.Clear();
        bool accumulation = settings.AccumulationEnabled;

        foreach (var window in scene.Windows)
        {
            if (!window.IsEligibleForLedges) continue;

            float centerX = (window.Bounds.Left + window.Bounds.Right) * 0.5f;
            bool atWorkAreaBottom = IsAtWorkAreaBottom(window.Bounds.Bottom, centerX);

            if (window.Kind == WindowKind.Taskbar)
            {
                // Задача: не рисовать два сугроба на одной линии. Верх панели задач обычно
                // совпадает с низом рабочей области — в этом случае снег копит кромка
                // рабочего стола, а кромка панели не создаётся.
                if (accumulation && settings.Taskbar && !(settings.ScreenBottom && atWorkAreaBottom))
                {
                    TouchLedge(window, LedgeSide.Top, settings);
                }
                continue;
            }

            if (accumulation && settings.WindowTops) TouchLedge(window, LedgeSide.Top, settings);

            // Подоконник окна, стоящего ровно на низу рабочей области, дублировал бы
            // кромку панели задач / рабочего стола — оставляем одну поверхность.
            bool skipBottom = atWorkAreaBottom && (settings.ScreenBottom || settings.Taskbar);
            if (accumulation && settings.WindowBottoms && !skipBottom) TouchLedge(window, LedgeSide.Bottom, settings);
        }

        if (accumulation && settings.ScreenBottom)
        {
            for (int i = 0; i < scene.Monitors.Count; i++)
            {
                TouchDesktopLedge(i, scene.Monitors[i], settings);
            }
        }

        RemoveStaleLedges();

        for (int i = 0; i < _ledges.Count; i++) RecomputeVisibility(_ledges[i]);

        RebuildGarlands(settings);

        // Снеговики восстанавливаются один раз — при первом снимке с реальными мониторами.
        // Дальше источником истины становится движок, а не файл настроек.
        if (!_figuresRestored && scene.Monitors.Count > 0)
        {
            _figuresRestored = true;
            RestoreSnowmen(settings.Snowmen);
        }

        // Кэш «массы» мог стать неточным после удаления кромок — пересчитываем.
        RecalculateMass();
    }

    private void BuildMonitorTable(SceneSnapshot scene, float[]? scales)
    {
        int count = scene.Monitors.Count;
        _monitors = new MonitorDescriptor[count];
        _monitorScales = new float[count];
        _spawnCumulative = new float[count];
        _spawnTotal = 0f;

        for (int i = 0; i < count; i++)
        {
            _monitors[i] = scene.Monitors[i];
            float scale = scales is not null && i < scales.Length ? scales[i] : 1f;
            _monitorScales[i] = MathF.Max(0f, scale);
            float area = scene.Monitors[i].Bounds.Area / 1_000_000f;
            if (_monitorScales[i] > 0f)
            {
                _spawnTotal += area * _monitorScales[i];
            }
            _spawnCumulative[i] = _spawnTotal;
        }
    }

    /// <summary>
    /// Совпадает ли горизонтальная линия с низом рабочей области монитора. Нужно, чтобы
    /// не создавать две кромки на одной линии (панель задач/подоконник и кромка стола).
    /// </summary>
    private bool IsAtWorkAreaBottom(float y, float centerX)
    {
        for (int i = 0; i < _monitors.Length; i++)
        {
            var monitor = _monitors[i];
            if (centerX < monitor.Bounds.Left || centerX > monitor.Bounds.Right) continue;

            var area = monitor.WorkArea.Width > 0 ? monitor.WorkArea : monitor.Bounds;
            if (MathF.Abs(area.Bottom - y) <= 3f) return true;
        }

        return false;
    }

    private void TouchLedge(WindowDescriptor window, LedgeSide side, SimulationSettings settings)    {
        var bounds = window.Bounds;
        float left = MathF.Max(bounds.Left, _virtualBounds.Left);
        float right = MathF.Min(bounds.Right, _virtualBounds.Right);
        if (right - left < 4f) return;

        float edgeY = side == LedgeSide.Top ? bounds.Top : bounds.Bottom;
        long key = MakeKey(window.Handle, side);

        if (!_ledgesByKey.TryGetValue(key, out var ledge))
        {
            ledge = new Ledge(key, window.Handle, side, isDesktopLedge: false);
            ledge.HeightVariation = VariationFor(key);
            _ledgesByKey[key] = ledge;
            _ledges.Add(ledge);
            ledge.SnapToTarget();
        }

        bool isNew = !_seenLedges.Contains(key);
        _seenLedges.Add(key);

        float scale = ScaleAt((left + right) * 0.5f, edgeY);
        ledge.SetTargetGeometry(new RectF(left, edgeY, right, edgeY), edgeY);
        ledge.Scale = scale;
        ledge.MaxHeight = settings.MaxThicknessDip * scale * ledge.HeightVariation;
        ledge.ZIndex = window.ZOrder;
        ledge.TargetEdgeY = edgeY;
        ledge.TargetLeft = left;
        ledge.TargetRight = right;

        int samples = Math.Clamp((int)((right - left) / 5f), Ledge.MinSamples, Ledge.MaxSamples);
        ledge.EnsureSamples(samples);
        ledge.SampleStep = (right - left) / ledge.SampleCount;

        if (isNew) ledge.SnapToTarget();
    }

    private void TouchDesktopLedge(int monitorIndex, MonitorDescriptor monitor, SimulationSettings settings)
    {
        long key = -(monitorIndex + 1L);
        var area = monitor.WorkArea.Width > 0 ? monitor.WorkArea : monitor.Bounds;
        float left = MathF.Max(area.Left, _virtualBounds.Left);
        float right = MathF.Min(area.Right, _virtualBounds.Right);
        if (right - left < 4f) return;

        float edgeY = area.Bottom;
        if (!_ledgesByKey.TryGetValue(key, out var ledge))
        {
            ledge = new Ledge(key, IntPtr.Zero, LedgeSide.Top, isDesktopLedge: true);
            ledge.HeightVariation = VariationFor(key);
            _ledgesByKey[key] = ledge;
            _ledges.Add(ledge);
        }

        bool isNew = !_seenLedges.Contains(key);
        _seenLedges.Add(key);

        float scale = monitor.Scale;
        ledge.TargetLeft = left;
        ledge.TargetRight = right;
        ledge.TargetEdgeY = edgeY;
        ledge.Scale = scale;
        ledge.MaxHeight = settings.MaxThicknessDip * scale * ledge.HeightVariation;
        ledge.ZIndex = int.MaxValue;

        int samples = Math.Clamp((int)((right - left) / 5f), Ledge.MinSamples, Ledge.MaxSamples);
        ledge.EnsureSamples(samples);
        ledge.SampleStep = (right - left) / ledge.SampleCount;

        if (isNew) ledge.SnapToTarget();
    }

    private void RemoveStaleLedges()
    {
        for (int i = _ledges.Count - 1; i >= 0; i--)
        {
            var ledge = _ledges[i];
            if (_seenLedges.Contains(ledge.Key)) continue;

            // Окно закрыли/скрыли: снег ссыпается вниз, а не исчезает бесследно (§3.2).
            SpawnCollapseBurst(ledge);
            _ledgesByKey.Remove(ledge.Key);
            _ledges.RemoveAt(i);
        }
    }

    private void RecomputeVisibility(Ledge ledge)
    {
        _intervalScratch.Clear();
        _intervalScratch.Add(new Vector2(ledge.Left, ledge.Right));

        if (ledge.IsDesktopLedge)
        {
            // Низ рабочей области — это верх панели задач (или край экрана). Если окно
            // доходит до этой линии или уходит под панель задач, рабочий стол там не
            // виден: снег над таким окном висел бы в воздухе. Саму панель задач
            // пропускаем — она и есть поверхность, на которой лежит снег.
            var sceneWindows = _scene.Windows;
            for (int i = 0; i < sceneWindows.Count; i++)
            {
                var window = sceneWindows[i];
                if (window.Kind == WindowKind.Taskbar || window.Kind == WindowKind.Shell) continue;

                var b = window.Bounds;
                if (b.Top > ledge.EdgeY || b.Bottom < ledge.EdgeY - 0.5f) continue;
                SubtractInterval(b.Left, b.Right);
            }
        }
        else if (ledge.Owner != IntPtr.Zero)
        {
            float bandTop = ledge.EdgeY - ledge.MaxHeight - 1f;
            float bandBottom = ledge.EdgeY + 1f;
            var windows = _scene.Windows;

            for (int i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                if (window.ZOrder >= ledge.ZIndex) break; // ниже — не перекрывает
                if (window.Handle == ledge.Owner) continue;

                var b = window.Bounds;
                if (b.Bottom <= bandTop || b.Top >= bandBottom) continue;
                SubtractInterval(b.Left, b.Right);
            }
        }

        var spans = new float[_intervalScratch.Count * 2];
        for (int i = 0; i < _intervalScratch.Count; i++)
        {
            spans[i * 2] = _intervalScratch[i].X;
            spans[i * 2 + 1] = _intervalScratch[i].Y;
        }
        ledge.SetVisibleSpans(spans);
    }

    private void SubtractInterval(float left, float right) => SubtractFrom(_intervalScratch, left, right);

    private static void SubtractFrom(List<Vector2> intervals, float left, float right)
    {
        for (int i = intervals.Count - 1; i >= 0; i--)
        {
            var interval = intervals[i];
            if (right <= interval.X || left >= interval.Y) continue;

            intervals.RemoveAt(i);
            int insertAt = i;
            if (interval.X < left)
            {
                intervals.Insert(insertAt++, new Vector2(interval.X, left));
            }
            if (right < interval.Y)
            {
                intervals.Insert(insertAt, new Vector2(right, interval.Y));
            }
        }
    }

    // ------------------------------------------------------------------
    // Гирлянды (этап 3)
    // ------------------------------------------------------------------

    /// <summary>
    /// Строит нити гирлянд по верхним кромкам видимых окон. Вызывается только при смене
    /// сцены или настроек: в кадре готовая раскладка не меняется и не аллоцирует память.
    /// Гирлянды не зависят от накопления снега — их можно включить отдельно.
    /// </summary>
    private void RebuildGarlands(SimulationSettings settings)
    {
        _garlands.Clear();
        _garlandBulbCount = 0;
        if (!settings.GarlandsEnabled || _monitors.Length == 0) return;

        float spacingDip = Math.Clamp(settings.GarlandSpacingDip, 8f, 96f);
        float sizeDip = Math.Clamp(settings.GarlandBulbSizeDip, 2f, 12f);
        var windows = _scene.Windows;

        for (int w = 0; w < windows.Count && _garlandBulbCount < MaxGarlandBulbs; w++)
        {
            var window = windows[w];
            if (!window.IsEligibleForLedges) continue;
            if (window.Kind == WindowKind.Taskbar) continue;

            var bounds = window.Bounds;
            float edgeY = bounds.Top;
            if (edgeY < _virtualBounds.Top - 2f || edgeY > _virtualBounds.Bottom) continue;

            float left = MathF.Max(bounds.Left, _virtualBounds.Left);
            float right = MathF.Min(bounds.Right, _virtualBounds.Right);
            if (right - left < 8f) continue;

            int monitorIndex = FindMonitorAt((left + right) * 0.5f, edgeY);
            if (monitorIndex < 0) continue;
            if (monitorIndex >= _monitorScales.Length || _monitorScales[monitorIndex] <= 0f) continue;

            float scale = _monitors[monitorIndex].Scale;
            float spacing = MathF.Max(6f, spacingDip * scale);
            float bulbRadius = MathF.Max(1.2f, sizeDip * 0.5f * scale);
            float wireY = edgeY + 2.2f * scale;
            float bulbY = wireY + bulbRadius + 1.4f * scale;

            // Окна выше по z-order прячут и провод, и лампочки в своей полосе X.
            ComputeGarlandSpans(window, left, right, edgeY - 2f * scale, bulbY + bulbRadius + 2f * scale);
            if (_garlandScratch.Count == 0) continue;

            _garlandBulbsScratch.Clear();
            long key = MakeKey(window.Handle, LedgeSide.Top);
            float stringPhase = GarlandTiming.PhaseFor(key);
            int room = MaxGarlandBulbs - _garlandBulbCount;

            for (int s = 0; s < _garlandScratch.Count && _garlandBulbsScratch.Count < room; s++)
            {
                AppendGarlandBulbs(
                    _garlandScratch[s].X,
                    _garlandScratch[s].Y,
                    left,
                    spacing,
                    bulbY,
                    stringPhase,
                    room);
            }

            if (_garlandBulbsScratch.Count == 0) continue;

            var bulbs = _garlandBulbsScratch.ToArray();
            _garlands.Add(new GarlandString(window.Handle, scale, edgeY, wireY, bulbRadius, spacing, stringPhase, bulbs));
            _garlandBulbCount += bulbs.Length;
        }
    }

    /// <summary>Прячет нить за окнами выше по z-order в полосе вокруг верхней кромки.</summary>
    private void ComputeGarlandSpans(WindowDescriptor owner, float left, float right, float bandTop, float bandBottom)
    {
        _garlandScratch.Clear();
        _garlandScratch.Add(new Vector2(left, right));

        var windows = _scene.Windows;
        for (int i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (window.ZOrder >= owner.ZOrder) break; // ниже — не перекрывает
            if (window.Handle == owner.Handle) continue;

            var b = window.Bounds;
            if (b.Bottom <= bandTop || b.Top >= bandBottom) continue;
            SubtractFrom(_garlandScratch, b.Left, b.Right);
        }
    }

    /// <summary>Раскладывает лампочки внутри видимого интервала нити с постоянным шагом.</summary>
    private void AppendGarlandBulbs(
        float spanLeft,
        float spanRight,
        float stringLeft,
        float spacing,
        float bulbY,
        float stringPhase,
        int limit)
    {
        if (spanRight - spanLeft < 1f || spacing <= 0.01f) return;

        float firstX = spanLeft + spacing * 0.35f;
        float lastX = spanRight - spacing * 0.35f;
        int first = (int)MathF.Ceiling((firstX - stringLeft) / spacing);
        int last = (int)MathF.Floor((lastX - stringLeft) / spacing);

        if (last < first)
        {
            // Узкий видимый кусок: одна лампочка по центру, чтобы нить не выглядела пустой.
            if (spanRight - spanLeft < spacing * 0.6f) return;
            float centerX = (spanLeft + spanRight) * 0.5f;
            int centerIndex = Math.Max(0, (int)MathF.Round((centerX - stringLeft) / spacing));
            _garlandBulbsScratch.Add(new GarlandBulb(centerX, bulbY, stringPhase + (centerIndex * 0.63f), centerIndex));
            return;
        }

        for (int i = first; i <= last && _garlandBulbsScratch.Count < limit; i++)
        {
            float x = stringLeft + (i * spacing);
            _garlandBulbsScratch.Add(new GarlandBulb(x, bulbY, stringPhase + (i * 0.63f), i));
        }
    }

    /// <summary>Один шаг симуляции.</summary>
    public void Update(double deltaSeconds, SnowInput input, SimulationSettings settings)
    {
        float dt = (float)Math.Clamp(deltaSeconds, 0.0, 0.1);
        if (dt <= 0f) return;

        _time += dt;

        UpdateCursor(dt, input);

        if (settings.SnowEnabled)
        {
            Spawn(dt, settings);
        }
        else if (_count > 0)
        {
            TrimTo(0);
        }

        SimulateParticles(dt, settings);
        ApplyCursorWake(input, settings);
        ApplyTools(dt, input, settings);
        UpdateBalls(dt, settings);
        RelaxLedges(dt);
        SyncFigures();
    }

    private void Spawn(float dt, SimulationSettings settings)    {
        int target = Math.Min(settings.MaxParticles, (int)(settings.MaxParticles * settings.Intensity));
        target = Math.Clamp(target, 0, HardParticleCap);

        if (_count > target) TrimTo(target);
        if (_spawnTotal <= 0f || _monitors.Length == 0) return;
        if (_count >= target) return;

        float areaMp = MathF.Max(0.05f, _virtualBounds.Area / 1_000_000f);
        float rate = DensityPerMegapixel * areaMp * MathF.Max(0.02f, settings.Intensity);
        _spawnAccumulator += rate * dt;

        int guard = 128;
        while (_spawnAccumulator >= 1f && _count < target && guard-- > 0)
        {
            _spawnAccumulator -= 1f;
            SpawnOne(settings, randomY: false);
        }

        if (_count >= target) _spawnAccumulator = 0f;
    }

    private void SpawnOne(SimulationSettings settings, bool randomY)
    {
        if (_count >= HardParticleCap) return;
        if (_count >= _particles.Length && !Grow()) return;

        int monitorIndex = PickMonitor();
        if (monitorIndex < 0) return;

        var monitor = _monitors[monitorIndex];
        float scale = monitor.Scale;
        var bounds = monitor.Bounds;

        bool large = _rng.Chance((float)settings.LargeFlakeChance);
        // Распределение со смещением к мелким снежинкам: снег выглядит рыхлым, а не
        // однородной белой массой. Крупные хлопья остаются редкими.
        float sizeRoll = _rng.NextFloat();
        float radius = (0.62f + sizeRoll * sizeRoll * 1.9f) * (float)settings.FlakeSizeScale * scale;
        if (large) radius *= _rng.NextRange(1.7f, 2.6f);

        var shape = PickShape(settings, large);

        float x = _rng.NextRange(bounds.Left, bounds.Right);
        float y = randomY
            ? _rng.NextRange(bounds.Top, Math.Max(bounds.Top + 1, bounds.Bottom))
            : bounds.Top - radius - _rng.NextRange(0f, 90f);

        float spreadSpeed = (float)settings.Spread * 34f;
        float windRad = WindAngleRadians(settings);
        float wind = MathF.Cos(windRad) * MathF.Abs((float)settings.WindStrength) * MaxWindSpeed;

        ref var p = ref _particles[_count++];
        p.X = x;
        p.Y = y;
        p.PrevX = x;
        p.PrevY = y;
        p.Vx = wind * 0.5f + _rng.NextRange(-spreadSpeed, spreadSpeed);
        p.Vy = BaseFallSpeed * (float)settings.FallSpeedScale * _rng.NextRange(0.55f, 0.9f);
        p.Radius = radius;
        p.Alpha = _rng.NextRange(0.42f, 1f) * (shape == SnowShape.Fine ? 0.82f : 1f);
        p.Phase = _rng.NextRange(0f, MathF.Tau);
        p.SwayFrequency = _rng.NextRange(0.55f, 2.1f);
        p.SwayAmplitude = _rng.NextRange(6f, 42f) * scale;
        p.Life = 0f;
        p.Shape = shape;
        p.Foreground = _rng.Chance((float)settings.ForegroundShare);
    }

    private SnowShape PickShape(SimulationSettings settings, bool large)
    {
        if (large) return SnowShape.Large;

        float variety = Math.Clamp((float)settings.ShapeVariety, 0f, 1f);
        float roll = _rng.NextFloat();
        if (roll < variety * 0.34f) return SnowShape.Crystal;
        if (roll < variety * 0.66f) return SnowShape.Star;
        if (roll < variety * 0.82f) return SnowShape.Cluster;
        return SnowShape.Fine;
    }

    private int PickMonitor()
    {
        if (_spawnTotal <= 0f) return -1;
        float roll = _rng.NextFloat() * _spawnTotal;
        for (int i = 0; i < _spawnCumulative.Length; i++)
        {
            if (roll <= _spawnCumulative[i] && _monitorScales[i] > 0f) return i;
        }
        // Погрешность округления: берём последний включённый монитор.
        for (int i = _spawnCumulative.Length - 1; i >= 0; i--)
        {
            if (_monitorScales[i] > 0f) return i;
        }
        return -1;
    }

    private bool Grow()
    {
        int next = Math.Min(HardParticleCap, _particles.Length * 2);
        if (next <= _particles.Length) return false;
        Array.Resize(ref _particles, next);
        return true;
    }

    private void SimulateParticles(float dt, SimulationSettings settings)
    {
        float windRad = WindAngleRadians(settings);
        float windMag = MathF.Abs((float)settings.WindStrength) * MaxWindSpeed;
        float windX = MathF.Cos(windRad) * windMag;
        float windY = MathF.Sin(windRad) * windMag * 0.3f;
        float turbulence = (float)settings.Turbulence;

        int i = 0;
        while (i < _count)
        {
            ref var p = ref _particles[i];
            p.PrevX = p.X;
            p.PrevY = p.Y;

            if (p.Life > 0f)
            {
                p.Life -= dt;
                p.Vy += 300f * dt;
                p.Vx *= MathF.Max(0f, 1f - 1.4f * dt);
                p.X += p.Vx * dt;
                p.Y += p.Vy * dt;
                if (p.Life <= 0f)
                {
                    _burstCount--;
                    RemoveAt(i);
                    continue;
                }
                i++;
                continue;
            }

            if (_virtualBounds.IsEmpty)
            {
                i++;
                continue;
            }

            float scale = ScaleAt(p.X, p.Y);
            float sizeFactor = 0.85f + p.Radius * 0.10f;
            float sway = MathF.Sin((float)_time * p.SwayFrequency + p.Phase) * p.SwayAmplitude * turbulence;

            float targetVx = windX * scale * sizeFactor + sway;
            float targetVy = BaseFallSpeed * (float)settings.FallSpeedScale * sizeFactor * scale + windY * scale;

            // Инерция: скорость подтягивается к целевой, а не задаётся мгновенно (§3.1).
            p.Vx += (targetVx - p.Vx) * MathF.Min(1f, 1.5f * dt);
            p.Vy += (targetVy - p.Vy) * MathF.Min(1f, 2.8f * dt);

            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;

            if (TryLedgeCollision(ref p, settings))
            {
                RemoveAt(i);
                continue;
            }

            if (p.Y > _virtualBounds.Bottom + 60f)
            {
                // Ушла вниз — переиспользуем сверху, чтобы сохранить плотность снегопада.
                p.Y = _virtualBounds.Top - _rng.NextRange(10f, 140f);
                p.X = _rng.NextRange(_virtualBounds.Left, _virtualBounds.Right);
                p.PrevX = p.X;
                p.PrevY = p.Y;
            }
            else if (p.X < _virtualBounds.Left - 80f)
            {
                p.X = _virtualBounds.Right + 70f;
                p.PrevX = p.X;
            }
            else if (p.X > _virtualBounds.Right + 80f)
            {
                p.X = _virtualBounds.Left - 70f;
                p.PrevX = p.X;
            }

            i++;
        }
    }

    private bool TryLedgeCollision(ref SnowParticle p, SimulationSettings settings)
    {
        if (!settings.AccumulationEnabled) return false;

        for (int i = 0; i < _ledges.Count; i++)
        {
            var ledge = _ledges[i];
            if (ledge.SampleCount == 0) continue;
            if (p.X < ledge.Left || p.X > ledge.Right) continue;

            // Кромка за пределами виртуального стола: сугроб на ней не виден, поэтому
            // частицы на него не тратим (иначе фоновые снежинки исчезают сразу после спавна).
            if (!ledge.IsDesktopLedge)
            {
                bool offscreen = ledge.Side == LedgeSide.Top
                    ? ledge.EdgeY <= _virtualBounds.Top + 0.5f
                    : ledge.EdgeY >= _virtualBounds.Bottom - 0.5f;
                if (offscreen) continue;
            }

            // Частицы «за окнами» оседают на верхних кромках, «перед окнами» — на нижних
            // подоконниках. Кромки рабочего стола (пол) принимают все частицы.
            bool accepts = ledge.IsDesktopLedge
                           || (p.Foreground ? ledge.Side == LedgeSide.Bottom : ledge.Side == LedgeSide.Top);
            if (!accepts) continue;
            if (!ledge.IsVisibleAt(p.X)) continue;

            float surface = ledge.EdgeY - ledge.HeightAt(p.X);
            if (p.Y < surface || p.PrevY > surface) continue;

            Deposit(ledge, p.X, p.Radius, settings);
            return true;
        }

        return false;
    }

    /// <summary>Добавляет снег в кромку с гауссовым разбросом. Возвращает фактически добавленную массу.</summary>
    private float Deposit(Ledge ledge, float x, float radius, SimulationSettings settings)
    {
        int index = ledge.IndexAt(x);
        if (index < 0) return 0f;

        float mass = (0.30f + radius * 0.45f) * (float)settings.DepositRate;
        var targets = ledge.Targets;
        float step = ledge.SampleStep;
        float added = 0f;

        Span<float> weights = stackalloc float[5] { 0.35f, 0.72f, 1f, 0.72f, 0.35f };
        float weightSum = 3.14f;

        for (int k = -2; k <= 2; k++)
        {
            int i = index + k;
            if (i < 0 || i >= targets.Length) continue;

            float room = ledge.CapAt(i) - targets[i];
            if (room <= 0f) continue;

            float add = MathF.Min(room, mass * weights[k + 2] / weightSum);
            if (add <= 0f) continue;

            targets[i] += add;
            added += add;
        }

        if (added > 0f)
        {
            float area = added * step;
            _totalMass += area;
            if (_totalMass > _massCap) _totalMass = _massCap;
        }

        return added;
    }

    private void RelaxLedges(float dt)
    {
        float riseRate = MathF.Min(1f, 7f * dt);
        float blur = MathF.Min(0.24f, 1.5f * dt);

        for (int li = 0; li < _ledges.Count; li++)
        {
            var ledge = _ledges[li];
            var heights = ledge.Heights;
            var targets = ledge.Targets;
            if (heights.Length == 0) continue;

            for (int i = 0; i < heights.Length; i++)
            {
                float target = MathF.Min(targets[i], ledge.MaxHeight);
                heights[i] += (target - heights[i]) * riseRate;
                if (heights[i] < 0.001f) heights[i] = 0f;
            }

            // Сглаживание сохраняет массу: сугроб «растекается», а не растёт иглой.
            if (heights.Length > 2 && blur > 0f)
            {
                float prev = heights[0];
                for (int i = 1; i < heights.Length - 1; i++)
                {
                    float current = heights[i];
                    float average = (prev + heights[i + 1]) * 0.5f;
                    heights[i] = current + (average - current) * blur;
                    prev = current;
                }
            }

            // Предел крутизны: снег осыпается, пока уклон не станет приемлемым.
            // Без этого каждая снежинка оставляет круглый бугор и кромка выглядит «фестонами».
            float maxDelta = MathF.Max(1.6f, ledge.MaxHeight * 0.19f);
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < heights.Length - 1; i++)
                {
                    float delta = heights[i] - heights[i + 1];
                    float magnitude = MathF.Abs(delta);
                    if (magnitude <= maxDelta) continue;

                    float shift = (magnitude - maxDelta) * 0.45f;
                    if (delta > 0f)
                    {
                        heights[i] -= shift;
                        heights[i + 1] += shift;
                    }
                    else
                    {
                        heights[i] += shift;
                        heights[i + 1] -= shift;
                    }
                }
            }

            for (int i = 0; i < heights.Length; i++)
            {
                if (heights[i] > ledge.MaxHeight) heights[i] = ledge.MaxHeight;
                else if (heights[i] < 0f) heights[i] = 0f;
            }
        }
    }

    private void UpdateCursor(float dt, SnowInput input)
    {
        if (!input.CursorValid)
        {
            _hasCursorHistory = false;
            _cursorVx = 0f;
            _cursorVy = 0f;
            return;
        }

        if (_hasCursorHistory && dt > 0f)
        {
            float vx = (input.CursorX - _cursorPrevX) / dt;
            float vy = (input.CursorY - _cursorPrevY) / dt;
            _cursorVx = _cursorVx * 0.6f + vx * 0.4f;
            _cursorVy = _cursorVy * 0.6f + vy * 0.4f;
        }

        _cursorPrevX = input.CursorX;
        _cursorPrevY = input.CursorY;
        _hasCursorHistory = true;
    }

    /// <summary>Лёгкий «след»: частицы рядом с курсором увлекаются за ним даже без взаимодействия.</summary>
    private void ApplyCursorWake(SnowInput input, SimulationSettings settings)
    {
        if (!input.CursorValid) return;

        float speed = MathF.Sqrt(_cursorVx * _cursorVx + _cursorVy * _cursorVy);
        if (speed < 40f) return;

        float scale = ScaleAt(input.CursorX, input.CursorY);
        float radius = 46f * scale;
        float force = MathF.Min(900f, speed * 0.55f);
        float radiusSq = radius * radius;

        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _particles[i];
            if (p.Life > 0f) continue;

            float dx = p.X - input.CursorX;
            float dy = p.Y - input.CursorY;
            float distSq = dx * dx + dy * dy;
            if (distSq > radiusSq || distSq < 0.01f) continue;

            float dist = MathF.Sqrt(distSq);
            float falloff = 1f - dist / radius;
            p.Vx += _cursorVx * 0.35f * falloff;
            p.Vy += _cursorVy * 0.35f * falloff;
            p.X += (dx / dist) * force * falloff * 0.004f;
            p.Y += (dy / dist) * force * falloff * 0.004f;
        }
    }

    private void ApplyTools(float dt, SnowInput input, SimulationSettings settings)
    {
        // Отпускание кнопки определяется здесь, а не в SnowInput: в нём есть только
        // нажатие и факт удержания, а фронт отпускания нужен для броска снежка.
        bool released = _primaryWasDown && !input.PrimaryDown;
        _primaryWasDown = input.PrimaryDown;

        if (!input.CursorValid)
        {
            DropHeldBall();
            return;
        }

        float scale = ScaleAt(input.CursorX, input.CursorY);

        switch (settings.ActiveTool)
        {
            case BrushTool.Shovel:
            {
                DropHeldBall();
                if (!input.PrimaryDown) return;
                float radius = (float)settings.ShovelRadiusDip * scale;
                ClearCircle(input.CursorX, input.CursorY, radius, burst: true);

                // Даже если под курсором нет сугроба, нажатие должно быть заметным:
                // лопата поднимает небольшую снежную пыль.
                if (input.PrimaryPressed)
                {
                    SpawnBurst(input.CursorX, input.CursorY, 9, 75f);
                }

                return;
            }

            case BrushTool.Snowball:
                UpdateSnowballTool(dt, input, settings, released);
                return;

            case BrushTool.Snowman:
                DropHeldBall();
                if (settings.InteractionMode && input.PrimaryPressed)
                {
                    if (!TryBuildSnowman(input.CursorX, input.CursorY, settings))
                    {
                        // Мягкий отклик на клик, если снега рядом не хватило.
                        SpawnBurst(input.CursorX, input.CursorY, 5, 55f);
                    }
                }

                return;

            default:
            {
                // Варежка работает при включённом режиме взаимодействия.
                DropHeldBall();
                if (!settings.InteractionMode) return;
                float mittenRadius = (float)settings.MittenRadiusDip * scale;
                ErodeCircle(input.CursorX, input.CursorY, mittenRadius, (float)settings.MittenStrength, dt);
                return;
            }
        }
    }

    private void UpdateSnowballTool(float dt, SnowInput input, SimulationSettings settings, bool released)
    {
        // Снежок лепится только в режиме взаимодействия: иначе любой обычный клик
        // по чужому окну превращался бы в бросок снега.
        if (!settings.InteractionMode)
        {
            DropHeldBall();
            return;
        }

        if (input.PrimaryPressed && _heldBall is null)
        {
            if (!TryMoldSnowball(input.CursorX, input.CursorY, settings))
            {
                // Мягкий отклик на клик, если снега рядом не хватило.
                SpawnBurst(input.CursorX, input.CursorY, 5, 55f);
            }
        }

        if (_heldBall is null) return;

        float follow = MathF.Min(1f, 30f * dt);
        _heldBall.X += (input.CursorX - _heldBall.X) * follow;

        // Держим шар чуть выше курсора, чтобы он не «утопал» в сугробе в момент броска.
        float targetY = input.CursorY - _heldBall.Radius * 1.1f;
        _heldBall.Y += (targetY - _heldBall.Y) * follow;
        _heldBall.PrevX = _heldBall.X;
        _heldBall.PrevY = _heldBall.Y;

        if (released) DropHeldBall(throwBall: true);
    }

    private bool TryMoldSnowball(float x, float y, SimulationSettings settings)
    {
        if (_balls.Count >= MaxSnowballs) return false;

        float scale = ScaleAt(x, y);
        var ledge = FindLedgeNear(x, y, 52f * scale);
        if (ledge is null) return false;

        float radius = SnowballRadiusDip * scale;
        float required = radius * radius * SnowballCostFactor;
        float removed = TakeSnow(ledge, x, radius * 1.5f, radius * 0.9f, required);
        if (removed < required * 0.95f) return false;

        var ball = new SnowBall
        {
            X = x,
            Y = y - radius * 1.1f,
            PrevX = x,
            PrevY = y - radius * 1.1f,
            Radius = radius,
            Scale = scale,
            Held = true,
        };

        _balls.Add(ball);
        _heldBall = ball;
        SpawnBurst(x, ledge.EdgeY - ledge.HeightAt(x), 5, 55f);
        return true;
    }

    private void DropHeldBall(bool throwBall = false)
    {
        var ball = _heldBall;
        if (ball is null) return;

        _heldBall = null;
        ball.Held = false;
        ball.PrevX = ball.X;
        ball.PrevY = ball.Y;

        float speed = MathF.Sqrt(_cursorVx * _cursorVx + _cursorVy * _cursorVy);
        if (!throwBall || speed < MinThrowSpeed)
        {
            ball.Vx = throwBall ? _cursorVx * 0.25f : 0f;
            ball.Vy = throwBall ? MathF.Max(0f, _cursorVy * 0.25f) : 0f;
            return;
        }

        float factor = MathF.Min(1f, MaxBallSpeed / speed);
        ball.Vx = _cursorVx * factor;
        ball.Vy = _cursorVy * factor;
    }

    private bool TryBuildSnowman(float x, float y, SimulationSettings settings)
    {
        float scale = ScaleAt(x, y);

        var existing = FindFigureAt(x, y, scale);
        if (existing is not null && existing.Balls >= SnowFigure.MaxBalls) return false;
        if (existing is null && _figures.Count >= MaxFigures) return false;

        Ledge? ledge;
        if (existing is not null)
        {
            if (!_ledgesByKey.TryGetValue(existing.LedgeKey, out ledge)) return false;
        }
        else
        {
            ledge = FindLedgeNear(x, y, 48f * scale);
            if (ledge is null) return false;
        }

        int level = existing?.Balls ?? 0;
        float ballRadius = SnowFigure.BallRadius(scale, Math.Min(level, 2));
        float required = ballRadius * ballRadius * FigureCostFactor;
        float centerX = existing?.X ?? x;

        // Шар скатывается из снега вокруг будущего основания.
        float removed = TakeSnow(ledge, centerX, ballRadius * 1.9f, ballRadius * 0.9f, required);
        if (removed < required * 0.95f) return false;

        if (existing is null)
        {
            float half = SnowFigure.BallRadius(scale, 0) + 5f * scale;
            float minX = ledge.Left + half;
            float maxX = MathF.Max(minX, ledge.Right - half);

            _figures.Add(new SnowFigure
            {
                LedgeKey = ledge.Key,
                MonitorKey = MonitorKeyAt(x, ledge.EdgeY - 1f),
                X = Math.Clamp(x, minX, maxX),
                BaseY = ledge.EdgeY,
                Scale = ledge.Scale,
                Balls = 1,
            });
        }
        else
        {
            existing.Balls++;
            existing.Scale = ledge.Scale;
        }

        SpawnBurst(x, ledge.EdgeY - ledge.HeightAt(x), 6, 70f);
        NotifySnowmenChanged();
        return true;
    }

    private SnowFigure? FindFigureAt(float x, float y, float scale)
    {
        SnowFigure? best = null;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < _figures.Count; i++)
        {
            var figure = _figures[i];
            float half = figure.HalfWidth + 10f * scale;
            if (x < figure.X - half || x > figure.X + half) continue;
            if (y < figure.TopY - 8f * figure.Scale || y > figure.BaseY + 6f * figure.Scale) continue;

            float distance = MathF.Abs(x - figure.X);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = figure;
            }
        }

        return best;
    }

    private Ledge? FindLedgeNear(float x, float y, float tolerance)
    {
        Ledge? best = null;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < _ledges.Count; i++)
        {
            var ledge = _ledges[i];
            if (ledge.SampleCount == 0) continue;
            if (x < ledge.Left || x > ledge.Right) continue;
            if (!ledge.IsVisibleAt(x)) continue;

            float surface = ledge.EdgeY - ledge.HeightAt(x);
            float distance = MathF.Abs(y - surface);
            if (distance > tolerance + ledge.MaxHeight) continue;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = ledge;
            }
        }

        return best;
    }

    /// <summary>
    /// Забирает снег из кромки вокруг X. Снимает и текущую высоту, и накопленную цель,
    /// иначе сугроб мгновенно восстановился бы из Targets. Возвращает реально снятую
    /// площадь (px²); при недостатке снега возвращает 0 и ничего не меняет.
    /// </summary>
    private float TakeSnow(Ledge ledge, float centerX, float halfWidth, float maxDepth, float required)
    {
        if (ledge.SampleCount == 0 || required <= 0f) return 0f;
        float step = ledge.SampleStep;
        if (step <= 0.001f) return 0f;

        // Тонкая кромка не может отдать шар с узкого участка. Снег «подгребается»
        // с более широкой полосы вокруг точки (как если бы шар катили вдоль сугроба),
        // поэтому лепка работает и на обычных тонких полосках снега.
        float span = MathF.Max(1f, halfWidth);
        float maxSpan = MathF.Max(span, MathF.Min(ledge.Right - ledge.Left, span * 9f));
        var heights = ledge.Heights;
        var targets = ledge.Targets;
        int first = 0;
        int last = 0;
        float available = 0f;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            first = Math.Clamp(
                (int)MathF.Floor((centerX - span - ledge.Left) / step),
                0,
                ledge.SampleCount - 1);
            last = Math.Clamp(
                (int)MathF.Ceiling((centerX + span - ledge.Left) / step),
                0,
                ledge.SampleCount - 1);

            available = 0f;
            for (int i = first; i <= last; i++)
            {
                available += MathF.Min(heights[i], maxDepth);
            }
            available *= step;

            if (available >= required || span >= maxSpan) break;
            span = MathF.Min(maxSpan, span * 1.7f);
        }

        if (last < first || available < required) return 0f;

        float remaining = required;
        int count = last - first + 1;
        int guard = 8;

        while (remaining > 0.001f && guard-- > 0)
        {
            float before = remaining;
            float share = remaining / count;

            for (int i = first; i <= last && remaining > 0.001f; i++)
            {
                float capacity = MathF.Min(heights[i], maxDepth) * step;
                if (capacity <= 0f) continue;

                float take = MathF.Min(capacity, MathF.Min(share, remaining));
                float delta = take / step;
                heights[i] -= delta;
                targets[i] = MathF.Min(MathF.Max(0f, targets[i] - delta), heights[i]);
                remaining -= take;
            }

            if (before - remaining < 0.001f) break;
        }

        float removed = required - MathF.Max(0f, remaining);
        if (removed <= 0f) return 0f;

        _totalMass = MathF.Max(0f, _totalMass - removed);
        return removed;
    }

    private void UpdateBalls(float dt, SimulationSettings settings)
    {
        if (_balls.Count == 0) return;

        float windRad = WindAngleRadians(settings);
        float windX = MathF.Cos(windRad) * MathF.Abs((float)settings.WindStrength) * 60f;

        for (int i = _balls.Count - 1; i >= 0; i--)
        {
            var ball = _balls[i];
            if (ball.Held) continue;

            ball.Age += dt;
            ball.PrevX = ball.X;
            ball.PrevY = ball.Y;

            ball.Vy += BallGravity * dt;
            ball.Vx += windX * 0.35f * dt;
            ball.Vx *= MathF.Max(0f, 1f - 0.12f * dt);
            ball.X += ball.Vx * dt;
            ball.Y += ball.Vy * dt;
            ball.Spin += ball.Vx * dt * 0.35f / MathF.Max(1f, ball.Radius);

            if (ball.Age > BallLifetimeSeconds
                || ball.Y > _virtualBounds.Bottom + 240f
                || ball.X < _virtualBounds.Left - 240f
                || ball.X > _virtualBounds.Right + 240f)
            {
                RemoveBall(ball);
                continue;
            }

            if (TryBallLedgeCollision(ball, settings)) continue;
            if (TryBallWindowCollision(ball)) continue;
        }
    }

    private bool TryBallLedgeCollision(SnowBall ball, SimulationSettings settings)
    {
        // На подъёме и при почти горизонтальном полёте снежок кромку не задевает:
        // иначе он разбивался бы в момент броска, едва оторвавшись от сугроба.
        if (ball.Vy < 20f) return false;

        for (int i = 0; i < _ledges.Count; i++)
        {
            var ledge = _ledges[i];
            if (ledge.SampleCount == 0) continue;
            if (ball.X < ledge.Left || ball.X > ledge.Right) continue;
            if (!ledge.IsVisibleAt(ball.X)) continue;

            float surface = ledge.EdgeY - ledge.HeightAt(ball.X);
            if (ball.Y + ball.Radius < surface) continue;

            Deposit(ledge, ball.X, ball.Radius, settings);
            SpawnBurst(ball.X, surface, 11, 135f);
            RemoveBall(ball);
            return true;
        }

        return false;
    }

    private bool TryBallWindowCollision(SnowBall ball)
    {
        var windows = _scene.Windows;
        int x = (int)ball.X;
        int y = (int)ball.Y;
        int prevX = (int)ball.PrevX;
        int prevY = (int)ball.PrevY;

        for (int i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (!window.IsEligibleForLedges) continue;
            if (!window.Bounds.Contains(x, y)) continue;

            // Снежок, выпущенный из глубины окна (например, с подоконника), не должен
            // разбиваться о стекло в первый же кадр: ждём пересечения границы.
            if (window.Bounds.Contains(prevX, prevY)) continue;

            SpawnBurst(ball.X, ball.Y, 9, 110f);
            RemoveBall(ball);
            return true;
        }

        return false;
    }

    private void RemoveBall(SnowBall ball)
    {
        _balls.Remove(ball);
        if (ReferenceEquals(_heldBall, ball)) _heldBall = null;
    }

    private void SyncFigures()
    {
        if (_figures.Count == 0) return;

        bool changed = false;

        for (int i = _figures.Count - 1; i >= 0; i--)
        {
            var figure = _figures[i];

            // Отвязанное основание (не было подходящей кромки): стоит на низу рабочей области.
            if (figure.LedgeKey == 0)
            {
                int index = FindMonitorIndex(figure.MonitorKey);
                if (index >= 0)
                {
                    var monitor = _monitors[index];
                    var area = monitor.WorkArea.Width > 0 ? monitor.WorkArea : monitor.Bounds;
                    figure.BaseY = area.Bottom;
                }

                continue;
            }

            if (!_ledgesByKey.TryGetValue(figure.LedgeKey, out var ledge))
            {
                SpawnFigureCollapseBurst(figure);
                _figures.RemoveAt(i);
                changed = true;
                continue;
            }

            figure.BaseY = ledge.EdgeY;
            if (ledge.Scale > 0f) figure.Scale = ledge.Scale;

            float half = figure.HalfWidth;
            figure.X = Math.Clamp(figure.X, ledge.Left + half, MathF.Max(ledge.Left + half, ledge.Right - half));

            string key = MonitorKeyAt(figure.X, figure.BaseY - 1f);
            if (!string.IsNullOrEmpty(key)) figure.MonitorKey = key;
        }

        if (changed) NotifySnowmenChanged();
    }

    private void SpawnFigureCollapseBurst(SnowFigure figure)
    {
        int samples = Math.Clamp(figure.Balls * 4, 3, 12);
        for (int i = 0; i < samples; i++)
        {
            float t = samples <= 1 ? 0f : i / (float)(samples - 1);
            float y = figure.TopY + (figure.BaseY - figure.TopY) * t;
            float x = figure.X + _rng.NextRange(-figure.BottomRadius, figure.BottomRadius);
            SpawnBurst(x, y, 1, 85f);
        }
    }

    private string MonitorKeyAt(float x, float y)
    {
        for (int i = 0; i < _monitors.Length; i++)
        {
            if (_monitors[i].Bounds.Contains((int)x, (int)y)) return _monitors[i].StorageKey;
        }

        return _monitors.Length > 0 ? _monitors[0].StorageKey : string.Empty;
    }

    private int FindMonitorIndex(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return -1;

        for (int i = 0; i < _monitors.Length; i++)
        {
            if (string.Equals(_monitors[i].StorageKey, key, StringComparison.OrdinalIgnoreCase)) return i;
            if (string.Equals(_monitors[i].DeviceName, key, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    private int FindMonitorAt(float x, float y)
    {
        for (int i = 0; i < _monitors.Length; i++)
        {
            if (_monitors[i].Bounds.Contains((int)x, (int)y)) return i;
        }

        return -1;
    }

    private void RestoreSnowmen(IReadOnlyList<SnowmanRecord> records)
    {
        if (records.Count == 0 || _monitors.Length == 0) return;

        for (int r = 0; r < records.Count && _figures.Count < MaxFigures; r++)
        {
            var record = records[r];
            if (record is null) continue;

            float requestedX = (float)record.X;
            float requestedY = (float)record.BaseY;

            int monitorIndex = FindMonitorIndex(record.Monitor);
            if (monitorIndex < 0) monitorIndex = FindMonitorAt(requestedX, requestedY);
            if (monitorIndex < 0) continue;

            var monitor = _monitors[monitorIndex];
            var ledge = FindRestoreLedge(requestedX, requestedY, monitor);

            long ledgeKey;
            float x;
            float baseY;
            float scale;

            if (ledge is not null)
            {
                ledgeKey = ledge.Key;
                scale = ledge.Scale > 0f ? ledge.Scale : monitor.Scale;
                float half = SnowFigure.BallRadius(scale, 0) + 5f * scale;
                float minX = ledge.Left + half;
                float maxX = MathF.Max(minX, ledge.Right - half);
                x = Math.Clamp(requestedX, minX, maxX);
                baseY = ledge.EdgeY;
            }
            else
            {
                // Кромки нет (например, накопление выключено): ставим на низ рабочей области.
                ledgeKey = 0;
                scale = monitor.Scale > 0f ? monitor.Scale : (float)Math.Clamp(record.Scale, 0.5, 2.5);
                var area = monitor.WorkArea.Width > 0 ? monitor.WorkArea : monitor.Bounds;
                float margin = 12f * scale;
                x = Math.Clamp(requestedX, area.Left + margin, MathF.Max(area.Left + margin, area.Right - margin));
                baseY = area.Bottom;
            }

            _figures.Add(new SnowFigure
            {
                LedgeKey = ledgeKey,
                MonitorKey = monitor.StorageKey,
                X = x,
                BaseY = baseY,
                Scale = scale,
                Balls = Math.Clamp(record.Balls, 1, SnowFigure.MaxBalls),
            });
        }
    }

    private Ledge? FindRestoreLedge(float x, float baseY, MonitorDescriptor monitor)
    {
        Ledge? best = null;
        float bestScore = float.MaxValue;

        for (int i = 0; i < _ledges.Count; i++)
        {
            var ledge = _ledges[i];
            if (ledge.SampleCount == 0) continue;

            float centerX = (ledge.Left + ledge.Right) * 0.5f;
            if (!monitor.Bounds.Contains((int)centerX, (int)ledge.EdgeY)) continue;

            float score = MathF.Abs(ledge.EdgeY - baseY);
            if (x < ledge.Left - 40f || x > ledge.Right + 40f) score += 1000f;

            if (score < bestScore)
            {
                bestScore = score;
                best = ledge;
            }
        }

        return best;
    }

    /// <summary>Снимок снеговиков для сохранения в настройках.</summary>
    public IReadOnlyList<SnowmanRecord> ExportSnowmen()
    {
        if (_figures.Count == 0) return Array.Empty<SnowmanRecord>();

        var result = new SnowmanRecord[_figures.Count];
        for (int i = 0; i < _figures.Count; i++)
        {
            var figure = _figures[i];
            result[i] = new SnowmanRecord
            {
                Monitor = figure.MonitorKey,
                X = figure.X,
                BaseY = figure.BaseY,
                Scale = figure.Scale,
                Balls = figure.Balls,
            };
        }

        return result;
    }

    private void NotifySnowmenChanged() => SnowmenChanged?.Invoke(ExportSnowmen());

    private void ErodeCircle(float cx, float cy, float radius, float strength, float dt)
    {
        float radiusSq = radius * radius;
        float erodeSpeed = 55f * strength;
        float step = 0f;

        for (int li = 0; li < _ledges.Count; li++)
        {
            var ledge = _ledges[li];
            if (ledge.SampleCount == 0) continue;
            if (cx + radius < ledge.Left || cx - radius > ledge.Right) continue;
            if (cy + radius < ledge.EdgeY - ledge.MaxHeight || cy - radius > ledge.EdgeY + 4f) continue;

            var heights = ledge.Heights;
            var targets = ledge.Targets;
            step = ledge.SampleStep;
            float removedTotal = 0f;

            for (int i = 0; i < heights.Length; i++)
            {
                if (heights[i] <= 0.01f) continue;
                float sx = ledge.Left + (i + 0.5f) * step;
                float sy = ledge.EdgeY - heights[i] * 0.5f;
                float dx = sx - cx;
                float dy = sy - cy;
                float distSq = dx * dx + dy * dy;
                if (distSq > radiusSq) continue;

                float falloff = 1f - MathF.Sqrt(distSq) / radius;
                falloff = falloff * falloff;
                float removed = MathF.Min(heights[i], erodeSpeed * falloff * dt);
                if (removed <= 0f) continue;

                heights[i] -= removed;
                targets[i] = MathF.Max(0f, targets[i] - removed);
                removedTotal += removed;

                if (removed > 0.01f && _rng.Chance(0.20f) && _burstCount < MaxBurstParticles)
                {
                    SpawnBurst(sx, sy, 1, 60f);
                }
            }

            if (removedTotal > 0f)
            {
                _totalMass = MathF.Max(0f, _totalMass - removedTotal * step);
            }
        }
    }

    private void ClearCircle(float cx, float cy, float radius, bool burst)
    {
        float radiusSq = radius * radius;

        for (int li = 0; li < _ledges.Count; li++)
        {
            var ledge = _ledges[li];
            if (ledge.SampleCount == 0) continue;
            if (cx + radius < ledge.Left || cx - radius > ledge.Right) continue;
            if (cy + radius < ledge.EdgeY - ledge.MaxHeight || cy - radius > ledge.EdgeY + 4f) continue;

            var heights = ledge.Heights;
            var targets = ledge.Targets;
            float step = ledge.SampleStep;
            float removedTotal = 0f;

            for (int i = 0; i < heights.Length; i++)
            {
                if (heights[i] <= 0.01f && targets[i] <= 0.01f) continue;
                float sx = ledge.Left + (i + 0.5f) * step;
                float sy = ledge.EdgeY - heights[i] * 0.5f;
                float dx = sx - cx;
                float dy = sy - cy;
                if (dx * dx + dy * dy > radiusSq) continue;

                removedTotal += heights[i];
                heights[i] = 0f;
                targets[i] = 0f;

                if (burst && _rng.Chance(0.25f) && _burstCount < MaxBurstParticles)
                {
                    SpawnBurst(sx, sy, 2, 150f);
                }
            }

            if (removedTotal > 0f) _totalMass = MathF.Max(0f, _totalMass - removedTotal * step);
        }
    }

    private void SpawnBurst(float x, float y, int count, float energy)
    {
        for (int k = 0; k < count; k++)
        {
            if (_burstCount >= MaxBurstParticles || _count >= HardParticleCap) return;
            if (_count >= _particles.Length && !Grow()) return;

            ref var p = ref _particles[_count++];
            p.X = x;
            p.Y = y;
            p.PrevX = x;
            p.PrevY = y;
            float angle = _rng.NextRange(0f, MathF.Tau);
            float speed = energy * _rng.NextRange(0.35f, 1f);
            p.Vx = MathF.Cos(angle) * speed;
            p.Vy = MathF.Sin(angle) * speed - energy * 0.35f;
            p.Radius = _rng.NextRange(0.6f, 1.8f);
            p.Alpha = _rng.NextRange(0.5f, 0.95f);
            p.Phase = 0f;
            p.SwayFrequency = 0f;
            p.SwayAmplitude = 0f;
            p.Life = BurstLifetimeSeconds * _rng.NextRange(0.5f, 1.1f);
            p.Shape = SnowShape.Fine;
            p.Foreground = true;
            _burstCount++;
        }
    }

    private void SpawnCollapseBurst(Ledge ledge)
    {
        if (ledge.SampleCount == 0 || ledge.SumHeight() < 0.5f) return;
        int samples = Math.Min(ledge.SampleCount, 24);
        float step = ledge.Width / samples;
        for (int i = 0; i < samples; i++)
        {
            float x = ledge.Left + (i + 0.5f) * step;
            float h = ledge.HeightAt(x);
            if (h < 1f) continue;
            if (_burstCount >= MaxBurstParticles) return;
            SpawnBurst(x, ledge.EdgeY - h * 0.5f, 1, 40f + h * 2f);
        }
    }

    private void TrimTo(int target)
    {
        while (_count > target)
        {
            int last = _count - 1;
            if (_particles[last].Life > 0f) _burstCount--;
            _count--;
        }
    }

    private void RemoveAt(int index)
    {
        int last = _count - 1;
        if (index != last)
        {
            _particles[index] = _particles[last];
        }
        _count = last;
    }

    private void RecalculateMass()
    {
        float mass = 0f;
        for (int i = 0; i < _ledges.Count; i++)
        {
            var ledge = _ledges[i];
            mass += ledge.SumHeight() * ledge.SampleStep;
        }
        _totalMass = mass;
    }

    private float ScaleAt(float x, float y)
    {
        for (int i = 0; i < _monitors.Length; i++)
        {
            if (_monitors[i].Bounds.Contains((int)x, (int)y)) return _monitors[i].Scale;
        }
        return 1f;
    }

    private static float WindAngleRadians(SimulationSettings settings)
    {
        float degrees = (float)settings.WindAngleDegrees;
        if (settings.WindStrength < 0f) degrees += 180f;
        return degrees * (MathF.PI / 180f);
    }

    private static long MakeKey(IntPtr handle, LedgeSide side) =>
        (handle.ToInt64() << 1) | (long)side;

    /// <summary>
    /// Детерминированный коэффициент 0.85…1.0 по ключу кромки: сугробы на разных окнах
    /// получаются разной высоты, но не «дрожат» между кадрами и не превышают предел.
    /// </summary>
    private static float VariationFor(long key)
    {
        ulong hash = (ulong)key * 0x9E3779B97F4A7C15UL;
        hash ^= hash >> 30;
        hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 27;
        float unit = (hash & 0xFFFFFFUL) / 16777215f;
        return 0.85f + 0.15f * unit;
    }

    // ---------------------------------------------------------------------
    // Публичные команды (трей/UI)
    // ---------------------------------------------------------------------

    /// <summary>Мгновенно убирает весь накопленный снег, снеговиков и снежки (кнопка «Очистить»).</summary>
    public void ClearAllSnow()
    {
        for (int i = 0; i < _ledges.Count; i++)
        {
            Array.Clear(_ledges[i].Heights);
            Array.Clear(_ledges[i].Targets);
        }
        _totalMass = 0f;

        bool hadFigures = _figures.Count > 0;
        _figures.Clear();
        _balls.Clear();
        _heldBall = null;

        if (hadFigures) SnowmenChanged?.Invoke(Array.Empty<SnowmanRecord>());
    }

    /// <summary>Очищает снег только на одном мониторе (§3.9).</summary>
    public int ClearMonitor(MonitorDescriptor monitor)
    {
        int cleared = 0;
        for (int i = 0; i < _ledges.Count; i++)
        {
            var ledge = _ledges[i];
            float centerX = (ledge.Left + ledge.Right) * 0.5f;
            if (centerX < monitor.Bounds.Left || centerX > monitor.Bounds.Right) continue;

            cleared += ClearLedge(ledge);
        }

        int figures = 0;
        for (int i = _figures.Count - 1; i >= 0; i--)
        {
            var figure = _figures[i];
            bool onMonitor = string.Equals(figure.MonitorKey, monitor.StorageKey, StringComparison.OrdinalIgnoreCase)
                             || monitor.Bounds.Contains((int)figure.X, (int)figure.BaseY);
            if (!onMonitor) continue;

            _figures.RemoveAt(i);
            figures++;
        }

        for (int i = _balls.Count - 1; i >= 0; i--)
        {
            var ball = _balls[i];
            if (!monitor.Bounds.Contains((int)ball.X, (int)ball.Y)) continue;

            _balls.RemoveAt(i);
            if (ReferenceEquals(_heldBall, ball)) _heldBall = null;
        }

        if (figures > 0) NotifySnowmenChanged();
        return cleared;
    }

    /// <summary>Очищает снег с кромок конкретного окна (§3.3, «очистка отдельного окна»).</summary>
    public int ClearWindow(IntPtr handle)
    {
        int cleared = 0;
        for (int i = 0; i < _ledges.Count; i++)
        {
            if (_ledges[i].Owner != handle) continue;
            cleared += ClearLedge(_ledges[i]);
        }

        int figures = 0;
        for (int i = _figures.Count - 1; i >= 0; i--)
        {
            var figure = _figures[i];
            if (figure.LedgeKey == 0) continue;
            if (!_ledgesByKey.TryGetValue(figure.LedgeKey, out var ledge)) continue;
            if (ledge.Owner != handle) continue;

            _figures.RemoveAt(i);
            figures++;
        }

        if (figures > 0) NotifySnowmenChanged();
        return cleared;
    }

    private int ClearLedge(Ledge ledge)
    {
        float before = ledge.SumHeight();
        Array.Clear(ledge.Heights);
        Array.Clear(ledge.Targets);

        // Масса сугроба — это не только уже «поднявшиеся» Heights, но и накопленные
        // Targets (снег, который ещё просаживается). Поэтому после очистки не вычитаем
        // приблизительную дельту, а пересчитываем точную массу по оставшимся кромкам.
        RecalculateMass();
        return (int)before;
    }

    /// <summary>Полностью удаляет частицы (используется при выключении снега).</summary>
    public void ClearParticles()
    {
        _count = 0;
        _burstCount = 0;
        _spawnAccumulator = 0f;
    }

    /// <summary>Заполняет сцену снегом сразу после запуска, чтобы не ждать 10 секунд.</summary>
    public void WarmStart(SimulationSettings settings, int? count = null)
    {
        int target = count ?? (int)(settings.MaxParticles * settings.Intensity);
        target = Math.Clamp(target, 0, HardParticleCap);
        int guard = target * 2 + 64;
        while (_count < target && guard-- > 0)
        {
            int before = _count;
            SpawnOne(settings, randomY: true);
            if (_count == before) break;
        }
    }

    /// <summary>Кромка под точкой (для «очистить окно под курсором»).</summary>
    public Ledge? FindLedgeAt(float x, float y, float tolerance = 12f)
    {
        for (int i = 0; i < _ledges.Count; i++)
        {
            var ledge = _ledges[i];
            if (x < ledge.Left || x > ledge.Right) continue;
            if (MathF.Abs(y - ledge.EdgeY) <= tolerance + ledge.MaxHeight) return ledge;
        }
        return null;
    }

    /// <summary>
    /// Есть ли рядом с точкой снег, с которым работает инструмент. Режим взаимодействия
    /// перехватывает мышь только там, где это нужно: остальной экран остаётся кликабельным.
    /// <paramref name="toleranceDip"/> задаётся в DIP и масштабируется под монитор.
    /// </summary>
    public bool IsInteractiveAt(float x, float y, float toleranceDip)
    {
        // Пока в руке снежок, он следует за курсором по всему экрану.
        if (_heldBall is not null) return true;

        float scale = ScaleAt(x, y);
        if (FindFigureAt(x, y, scale) is not null) return true;

        float tolerance = toleranceDip * scale;
        for (int i = 0; i < _ledges.Count; i++)
        {
            var ledge = _ledges[i];
            if (x < ledge.Left || x > ledge.Right) continue;
            if (!ledge.IsVisibleAt(x)) continue;

            // Снег лежит над кромкой: ниже неё (например, на панели задач) перехват не нужен.
            float above = ledge.EdgeY - y;
            if (above < -2f || above > tolerance + ledge.MaxHeight) continue;
            return true;
        }

        return false;
    }
}
