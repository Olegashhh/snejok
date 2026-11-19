using System.Globalization;

namespace WinterDesktop.Core.Geometry;

/// <summary>Прямоугольник с плавающей точкой (физические пиксели). Используется физикой снега.</summary>
public readonly struct RectF
{
    public RectF(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public float Left { get; }
    public float Top { get; }
    public float Right { get; }
    public float Bottom { get; }

    public float Width => MathF.Max(0f, Right - Left);
    public float Height => MathF.Max(0f, Bottom - Top);
    public bool IsEmpty => Width <= 0f || Height <= 0f;

    public static RectF FromSize(float x, float y, float width, float height) =>
        new(x, y, x + MathF.Max(0f, width), y + MathF.Max(0f, height));

    public static RectF FromRectI(RectI r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public bool Contains(float x, float y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool IntersectsWith(RectF other) =>
        other.Left < Right && other.Right > Left && other.Top < Bottom && other.Bottom > Top;

    public RectF Intersect(RectF other)
    {
        float l = MathF.Max(Left, other.Left);
        float t = MathF.Max(Top, other.Top);
        float r = MathF.Min(Right, other.Right);
        float b = MathF.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? default : new RectF(l, t, r, b);
    }

    public RectF Offset(float dx, float dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public RectI ToRectI() =>
        new((int)MathF.Floor(Left), (int)MathF.Floor(Top), (int)MathF.Ceiling(Right), (int)MathF.Ceiling(Bottom));

    public static RectF Lerp(RectF a, RectF b, float t) => new(
        a.Left + (b.Left - a.Left) * t,
        a.Top + (b.Top - a.Top) * t,
        a.Right + (b.Right - a.Right) * t,
        a.Bottom + (b.Bottom - a.Bottom) * t);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Left:0.#},{Top:0.#} {Width:0.#}x{Height:0.#}]");
}
