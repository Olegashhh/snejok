namespace WinterDesktop.SnowEngine;

/// <summary>
/// Собранный снеговик. Хранит только геометрию: три шара добавляются снизу вверх,
/// основание привязано к кромке (<see cref="LedgeKey"/>) и следует за ней при
/// перемещении окна. Для сохранения между запусками сериализуется в
/// <see cref="WinterDesktop.Core.Models.SnowmanRecord"/>.
/// </summary>
public sealed class SnowFigure
{
    public const int MaxBalls = 3;

    /// <summary>Ключ кромки, на которой стоит снеговик; 0 — отвязанное основание (низ рабочей области).</summary>
    public long LedgeKey;

    /// <summary>Стабильный ключ монитора (например DISPLAY1) для сохранения.</summary>
    public string MonitorKey = string.Empty;

    /// <summary>Центр снеговика по X.</summary>
    public float X;

    /// <summary>Линия основания: низ нижнего шара.</summary>
    public float BaseY;

    /// <summary>Масштаб DPI, с которым собран снеговик.</summary>
    public float Scale = 1f;

    /// <summary>Сколько шаров собрано: 1–3. Три шара — готовый снеговик.</summary>
    public int Balls = 1;

    public float HalfWidth => BottomRadius + 5f * Scale;

    public float BottomRadius => BallRadius(Scale, 0);

    public float TopY => Balls <= 0 ? BaseY : BallCenterY(Balls - 1) - BallRadius(Scale, Balls - 1);

    /// <summary>Радиус шара по уровню снизу (0 — нижний, 1 — средний, 2 — голова).</summary>
    public static float BallRadius(float scale, int level) => level switch
    {
        0 => 13f * scale,
        1 => 10f * scale,
        _ => 8f * scale,
    };

    /// <summary>Центр шара по уровню; шары слегка вдавлены друг в друга.</summary>
    public float BallCenterY(int level)
    {
        float y = BaseY - BallRadius(Scale, 0);
        if (level >= 1)
        {
            y -= (BallRadius(Scale, 0) + BallRadius(Scale, 1)) * 0.86f;
        }

        if (level >= 2)
        {
            y -= (BallRadius(Scale, 1) + BallRadius(Scale, 2)) * 0.86f;
        }

        return y;
    }
}
