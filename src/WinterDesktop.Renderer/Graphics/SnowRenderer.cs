using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using WinterDesktop.Core.Models;
using WinterDesktop.SnowEngine;
using RectI = WinterDesktop.Core.Geometry.RectI;

namespace WinterDesktop.Renderer.Graphics;

/// <summary>
/// Рисует снежинки, сугробы и гирлянды для одного монитора.
///
/// Снежинки идут через <c>ID2D1SpriteBatch</c> (до 4096 штук на вызов), сугробы — одним
/// <c>ID2D1PathGeometry</c> по высотной карте кромок и <c>FillGeometry</c>. Путь строится
/// заново каждый кадр: после <c>Close</c> геометрию нельзя открыть повторно, а сплошная
/// заливка единого контура не даёт швов и двойного наложения полупрозрачной кисти.
/// Гирлянды рисуются готовыми точками движка: провод, подвесы, ореол и ядро лампочки.
/// </summary>
internal sealed class SnowRenderer : IDisposable
{
    private const int MaxSpritesPerBatch = 4096;
    private const float MinimumFlakeDiameter = 3.2f;
    private const float FlakeSizeFactor = 6.2f;

    /// <summary>Длина схода сугроба на краю кромки на единицу толщины (угол ~34°).</summary>
    private const float TaperRunPerHeight = 1.5f;

    private static readonly Color4[] WinterPalette =
    {
        new(0.78f, 0.93f, 1.00f, 1f),
        new(0.58f, 0.82f, 1.00f, 1f),
        new(0.90f, 0.97f, 1.00f, 1f),
        new(0.45f, 0.72f, 0.98f, 1f),
    };

    private static readonly Color4[] FestivePalette =
    {
        new(1.00f, 0.28f, 0.25f, 1f),
        new(1.00f, 0.78f, 0.25f, 1f),
        new(0.30f, 0.88f, 0.45f, 1f),
        new(0.30f, 0.58f, 1.00f, 1f),
    };

    private static readonly Color4[] WarmPalette =
    {
        new(1.00f, 0.63f, 0.25f, 1f),
        new(1.00f, 0.42f, 0.25f, 1f),
        new(1.00f, 0.83f, 0.55f, 1f),
        new(0.98f, 0.92f, 0.72f, 1f),
    };

    private static readonly Color4[] CandyPalette =
    {
        new(1.00f, 0.48f, 0.72f, 1f),
        new(1.00f, 0.80f, 0.92f, 1f),
        new(0.52f, 0.92f, 1.00f, 1f),
        new(0.76f, 0.62f, 1.00f, 1f),
    };

    private readonly GraphicsDevice _graphics;
    private readonly SnowSpriteAtlas _atlas;
    private readonly ID2D1SolidColorBrush _flakeBrush;
    private readonly ID2D1SolidColorBrush _snowBrush;
    private readonly ID2D1SolidColorBrush _outlineBrush;
    private readonly ID2D1SolidColorBrush _shadowBrush;
    private readonly ID2D1SolidColorBrush _coalBrush;
    private readonly ID2D1SolidColorBrush _carrotBrush;
    private readonly ID2D1SolidColorBrush _twigBrush;
    private readonly ID2D1SolidColorBrush _garlandWireBrush;
    private readonly ID2D1SolidColorBrush _garlandBrush;
    private readonly ID2D1SpriteBatch? _spriteBatch;

    private readonly RawRectF[] _destinations = new RawRectF[MaxSpritesPerBatch];
    private readonly RawRect[] _sources = new RawRect[MaxSpritesPerBatch];
    private readonly Color4[] _colors = new Color4[MaxSpritesPerBatch];
    private readonly Matrix3x2[] _transforms = new Matrix3x2[MaxSpritesPerBatch];

    private bool _disposed;

