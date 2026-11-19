namespace WinterDesktop.Core.Geometry;

public static class GeometryMath
{
    public const float Epsilon = 1e-5f;

    public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

    public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 - edge0 <= Epsilon) return x >= edge1 ? 1f : 0f;
        float t = Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Деградация к нулю за пределами прямоугольника — используется для затухания эффектов у краёв.</summary>
    public static float EdgeFalloff(float x, float y, RectF rect, float margin)
    {
        if (margin <= 0f) return 0f;
        float dx = MathF.Min(x - rect.Left, rect.Right - x);
        float dy = MathF.Min(y - rect.Top, rect.Bottom - y);
        float d = MathF.Min(dx, dy);
        return d <= 0f ? 0f : d >= margin ? 1f : d / margin;
    }

    /// <summary>Экспоненциальное сглаживание, независимое от длительности кадра.</summary>
    public static float Damp(float current, float target, float rate, float dt) =>
        target + (current - target) * MathF.Exp(-rate * dt);

    public static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
}
