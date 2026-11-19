using WinterDesktop.Core.Models;

namespace WinterDesktop.Core.Settings;

/// <summary>
/// Корневой объект настроек. Структура плоская и явная: каждое поле сериализуется в JSON
/// под своим camelCase-именем, что делает файл читаемым и удобным для ручного бэкапа (§5).
/// </summary>
public sealed class AppSettings : SettingsNode
{
    /// <summary>Текущая версия схемы. Увеличивается при несовместимых изменениях + миграция.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public GeneralSettings General { get; set; } = new();

    public SnowSettings Snow { get; set; } = new();

    public AccumulationSettings Accumulation { get; set; } = new();

    public ToolSettings Tools { get; set; } = new();

    /// <summary>Гирлянды на верхних кромках окон (этап 3).</summary>
    public GarlandsSettings Garlands { get; set; } = new();

    /// <summary>Собранные снеговики: сохраняются между запусками (§3.3, этап 3).</summary>
    public SnowmenSettings Snowmen { get; set; } = new();

    public PerformanceSettings Performance { get; set; } = new();

    public MonitorCollectionSettings Monitors { get; set; } = new();

    /// <summary>Полный сброс к значениям по умолчанию. Возвращает новый экземпляр.</summary>
    public static AppSettings CreateDefault() => new();
}

public sealed class GeneralSettings : SettingsNode
{
    private bool _runAtStartup;
    private bool _startMinimized = true;
    private string _language = "ru";
    private bool _showNotifications = true;

    /// <summary>Автозапуск при входе в Windows через HKCU\...\Run (§3.8).</summary>
    public bool RunAtStartup
    {
        get => _runAtStartup;
        set => Set(ref _runAtStartup, value);
    }

    /// <summary>Стартовать свёрнутым в трей (без окна настроек).</summary>
    public bool StartMinimized
    {
        get => _startMinimized;
        set => Set(ref _startMinimized, value);
    }

    /// <summary>Код языка интерфейса: "ru" или "en".</summary>
    public string Language
    {
        get => _language;
        set => Set(ref _language, string.IsNullOrWhiteSpace(value) ? "ru" : value);
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set => Set(ref _showNotifications, value);
    }
}

public sealed class SnowSettings : SettingsNode
{
    private bool _enabled = true;
    private SnowMode _mode = SnowMode.Normal;
    private double _intensity = 0.55;
    private double _flakeSizeScale = 1.0;
    private double _fallSpeedScale = 1.0;
    private double _windStrength = 0.15;
    private double _windAngleDegrees;
    private double _turbulence = 0.5;
    private double _spread = 0.5;
    private double _opacity = 0.9;
    private double _shapeVariety = 0.7;
    private double _largeFlakeChance = 0.06;
    private double _foregroundShare = 0.35;

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public SnowMode Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    /// <summary>0..1 — доля от бюджета частиц.</summary>
    public double Intensity
    {
        get => _intensity;
        set => Set(ref _intensity, Clamp01(value));
    }

    /// <summary>0.4..2.5 — множитель базового радиуса снежинки.</summary>
    public double FlakeSizeScale
    {
        get => _flakeSizeScale;
        set => Set(ref _flakeSizeScale, Math.Clamp(value, 0.4, 2.5));
    }

    /// <summary>0.2..3.0 — множитель скорости падения.</summary>
    public double FallSpeedScale
    {
        get => _fallSpeedScale;
        set => Set(ref _fallSpeedScale, Math.Clamp(value, 0.2, 3.0));
    }

    /// <summary>-1..1 — сила ветра; знак задаёт сторону, величина зависит от угла.</summary>
    public double WindStrength
    {
        get => _windStrength;
        set => Set(ref _windStrength, Math.Clamp(value, -1.0, 1.0));
    }

    /// <summary>0..360°, 0° = ветер вправо, 90° = вниз.</summary>
    public double WindAngleDegrees
    {
        get => _windAngleDegrees;
        set => Set(ref _windAngleDegrees, NormalizeAngle(value));
    }

    /// <summary>0..1 — амплитуда вихревых колебаний (боковое покачивание).</summary>
    public double Turbulence
    {
        get => _turbulence;
        set => Set(ref _turbulence, Clamp01(value));
    }

    /// <summary>0..1 — горизонтальный разброс начальной скорости.</summary>
    public double Spread
    {
        get => _spread;
        set => Set(ref _spread, Clamp01(value));
    }

