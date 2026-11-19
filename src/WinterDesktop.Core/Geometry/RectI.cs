using System.Globalization;

namespace WinterDesktop.Core.Geometry;

/// <summary>
/// Прямоугольник в физических пикселях с полуоткрытой семантикой: <c>[Left, Right)</c>, <c>[Top, Bottom)</c>.
/// Полуоткрытая семантика выбрана осознанно: соседние мониторы не перекрываются и точка всегда
/// принадлежит ровно одному прямоугольнику.
/// </summary>
public readonly struct RectI : IEquatable<RectI>
{
    public RectI(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Left { get; }
    public int Top { get; }
    public int Right { get; }
    public int Bottom { get; }

    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsEmpty => Width == 0 || Height == 0;
    public long Area => (long)Width * Height;

    public static RectI Empty => default;

    public static RectI FromSize(int x, int y, int width, int height) =>
        new(x, y, x + Math.Max(0, width), y + Math.Max(0, height));

    public static RectI FromLtrb(int left, int top, int right, int bottom)
    {
        if (right < left) (left, right) = (right, left);
        if (bottom < top) (top, bottom) = (bottom, top);
        return new RectI(left, top, right, bottom);
    }

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool Contains(RectI other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    public bool IntersectsWith(RectI other) =>
        other.Left < Right && other.Right > Left && other.Top < Bottom && other.Bottom > Top;

    public RectI Intersect(RectI other)
    {
        int l = Math.Max(Left, other.Left);
        int t = Math.Max(Top, other.Top);
        int r = Math.Min(Right, other.Right);
        int b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? Empty : new RectI(l, t, r, b);
    }

    public RectI Union(RectI other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new RectI(
            Math.Min(Left, other.Left),
            Math.Min(Top, other.Top),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }

    public RectI Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public RectI Inflate(int amount) => new(Left - amount, Top - amount, Right + amount, Bottom + amount);

    public System.Numerics.Vector2 Center => new((Left + Right) * 0.5f, (Top + Bottom) * 0.5f);

    public bool Equals(RectI other) =>
        Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

    public override bool Equals(object? obj) => obj is RectI other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);

    public static bool operator ==(RectI a, RectI b) => a.Equals(b);
    public static bool operator !=(RectI a, RectI b) => !a.Equals(b);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Left},{Top} {Width}x{Height}]");
}
