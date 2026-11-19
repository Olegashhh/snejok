namespace WinterDesktop.Core.Models;

/// <summary>Режим снегопада (§3.1). <see cref="Custom"/> включается, когда пользователь изменил пресет.</summary>
public enum SnowMode
{
    Light = 0,
    Normal = 1,
    Blizzard = 2,
    Custom = 3,
}

/// <summary>Кромка окна, на которой может копиться снег (§3.2).</summary>
public enum LedgeSide
{
    Top = 0,
    Bottom = 1,
}

/// <summary>Инструмент взаимодействия (§3.3).</summary>
public enum BrushTool
{
    Mitten = 0,
    Shovel = 1,
    Snowball = 2,
    Snowman = 3,
}

/// <summary>Профиль производительности (§3.10).</summary>
public enum PerformanceProfile
{
    Eco = 0,
    Balanced = 1,
    High = 2,
}

/// <summary>Ограничение частоты кадров (§7).</summary>
public enum FrameRateLimit
{
    /// <summary>По частоте обновления монитора, но не выше 60.</summary>
    Auto = 0,
    Fps30 = 1,
    Fps60 = 2,
    Unlimited = 3,
}

/// <summary>Тип поверхности, на которой может копиться снег.</summary>
public enum WindowKind
{
    Normal = 0,
    /// <summary>Рабочий стол / Progman / WorkerW — не является кромкой для осадков.</summary>
    Shell = 1,
    Taskbar = 2,
}

/// <summary>Форма снежинки. Форма выбирается детерминированно из индекса частицы и Seed.</summary>
public enum SnowShape
{
    Fine = 0,
    Crystal = 1,
    Star = 2,
    Cluster = 3,
    Large = 4,
}

/// <summary>Палитра лампочек гирлянды (этап 3). Цвета чередуются по индексу лампочки.</summary>
public enum GarlandPalette
{
    /// <summary>Холодные голубые и белые огни.</summary>
    Winter = 0,

    /// <summary>Красный, золотой, зелёный и синий — классическая праздничная гирлянда.</summary>
    Festive = 1,

    /// <summary>Тёплые янтарные и оранжевые огни.</summary>
    Warm = 2,

    /// <summary>Розовые, мятные и сиреневые огни.</summary>
    Candy = 3,
}

/// <summary>Режим анимации гирлянды: мерцание, волна, бегущий огонь или ровный свет.</summary>
public enum GarlandAnimation
{
    /// <summary>Каждая лампочка мерцает в своей фазе.</summary>
    Twinkle = 0,

    /// <summary>Волна яркости бежит вдоль нити.</summary>
    Wave = 1,

    /// <summary>Огни загораются по очереди, как в бегущей строке.</summary>
    Chase = 2,

    /// <summary>Все лампочки горят ровно, без анимации.</summary>
    Steady = 3,
}