    /// <summary>0.1..1 — прозрачность снежинок.</summary>
    public double Opacity
    {
        get => _opacity;
        set => Set(ref _opacity, Math.Clamp(value, 0.1, 1.0));
    }

    /// <summary>0..1 — 0: только простые круглые снежинки, 1: все формы.</summary>
    public double ShapeVariety
    {
        get => _shapeVariety;
        set => Set(ref _shapeVariety, Clamp01(value));
    }

    /// <summary>0..1 — вероятность появления крупной снежинки.</summary>
    public double LargeFlakeChance
    {
        get => _largeFlakeChance;
        set => Set(ref _largeFlakeChance, Math.Clamp(value, 0.0, 0.5));
    }

    /// <summary>0..1 — доля снежинок, летящих перед окнами (не оседают на верхних кромках).</summary>
    public double ForegroundShare
    {
        get => _foregroundShare;
        set => Set(ref _foregroundShare, Clamp01(value));
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private static double NormalizeAngle(double degrees)
    {
        if (double.IsNaN(degrees)) return 0;
        double normalized = degrees % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }
}

public sealed class AccumulationSettings : SettingsNode
{
    private bool _enabled = true;
    private bool _windowTops = true;
    private bool _windowBottoms = true;
    private bool _taskbar = true;
    private bool _screenBottom = true;
    private double _maxThicknessDip = 24;
    private double _depositRate = 1.0;

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public bool WindowTops
    {
        get => _windowTops;
        set => Set(ref _windowTops, value);
    }

    public bool WindowBottoms
    {
        get => _windowBottoms;
        set => Set(ref _windowBottoms, value);
    }

    public bool Taskbar
    {
        get => _taskbar;
        set => Set(ref _taskbar, value);
    }

    public bool ScreenBottom
    {
        get => _screenBottom;
        set => Set(ref _screenBottom, value);
    }

    /// <summary>Максимальная толщина сугроба в DIP (§3.2 «ограничение максимального количества»).</summary>
    public double MaxThicknessDip
    {
        get => _maxThicknessDip;
        set => Set(ref _maxThicknessDip, Math.Clamp(value, 3.0, 60.0));
    }

    /// <summary>0.2..3 — во сколько раз быстрее накапливается снег.</summary>
    public double DepositRate
    {
        get => _depositRate;
        set => Set(ref _depositRate, Math.Clamp(value, 0.2, 3.0));
    }
}

public sealed class ToolSettings : SettingsNode
{
    private BrushTool _activeTool = BrushTool.Mitten;
    private double _mittenRadiusDip = 55;
    private double _mittenStrength = 0.6;
    private double _shovelRadiusDip = 160;
    private bool _interactionMode;

    public BrushTool ActiveTool
    {
        get => _activeTool;
        set => Set(ref _activeTool, value);
    }

    /// <summary>Радиус варежки в DIP.</summary>
    public double MittenRadiusDip
    {
        get => _mittenRadiusDip;
        set => Set(ref _mittenRadiusDip, Math.Clamp(value, 12.0, 200.0));
    }

    /// <summary>0.1..1 — сила смахивания варежкой.</summary>
    public double MittenStrength
    {
        get => _mittenStrength;
        set => Set(ref _mittenStrength, Math.Clamp(value, 0.1, 1.0));
    }

    public double ShovelRadiusDip
    {
        get => _shovelRadiusDip;
        set => Set(ref _shovelRadiusDip, Math.Clamp(value, 40.0, 500.0));
    }

    /// <summary>Включён ли режим взаимодействия (иначе клики проходят сквозь слой).</summary>
    public bool InteractionMode
    {
        get => _interactionMode;
        set => Set(ref _interactionMode, value);
    }
}

/// <summary>
/// Гирлянды на верхних кромках окон: палитра, режим мигания, скорость и геометрия
/// лампочек. Снегопад и гирлянды независимы — их можно включать по отдельности.
/// </summary>
public sealed class GarlandsSettings : SettingsNode
{
    private bool _enabled;
    private GarlandPalette _palette = GarlandPalette.Festive;
    private GarlandAnimation _animation = GarlandAnimation.Twinkle;
    private double _speed = 1.0;
    private double _spacingDip = 26.0;
    private double _bulbSizeDip = 4.5;
    private double _glow = 0.5;

    /// <summary>Гирлянды по умолчанию выключены, чтобы не менять привычный вид стола.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public GarlandPalette Palette
    {
        get => _palette;
        set => Set(ref _palette, value);
    }

