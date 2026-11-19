using System.Runtime.InteropServices;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinterDesktop.Core.Models;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace WinterDesktop.Renderer.Graphics;

/// <summary>
/// Процедурный атлас снежинок. Пять форм рисуются в память с суперсэмплингом и загружаются
/// в Direct2D как одна текстура, поэтому все частицы отрисовываются одним sprite batch без
/// промежуточных геометрий (§4, бюджет кадра).
/// </summary>
internal sealed class SnowSpriteAtlas : IDisposable
{
    public const int CellSize = 64;
    public const int ShapeCount = 5;

    private readonly ID2D1Bitmap1 _bitmap;
    private readonly RawRect[] _sources;

    private SnowSpriteAtlas(ID2D1Bitmap1 bitmap, RawRect[] sources)
    {
        _bitmap = bitmap;
        _sources = sources;
    }

    public ID2D1Bitmap Bitmap => _bitmap;

    /// <summary>Исходный прямоугольник для формы; индекс совпадает с <see cref="SnowShape"/>.</summary>
    public RawRect Source(SnowShape shape)
    {
        int index = (int)shape;
        return _sources[index < 0 || index >= _sources.Length ? 0 : index];
    }

    public static SnowSpriteAtlas Create(ID2D1DeviceContext context)
    {
        int width = CellSize * ShapeCount;
        int height = CellSize;

        var pixels = new byte[width * height * 4];

        for (int shape = 0; shape < ShapeCount; shape++)
        {
            int originX = shape * CellSize;
            for (int y = 0; y < CellSize; y++)
            {
                for (int x = 0; x < CellSize; x++)
                {
                    float coverage = Coverage((SnowShape)shape, x + 0.5f, y + 0.5f);
                    if (coverage <= 0.002f) continue;

                    byte alpha = (byte)Math.Clamp((int)(coverage * 255f), 0, 255);
                    int offset = ((y * width) + originX + x) * 4;

                    // Премультиплицированная альфа: белый цвет, умноженный на альфу.
                    pixels[offset + 0] = alpha;
                    pixels[offset + 1] = alpha;
                    pixels[offset + 2] = alpha;
                    pixels[offset + 3] = alpha;
                }
            }
        }

        var properties = new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
            96f,
            96f,
            BitmapOptions.None);

