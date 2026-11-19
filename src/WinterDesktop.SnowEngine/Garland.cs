using WinterDesktop.Core.Models;

namespace WinterDesktop.SnowEngine;

/// <summary>
/// Лампочка гирлянды в физических пикселях виртуального рабочего стола.
/// <see cref="Index"/> — порядковый номер на нити (для чередования цветов),
/// <see cref="Phase"/> — индивидуальная фаза мигания.
/// </summary>
public readonly record struct GarlandBulb(float X, float Y, float Phase, int Index);

/// <summary>
/// Нить гирлянды вдоль верхней кромки окна. Геометрия считается в движке: провод идёт
/// чуть ниже кромки, лампочки висят под ним. Рендер только рисует готовые точки,
/// поэтому раскладка проверяется юнит-тестами без Direct2D.
/// </summary>
public sealed class GarlandString
{
    internal GarlandString(
        IntPtr owner,
        float scale,
        float edgeY,
        float wireY,
        float bulbRadius,
        float spacing,
        float phase,
        GarlandBulb[] bulbs)
    {
        Owner = owner;
        Scale = scale;
        EdgeY = edgeY;
        WireY = wireY;
        BulbRadius = bulbRadius;
        Spacing = spacing;
        Phase = phase;
        Bulbs = bulbs;
    }

    /// <summary>Окно-владелец; IntPtr.Zero быть не может.</summary>
    public IntPtr Owner { get; }

    /// <summary>Масштаб DPI монитора, на котором висит нить.</summary>
    public float Scale { get; }

    /// <summary>Y верхней кромки окна (провод висит ниже неё).</summary>
    public float EdgeY { get; }

    /// <summary>Y провода — чуть ниже верхней кромки окна.</summary>
    public float WireY { get; }

    /// <summary>Радиус лампочки в физических пикселях.</summary>
    public float BulbRadius { get; }

    /// <summary>Расстояние между лампочками в физических пикселях.</summary>
    public float Spacing { get; }

    /// <summary>Базовая фаза нити: соседние окна мигают не синхронно.</summary>
    public float Phase { get; }

    /// <summary>Видимые лампочки нити (перекрытые окнами выше по z-order уже исключены).</summary>
    public GarlandBulb[] Bulbs { get; }

    public int BulbCount => Bulbs.Length;
}

/// <summary>
/// Чистая математика анимации гирлянд. Вынесена из рендера, чтобы поведение
/// «мерцание / волна / бегущий огонь / ровный свет» проверялось тестами.
/// </summary>
public static class GarlandTiming
{
    /// <summary>
    /// Яркость лампочки в диапазоне 0…1. <paramref name="phase"/> задаёт положение
    /// на нити и индивидуальный сдвиг, <paramref name="speed"/> масштабирует время.
    /// </summary>
    public static float Brightness(GarlandAnimation animation, float phase, float time, float speed)
    {
        float t = MathF.Max(0f, time) * Math.Clamp(speed, 0.1f, 4f);

        switch (animation)
        {
            case GarlandAnimation.Steady:
                return 1f;

            case GarlandAnimation.Wave:
                return 0.28f + 0.72f * (0.5f + 0.5f * MathF.Sin((t * 2.6f) - phase));

            case GarlandAnimation.Chase:
            {
                // Один «огонёк» бежит вдоль нити: фаза сдвигает момент вспышки.
                float position = (t * 1.6f) - (phase * 0.31830989f);
                position -= MathF.Floor(position);
                float bump = MathF.Max(0f, MathF.Cos(position * MathF.Tau));
                return 0.18f + 0.82f * MathF.Pow(bump, 5f);
            }

            default: // Twinkle
                return 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin((t * 2.1f) + phase));
        }
    }

    /// <summary>Детерминированная фаза нити по ключу окна (0…2π).</summary>
    public static float PhaseFor(long key)
    {
        ulong hash = (ulong)key * 0x9E3779B97F4A7C15UL;
        hash ^= hash >> 31;
        hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 29;
        return (hash & 0xFFFFUL) * (MathF.Tau / 65536f);
    }
}