    public GarlandAnimation Animation
    {
        get => _animation;
        set => Set(ref _animation, value);
    }

    /// <summary>0.1..4 — множитель скорости мигания и переливов.</summary>
    public double Speed
    {
        get => _speed;
        set => Set(ref _speed, Math.Clamp(value, 0.1, 4.0));
    }

    /// <summary>12..80 DIP — расстояние между соседними лампочками.</summary>
    public double SpacingDip
    {
        get => _spacingDip;
        set => Set(ref _spacingDip, Math.Clamp(value, 12.0, 80.0));
    }

    /// <summary>2..12 DIP — диаметр лампочки.</summary>
    public double BulbSizeDip
    {
        get => _bulbSizeDip;
        set => Set(ref _bulbSizeDip, Math.Clamp(value, 2.0, 12.0));
    }

    /// <summary>0..1 — яркость и радиус ореола вокруг горящих лампочек.</summary>
    public double Glow
    {
        get => _glow;
        set => Set(ref _glow, Math.Clamp(value, 0.0, 1.0));
    }
}

public sealed class SnowmenSettings : SettingsNode
{
    private List<SnowmanRecord> _items = new();

    /// <summary>
    /// Список снеговиков. Список заменяется целиком при изменении из движка:
    /// <see cref="SettingsTree"/> не обходит элементы списков, поэтому хранилище
    /// сохраняет его по явному <c>SaveSoon()</c> из приложения.
    /// </summary>
    public List<SnowmanRecord> Items
    {
        get => _items;
        set => Set(ref _items, value ?? new List<SnowmanRecord>());
    }
}

public sealed class PerformanceSettings : SettingsNode
{
    private PerformanceProfile _profile = PerformanceProfile.Balanced;
    private FrameRateLimit _frameRateLimit = FrameRateLimit.Auto;
    private bool _adaptiveQuality = true;
    private bool _pauseInFullscreen = true;
    private bool _reduceOnBattery = true;
    private int _maxParticles;

    public PerformanceProfile Profile
    {
        get => _profile;
        set => Set(ref _profile, value);
    }

    public FrameRateLimit FrameRateLimit
    {
        get => _frameRateLimit;
        set => Set(ref _frameRateLimit, value);
    }

    /// <summary>Автоматически снижать качество при просадке кадров (§3.10).</summary>
    public bool AdaptiveQuality
    {
        get => _adaptiveQuality;
        set => Set(ref _adaptiveQuality, value);
    }

    public bool PauseInFullscreen
    {
        get => _pauseInFullscreen;
        set => Set(ref _pauseInFullscreen, value);
    }

    public bool ReduceOnBattery
    {
        get => _reduceOnBattery;
        set => Set(ref _reduceOnBattery, value);
    }

    /// <summary>0 = автоматически по профилю; иначе жёсткий предел частиц.</summary>
    public int MaxParticles
    {
        get => _maxParticles;
        set => Set(ref _maxParticles, Math.Clamp(value, 0, 20000));
    }
}

public sealed class MonitorCollectionSettings : SettingsNode
{
    private bool _allMonitors = true;

    /// <summary>Показывать снег на всех мониторах. Если false — используется <see cref="Overrides"/>.</summary>
    public bool AllMonitors
    {
        get => _allMonitors;
        set => Set(ref _allMonitors, value);
    }

    /// <summary>Переопределения по стабильному ключу монитора (имя устройства).</summary>
    public Dictionary<string, MonitorSettings> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public MonitorSettings GetOrCreate(string key)
    {
        if (Overrides.TryGetValue(key, out var existing)) return existing;
        var created = new MonitorSettings();
        Overrides[key] = created;
        return created;
    }

    public bool IsEnabled(MonitorDescriptor monitor)
    {
        if (AllMonitors) return true;
        return Overrides.TryGetValue(monitor.StorageKey, out var settings) && settings.Enabled;
    }

    public double IntensityScaleFor(MonitorDescriptor monitor) =>
        Overrides.TryGetValue(monitor.StorageKey, out var settings) ? settings.IntensityScale : 1.0;
}

public sealed class MonitorSettings : SettingsNode
{
    private bool _enabled = true;
    private double _intensityScale = 1.0;

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>0.1..2 — множитель интенсивности на этом мониторе (§3.9).</summary>
    public double IntensityScale
    {
        get => _intensityScale;
        set => Set(ref _intensityScale, Math.Clamp(value, 0.1, 2.0));
    }
}
