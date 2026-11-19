namespace WinterDesktop.Core.Models;

/// <summary>
/// Сохранённый снеговик. Координаты — физические пиксели виртуального рабочего стола,
/// <see cref="BaseY"/> — линия основания (низ нижнего шара). Запись не привязана к HWND:
/// при восстановлении снеговик притягивается к ближайшей существующей кромке.
/// </summary>
public sealed class SnowmanRecord
{
    /// <summary>Стабильный ключ монитора (имя устройства, например DISPLAY1).</summary>
    public string Monitor { get; set; } = string.Empty;

    public double X { get; set; }

    /// <summary>Низ нижнего шара: на этой линии снеговик стоит.</summary>
    public double BaseY { get; set; }

    /// <summary>Масштаб DPI, с которым снеговик был собран.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Сколько шаров собрано: 1–3. Три шара — готовый снеговик.</summary>
    public int Balls { get; set; } = 1;
}