    public SnowRenderer(GraphicsDevice graphics)
    {
        _graphics = graphics;
        var context = graphics.D2DContext;

        _atlas = SnowSpriteAtlas.Create(context);
        _flakeBrush = context.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.92f));
        _snowBrush = context.CreateSolidColorBrush(new Color4(0.96f, 0.98f, 1f, 0.93f));
        _outlineBrush = context.CreateSolidColorBrush(new Color4(0.80f, 0.87f, 0.97f, 0.85f));
        _shadowBrush = context.CreateSolidColorBrush(new Color4(0.42f, 0.52f, 0.68f, 0.30f));
        _coalBrush = context.CreateSolidColorBrush(new Color4(0.08f, 0.10f, 0.14f, 0.95f));
        _carrotBrush = context.CreateSolidColorBrush(new Color4(0.98f, 0.52f, 0.12f, 0.98f));
        _twigBrush = context.CreateSolidColorBrush(new Color4(0.32f, 0.21f, 0.12f, 0.95f));
        _garlandWireBrush = context.CreateSolidColorBrush(new Color4(0.16f, 0.20f, 0.28f, 0.62f));
        _garlandBrush = context.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));

        if (graphics.Context3 is not null)
        {
            _spriteBatch = graphics.Context3.CreateSpriteBatch();
        }
    }

    /// <summary>Полный кадр для одного монитора: очистка, сугробы, снежинки, Present.</summary>
    public bool Render(OverlayWindow window, SnowSimulation simulation, SimulationSettings settings)
    {
        var region = window.Monitor.Bounds;

        window.BeginDraw();
        try
        {
            DrawLedges(simulation, region);
            DrawGarlands(simulation, region, settings);
            DrawFigures(simulation, region);
            DrawSnowballs(simulation, region);
            DrawFlakes(simulation, region, settings);
        }
        catch (Exception)
        {
            // Ошибка отрисовки кадра не должна убивать поток эффектов: пропускаем кадр.
            return false;
        }

        return window.EndDrawAndPresent();
    }

    /// <summary>
    /// Гирлянды: провод и подвесы, затем ореол и ядро каждой лампочки. Яркость берётся
    /// из <see cref="GarlandTiming"/>, поэтому режимы мигания не дублируются в рендере.
    /// </summary>
    private void DrawGarlands(SnowSimulation simulation, RectI region, SimulationSettings settings)
    {
        var garlands = simulation.Garlands;
        if (garlands.Count == 0) return;

        var context = _graphics.D2DContext;
        context.AntialiasMode = AntialiasMode.PerPrimitive;

        float time = simulation.Time;
        float speed = settings.GarlandSpeed;
        float glow = Math.Clamp(settings.GarlandGlow, 0f, 1f);
        var palette = PaletteFor(settings.GarlandPalette);

        for (int s = 0; s < garlands.Count; s++)
        {
            var garland = garlands[s];
            var bulbs = garland.Bulbs;
            if (bulbs.Length == 0) continue;
            if (garland.WireY < region.Top - 64f || garland.WireY > region.Bottom + 64f) continue;

            float wireWidth = MathF.Max(0.8f, 1.0f * garland.Scale);

            // Провод и подвесы: соседние видимые лампочки соединяются, только если
            // между ними нет скрытого окном участка нити.
            for (int i = 0; i < bulbs.Length; i++)
            {
                var bulb = bulbs[i];
                if (!IsVisible(bulb.X, bulb.Y, region, 32f)) continue;

                if (i > 0)
                {
                    var prev = bulbs[i - 1];
                    bool adjacent = bulb.X - prev.X <= garland.Spacing * 1.6f;
                    if (adjacent && IsVisible(prev.X, prev.Y, region, 32f))
                    {
                        context.DrawLine(
                            new Vector2(prev.X, garland.WireY),
                            new Vector2(bulb.X, garland.WireY),
                            _garlandWireBrush,
                            wireWidth);
                    }
                }

                context.DrawLine(
                    new Vector2(bulb.X, garland.WireY),
                    new Vector2(bulb.X, bulb.Y - garland.BulbRadius),
                    _garlandWireBrush,
                    wireWidth * 0.85f);
            }

            // Ореол (мягкое свечение) и ядро. Ореол рисуется первым, чтобы не гасить ядро.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < bulbs.Length; i++)
                {
                    var bulb = bulbs[i];
                    if (!IsVisible(bulb.X, bulb.Y, region, 32f)) continue;

                    float brightness = GarlandTiming.Brightness(
                        settings.GarlandAnimation,
                        bulb.Phase,
                        time,
                        speed);

                    if (pass == 0)
                    {
                        if (glow <= 0.01f) continue;
                        float haloRadius = garland.BulbRadius * (1.5f + 1.1f * glow);
                        float haloAlpha = glow * (0.10f + 0.28f * brightness);
                        var color = palette[bulb.Index % palette.Length];
                        _garlandBrush.Color = new Color4(color.R, color.G, color.B, haloAlpha);
                        context.FillEllipse(
                            new Ellipse(new Vector2(bulb.X, bulb.Y), haloRadius, haloRadius),
                            _garlandBrush);
                        continue;
                    }

                    var core = palette[bulb.Index % palette.Length];
                    float coreAlpha = 0.30f + 0.68f * brightness;
                    _garlandBrush.Color = new Color4(core.R, core.G, core.B, coreAlpha);
                    context.FillEllipse(
                        new Ellipse(new Vector2(bulb.X, bulb.Y), garland.BulbRadius, garland.BulbRadius),
                        _garlandBrush);

                    // Маленький блик: лампочка выглядит стеклянной, а не плоским кружком.
                    float highlight = MathF.Max(0.55f, garland.BulbRadius * 0.36f);
                    _garlandBrush.Color = new Color4(1f, 1f, 1f, 0.28f + 0.45f * brightness);
                    context.FillEllipse(
                        new Ellipse(
                            new Vector2(
                                bulb.X - garland.BulbRadius * 0.26f,
                                bulb.Y - garland.BulbRadius * 0.30f),
                            highlight,
                            highlight),
                        _garlandBrush);
                }
            }
        }
    }

    private static Color4[] PaletteFor(GarlandPalette palette) => palette switch
    {
        GarlandPalette.Winter => WinterPalette,
        GarlandPalette.Warm => WarmPalette,
        GarlandPalette.Candy => CandyPalette,
        _ => FestivePalette,
    };

    /// <summary>Снеговики: тень, ветки, шары, угольки и морковка. Рисуются поверх сугробов.</summary>
    private void DrawFigures(SnowSimulation simulation, RectI region)
    {
        var figures = simulation.Figures;
        if (figures.Count == 0) return;

        var context = _graphics.D2DContext;
        context.AntialiasMode = AntialiasMode.PerPrimitive;

        for (int i = 0; i < figures.Count; i++)
        {
            var figure = figures[i];
            if (figure.BaseY < region.Top - 8f || figure.BaseY > region.Bottom + 180f) continue;
            if (figure.X < region.Left - 200f || figure.X > region.Right + 200f) continue;

            DrawFigure(figure);
        }
    }

    private void DrawFigure(SnowFigure figure)
    {
        var context = _graphics.D2DContext;
        float scale = figure.Scale;
        float bottomRadius = SnowFigure.BallRadius(scale, 0);

        // Мягкая тень на снегу: снеговик «стоит», а не висит.
        context.FillEllipse(
            new Ellipse(new Vector2(figure.X, figure.BaseY - 0.5f * scale), bottomRadius * 1.02f, bottomRadius * 0.28f),
            _shadowBrush);

        // Ветки-руки рисуются до шаров, чтобы выглядывать из-за тела.
        if (figure.Balls >= 3) DrawArms(figure);

        for (int level = 0; level < figure.Balls; level++)
        {
            float radius = SnowFigure.BallRadius(scale, level);
            var ellipse = new Ellipse(new Vector2(figure.X, figure.BallCenterY(level)), radius, radius);
            context.FillEllipse(ellipse, _snowBrush);
            context.DrawEllipse(ellipse, _outlineBrush, MathF.Max(0.9f, 1.1f * scale));
        }

        int headLevel = figure.Balls - 1;

        // Пуговицы на шарах ниже головы.
        for (int level = 0; level < headLevel; level++)
        {
            float radius = SnowFigure.BallRadius(scale, level);
            float centerY = figure.BallCenterY(level);
            float dot = MathF.Max(0.8f, radius * 0.10f);

            for (int sign = -1; sign <= 1; sign += 2)
            {
                context.FillEllipse(
                    new Ellipse(new Vector2(figure.X, centerY + sign * radius * 0.28f), dot, dot),
                    _coalBrush);
            }
        }

        if (figure.Balls < 2) return;

        float headRadius = SnowFigure.BallRadius(scale, headLevel);
        float headY = figure.BallCenterY(headLevel);

        // Глаза.
        float eyeRadius = MathF.Max(0.9f, headRadius * 0.11f);
        float eyeY = headY - headRadius * 0.18f;
        context.FillEllipse(
            new Ellipse(new Vector2(figure.X - headRadius * 0.34f, eyeY), eyeRadius, eyeRadius),
            _coalBrush);
        context.FillEllipse(
            new Ellipse(new Vector2(figure.X + headRadius * 0.34f, eyeY), eyeRadius, eyeRadius),
            _coalBrush);

        // Нос-морковка.
        context.DrawLine(
            new Vector2(figure.X + headRadius * 0.10f, headY + headRadius * 0.03f),
            new Vector2(figure.X + headRadius * 1.15f, headY + headRadius * 0.15f),
            _carrotBrush,
            MathF.Max(1.4f, 2.0f * scale));
    }

    private void DrawArms(SnowFigure figure)
    {
        var context = _graphics.D2DContext;
        float scale = figure.Scale;
        float radius = SnowFigure.BallRadius(scale, 1);
        float centerY = figure.BallCenterY(1);
        float width = MathF.Max(1.0f, 1.6f * scale);

        float leftStartX = figure.X - radius * 0.85f;
        float rightStartX = figure.X + radius * 0.85f;
        float elbowY = centerY - radius * 0.55f;

        var leftStart = new Vector2(leftStartX, centerY - radius * 0.15f);
        var leftElbow = new Vector2(figure.X - radius * 1.75f, elbowY);
        var leftTip = new Vector2(figure.X - radius * 2.45f, elbowY - radius * 0.40f);
        context.DrawLine(leftStart, leftElbow, _twigBrush, width);
        context.DrawLine(leftElbow, leftTip, _twigBrush, width);
        context.DrawLine(leftElbow, new Vector2(leftElbow.X - radius * 0.12f, leftElbow.Y - radius * 0.55f), _twigBrush, width * 0.8f);

        var rightStart = new Vector2(rightStartX, centerY - radius * 0.15f);
        var rightElbow = new Vector2(figure.X + radius * 1.75f, elbowY);
        var rightTip = new Vector2(figure.X + radius * 2.45f, elbowY - radius * 0.40f);
        context.DrawLine(rightStart, rightElbow, _twigBrush, width);
        context.DrawLine(rightElbow, rightTip, _twigBrush, width);
        context.DrawLine(rightElbow, new Vector2(rightElbow.X + radius * 0.12f, rightElbow.Y - radius * 0.55f), _twigBrush, width * 0.8f);
    }

    /// <summary>Летящие и удерживаемые снежки.</summary>
    private void DrawSnowballs(SnowSimulation simulation, RectI region)
    {
        var balls = simulation.Snowballs;
        if (balls.Count == 0) return;

        var context = _graphics.D2DContext;
        context.AntialiasMode = AntialiasMode.PerPrimitive;

        for (int i = 0; i < balls.Count; i++)
        {
            var ball = balls[i];
            if (!IsVisible(ball.X, ball.Y, region, 64f)) continue;

            var ellipse = new Ellipse(new Vector2(ball.X, ball.Y), ball.Radius, ball.Radius);
            context.FillEllipse(ellipse, _snowBrush);
            context.DrawEllipse(ellipse, _outlineBrush, MathF.Max(0.9f, ball.Scale * 1.1f));

            float highlight = MathF.Max(0.8f, ball.Radius * 0.18f);
            context.FillEllipse(
                new Ellipse(
                    new Vector2(ball.X - ball.Radius * 0.30f, ball.Y - ball.Radius * 0.35f),
                    highlight,
                    highlight),
                _flakeBrush);
        }
    }

    private void DrawFlakes(SnowSimulation simulation, RectI region, SimulationSettings settings)
    {
        var particles = simulation.ActiveParticles;
        if (particles.Length == 0) return;

        float opacity = Math.Clamp(settings.Opacity, 0f, 1f);

        if (_spriteBatch is null || _graphics.Context3 is null)
        {
            DrawFlakesFallback(particles, region, opacity);
            return;
        }

        int pending = 0;
        for (int i = 0; i < particles.Length; i++)
        {
            ref readonly var particle = ref particles[i];
            if (!IsVisible(particle.X, particle.Y, region, 48f)) continue;

            float alpha = Math.Clamp(particle.Alpha * opacity, 0f, 1f);
            if (alpha <= 0.01f) continue;

            float size = MathF.Max(MinimumFlakeDiameter, particle.Radius * FlakeSizeFactor);
            float half = size * 0.5f;

            // Пылинки-«Fine» не вращаются: экономия двух тригонометрических вызовов на частицу.
            var transform = particle.Shape == SnowShape.Fine
                ? Matrix3x2.Identity
                : Matrix3x2.CreateRotation(particle.Phase);

            _destinations[pending] = new RawRectF(
                particle.X - half,
                particle.Y - half,
                particle.X + half,
                particle.Y + half);
            _sources[pending] = _atlas.Source(particle.Shape);
            _colors[pending] = new Color4(alpha, alpha, alpha, alpha);
            _transforms[pending] = transform;

            pending++;
            if (pending == MaxSpritesPerBatch)
            {
                FlushSprites(pending);
                pending = 0;
            }
        }

        if (pending > 0) FlushSprites(pending);
    }

    private void FlushSprites(int count)
    {
        var batch = _spriteBatch!;
        var context = _graphics.Context3!;

        batch.Clear();
        batch.AddSprites(
            (uint)count,
            _destinations,
            _sources,
            _colors,
            _transforms,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<RawRectF>(),
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<RawRect>(),
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<Color4>(),
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<Matrix3x2>());

        // Sprite batch, как и меш, требует режима без сглаживания: иначе Direct2D
        // завершает кадр с D2DERR_WRONG_STATE (проверено на GPU и WARP).
        context.AntialiasMode = AntialiasMode.Aliased;
        context.DrawSpriteBatch(
            batch,
            0,
            (uint)count,
            _atlas.Bitmap,
            BitmapInterpolationMode.Linear,
            SpriteOptions.ClampToSourceRectangle);
    }

    /// <summary>Резервный путь для систем без sprite batch (Windows 8.1): кружки вместо атласа.</summary>
    private void DrawFlakesFallback(ReadOnlySpan<SnowParticle> particles, RectI region, float opacity)
    {
        // Кружки сглаживаем: режим Aliased остаётся после меша/спрайтов.
        _graphics.D2DContext.AntialiasMode = AntialiasMode.PerPrimitive;

        for (int i = 0; i < particles.Length; i++)
        {
            ref readonly var particle = ref particles[i];
            if (!IsVisible(particle.X, particle.Y, region, 48f)) continue;

            float radius = MathF.Max(1.4f, particle.Radius * 1.35f);
            var ellipse = new Ellipse(new Vector2(particle.X, particle.Y), radius, radius);
            _graphics.D2DContext.FillEllipse(ellipse, _flakeBrush);
        }
    }

    private void DrawLedges(SnowSimulation simulation, RectI region)
    {
        var ledges = simulation.Ledges;
        if (ledges.Count == 0) return;

        // Один путь на все кромки монитора: сплошная заливка без внутренних швов.
        // Раньше каждая кромка рисовалась полосами треугольников с перекрытием 0.5 px,
        // и на стыках полос полупрозрачная кисть накладывалась дважды — появлялись
        // вертикальные полосы каждые 5 px. Заливка единого контура их убирает.
        using var geometry = _graphics.D2DFactory.CreatePathGeometry();
        bool hasFigures = false;

        using (var sink = geometry.Open())
        {
            sink.SetFillMode(FillMode.Winding);

            for (int li = 0; li < ledges.Count; li++)
            {
                var ledge = ledges[li];
                var heights = ledge.HeightSamples;
                if (heights.Length < 2) continue;

                float step = ledge.SampleStep;
                if (step <= 0.01f) continue;

                // Кромка не пересекает этот монитор по вертикали — рисовать нечего.
                if (ledge.EdgeY < region.Top - 4f || ledge.EdgeY > region.Bottom + 8f) continue;

                var spans = ledge.VisibleSpans;
                if (spans.Length < 2) continue;

                float edgeY = ledge.EdgeY;
                float bottom = edgeY + 2.5f;

                for (int s = 0; s + 1 < spans.Length; s += 2)
                {
                    float spanLeft = MathF.Max(spans[s], MathF.Max(ledge.Left, region.Left));
                    float spanRight = MathF.Min(spans[s + 1], MathF.Min(ledge.Right, region.Right));
                    if (spanRight - spanLeft < 1f) continue;

                    int first = Math.Clamp((int)((spanLeft - ledge.Left) / step), 0, heights.Length - 2);
                    int last = Math.Clamp((int)((spanRight - ledge.Left) / step), first + 1, heights.Length - 1);

                    bool anyHeight = false;
                    for (int i = first; i <= last; i++)
                    {
                        if (heights[i] >= 0.35f) { anyHeight = true; break; }
                    }

                    if (!anyHeight) continue;

                    // Контур: левый край → вершины heightfield → правый край → низ.
                    // У края сугроб сходит на нет под углом естественного откоса: без этого
                    // полотно обрывалось вертикальной стенкой на границе окна или перекрытия.
                    float edgeHeightLeft = HeightAt(ledge, first, spanLeft);
                    float edgeHeightRight = HeightAt(ledge, last, spanRight);
                    float spanWidth = spanRight - spanLeft;
                    float runLeft = MathF.Min(edgeHeightLeft * TaperRunPerHeight, spanWidth * 0.45f);
                    float runRight = MathF.Min(edgeHeightRight * TaperRunPerHeight, spanWidth * 0.45f);

                    sink.BeginFigure(new Vector2(spanLeft, edgeY), FigureBegin.Filled);
                    for (int i = first + 1; i <= last; i++)
                    {
                        float x = ledge.Left + (i * step);
                        float taper = TaperFactor(x, spanLeft, spanRight, runLeft, runRight);
                        sink.AddLine(new Vector2(x, edgeY - (heights[i] * taper)));
                    }

                    sink.AddLine(new Vector2(spanRight, edgeY));
                    sink.AddLine(new Vector2(spanRight, bottom));
                    sink.AddLine(new Vector2(spanLeft, bottom));
                    sink.EndFigure(FigureEnd.Closed);
                    hasFigures = true;
                }
            }

            sink.Close();
        }

        if (!hasFigures) return;

        var context = _graphics.D2DContext;
        // Сглаженный путь даёт мягкую кромку сугроба вместо ступенек из треугольников.
        context.AntialiasMode = AntialiasMode.PerPrimitive;
        context.FillGeometry(geometry, _snowBrush);
    }

    /// <summary>
    /// Множитель высоты у края видимого участка: 0 на самой границе, 1 после схода.
    /// Сглаженная ступень (smoothstep) даёт выпуклый склон, похожий на снежный откос.
    /// </summary>
    private static float TaperFactor(float x, float left, float right, float runLeft, float runRight)
    {
        float factor = 1f;

        if (runLeft > 0.01f && x < left + runLeft)
        {
            float t = (x - left) / runLeft;
            factor = MathF.Min(factor, t * t * (3f - 2f * t));
        }

        if (runRight > 0.01f && x > right - runRight)
        {
            float t = (right - x) / runRight;
            factor = MathF.Min(factor, t * t * (3f - 2f * t));
        }

        return factor;
    }

    /// <summary>Высота в точке X с линейной интерполяцией между соседними сэмплами.</summary>
    private static float HeightAt(Ledge ledge, int sample, float x)
    {
        var heights = ledge.HeightSamples;
        if (sample < 0 || sample >= heights.Length) return 0f;

        float step = ledge.SampleStep;
        if (step <= 0.01f) return heights[sample];

        float relative = (x - ledge.Left) / step;
        int i0 = Math.Clamp((int)relative, 0, heights.Length - 1);
        int i1 = Math.Min(i0 + 1, heights.Length - 1);
        float t = Math.Clamp(relative - i0, 0f, 1f);
        return heights[i0] + (heights[i1] - heights[i0]) * t;
    }

    private static bool IsVisible(float x, float y, RectI region, float margin)
    {
        return x >= region.Left - margin && x <= region.Right + margin
               && y >= region.Top - margin && y <= region.Bottom + margin;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _spriteBatch?.Dispose();
        _garlandBrush.Dispose();
        _garlandWireBrush.Dispose();
        _twigBrush.Dispose();
        _carrotBrush.Dispose();
        _coalBrush.Dispose();
        _shadowBrush.Dispose();
        _outlineBrush.Dispose();
        _snowBrush.Dispose();
        _flakeBrush.Dispose();
        _atlas.Dispose();
    }
}