        ID2D1Bitmap1? bitmap = null;
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap = context.CreateBitmap(
                new SizeI(width, height),
                handle.AddrOfPinnedObject(),
                (uint)(width * 4),
                properties);
        }
        finally
        {
            handle.Free();
        }

        var sources = new RawRect[ShapeCount];
        for (int i = 0; i < ShapeCount; i++)
        {
            sources[i] = new RawRect(i * CellSize, 0, (i + 1) * CellSize, CellSize);
        }

        return new SnowSpriteAtlas(bitmap, sources);
    }

    /// <summary>
    /// Покрытие пикселя формой. Форма описывается через расстояние до набора линий/окружностей,
    /// что даёт гладкий край без внешних ассетов и без растеризации GDI.
    /// </summary>
    private static float Coverage(SnowShape shape, float px, float py)
    {
        float cx = CellSize * 0.5f;
        float cy = CellSize * 0.5f;
        float localX = px - cx;
        float localY = py - cy;
        float radius = MathF.Sqrt(localX * localX + localY * localY);

        switch (shape)
        {
            case SnowShape.Fine:
            {
                // Мягкая крупинка: гауссово ядро + плотный центр.
                float soft = MathF.Exp(-(radius * radius) / (2f * 7.0f * 7.0f));
                float core = MathF.Exp(-(radius * radius) / (2f * 3.1f * 3.1f));
                return MathF.Min(1f, soft * 0.55f + core * 0.55f);
            }

            case SnowShape.Crystal:
                return FromDistance(CrystalDistance(localX, localY, arms: 6, armLength: 25f, thickness: 1.5f, branches: 2, branchScale: 1f), glowStrength: 0.18f);

            case SnowShape.Star:
                return FromDistance(StarDistance(localX, localY, arms: 6, armLength: 26f), glowStrength: 0.14f);

            case SnowShape.Cluster:
                return FromDistance(ClusterDistance(localX, localY), glowStrength: 0.16f);

            case SnowShape.Large:
                return FromDistance(CrystalDistance(localX, localY, arms: 6, armLength: 29f, thickness: 2.5f, branches: 3, branchScale: 1.35f), glowStrength: 0.22f);

            default:
                return 0f;
        }
    }

    /// <summary>Перевод знакового расстояния в альфу: 1 px антиалиасинга + слабое свечение.</summary>
    private static float FromDistance(float distance, float glowStrength)
    {
        float core = Math.Clamp(0.5f - distance, 0f, 1f);
        if (core > 0f) return MathF.Min(1f, core);
        if (distance > 9f) return 0f;

        float glow = glowStrength * MathF.Exp(-distance / 2.4f);
        return Math.Clamp(glow, 0f, 1f);
    }

    private static float CrystalDistance(float x, float y, int arms, float armLength, float thickness, int branches, float branchScale)
    {
        float distance = float.MaxValue;

        for (int i = 0; i < arms; i++)
        {
            float angle = i * (MathF.Tau / arms);
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            distance = MathF.Min(distance, SegmentDistance(x, y, dx * 3f, dy * 3f, dx * armLength, dy * armLength, thickness));

            for (int b = 0; b < branches; b++)
            {
                float t = 0.48f + 0.16f * b;
                float baseX = dx * armLength * t;
                float baseY = dy * armLength * t;

                // Веточки отходят под углом ±34°, как у настоящей снежинки.
                for (int side = -1; side <= 1; side += 2)
                {
                    float branchAngle = angle + side * 0.6f;
                    float length = armLength * (0.2f - 0.035f * b) * branchScale;
                    float tipX = baseX + MathF.Cos(branchAngle) * length;
                    float tipY = baseY + MathF.Sin(branchAngle) * length;
                    distance = MathF.Min(distance, SegmentDistance(x, y, baseX, baseY, tipX, tipY, thickness * 0.72f));
                }
            }

            // Точки на концах лучей добавляют «искристость».
            distance = MathF.Min(distance, MathF.Sqrt((x - dx * armLength) * (x - dx * armLength) + (y - dy * armLength) * (y - dy * armLength)) - thickness * 0.9f);
        }

        // Шестиугольное ядро.
        distance = MathF.Min(distance, MathF.Sqrt(x * x + y * y) - thickness * 1.9f);
        return distance;
    }

    private static float StarDistance(float x, float y, int arms, float armLength)
    {
        float distance = float.MaxValue;

        for (int i = 0; i < arms; i++)
        {
            float angle = i * (MathF.Tau / arms);
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            // Утолщение к центру имитируется двумя отрезками разной толщины.
            distance = MathF.Min(distance, SegmentDistance(x, y, dx * 4f, dy * 4f, dx * armLength * 0.45f, dy * armLength * 0.45f, 2.1f));
            distance = MathF.Min(distance, SegmentDistance(x, y, dx * armLength * 0.45f, dy * armLength * 0.45f, dx * armLength, dy * armLength, 0.85f));
        }

        distance = MathF.Min(distance, MathF.Sqrt(x * x + y * y) - 2.6f);
        return distance;
    }

    private static float ClusterDistance(float x, float y)
    {
        float distance = MathF.Sqrt(x * x + y * y) - 4.6f;

        for (int i = 0; i < 6; i++)
        {
            float angle = i * (MathF.Tau / 6f) + 0.4f;
            float dotX = MathF.Cos(angle) * 12.5f;
            float dotY = MathF.Sin(angle) * 12.5f;
            float dx = x - dotX;
            float dy = y - dotY;
            float dotRadius = i % 2 == 0 ? 3.4f : 2.3f;
            distance = MathF.Min(distance, MathF.Sqrt(dx * dx + dy * dy) - dotRadius);
        }

        return distance;
    }

    private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by, float thickness)
    {
        float abx = bx - ax;
        float aby = by - ay;
        float apx = px - ax;
        float apy = py - ay;

        float lengthSq = (abx * abx) + (aby * aby);
        float t = lengthSq <= 0.0001f ? 0f : Math.Clamp((apx * abx + apy * aby) / lengthSq, 0f, 1f);

        float dx = apx - abx * t;
        float dy = apy - aby * t;
        return MathF.Sqrt((dx * dx) + (dy * dy)) - thickness;
    }

    public void Dispose() => _bitmap.Dispose();
}
