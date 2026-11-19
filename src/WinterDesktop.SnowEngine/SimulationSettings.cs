using WinterDesktop.Core.Models;
using WinterDesktop.Core.Performance;
using WinterDesktop.Core.Settings;

namespace WinterDesktop.SnowEngine;

/// <summary>
/// Неизменяемый снимок настроек для движка. Живёт на render-потоке, поэтому настройки
/// копируются в него на UI-потоке при каждом изменении — гонок за объекты настроек нет.
/// </summary>
public sealed record SimulationSettings
{
    public bool SnowEnabled { get; init; } = true;
    public float Intensity { get; init; } = 0.55f;
    public float FlakeSizeScale { get; init; } = 1f;
    public float FallSpeedScale { get; init; } = 1f;
    public float WindStrength { get; init; } = 0.15f;
    public float WindAngleDegrees { get; init; }
    public float Turbulence { get; init; } = 0.5f;
    public float Spread { get; init; } = 0.5f;
    public float Opacity { get; init; } = 0.9f;
    public float ShapeVariety { get; init; } = 0.7f;
    public float LargeFlakeChance { get; init; } = 0.06f;
    public float ForegroundShare { get; init; } = 0.35f;

    public bool AccumulationEnabled { get; init; } = true;
    public bool WindowTops { get; init; } = true;
    public bool WindowBottoms { get; init; } = true;
    public bool Taskbar { get; init; } = true;
    public bool ScreenBottom { get; init; } = true;
    public float MaxThicknessDip { get; init; } = 18f;
    public float DepositRate { get; init; } = 1f;

    public BrushTool ActiveTool { get; init; } = BrushTool.Mitten;
    public float MittenRadiusDip { get; init; } = 55f;
    public float MittenStrength { get; init; } = 0.6f;
    public float ShovelRadiusDip { get; init; } = 160f;
    public bool InteractionMode { get; init; }

    public bool GarlandsEnabled { get; init; }
    public GarlandPalette GarlandPalette { get; init; } = GarlandPalette.Festive;
    public GarlandAnimation GarlandAnimation { get; init; } = GarlandAnimation.Twinkle;
    public float GarlandSpeed { get; init; } = 1f;
    public float GarlandSpacingDip { get; init; } = 26f;
    public float GarlandBulbSizeDip { get; init; } = 4.5f;
    public float GarlandGlow { get; init; } = 0.5f;

    /// <summary>Снеговики из настроек: применяются один раз при первом снимке сцены.</summary>
    public IReadOnlyList<SnowmanRecord> Snowmen { get; init; } = Array.Empty<SnowmanRecord>();

    public int MaxParticles { get; init; } = 2600;
    public float QualityScale { get; init; } = 1f;

    public static SimulationSettings Default { get; } = new();

    /// <summary>Единственная точка перевода пользовательских настроек в значения движка.</summary>
    public static SimulationSettings From(AppSettings settings, PerformanceBudget budget)
    {
        var snow = settings.Snow;
        var acc = settings.Accumulation;
        var tools = settings.Tools;
        var garlands = settings.Garlands;

        return new SimulationSettings
        {
            SnowEnabled = snow.Enabled,
            Intensity = (float)snow.Intensity,
            FlakeSizeScale = (float)snow.FlakeSizeScale,
            FallSpeedScale = (float)snow.FallSpeedScale,
            WindStrength = (float)snow.WindStrength,
            WindAngleDegrees = (float)snow.WindAngleDegrees,
            Turbulence = (float)snow.Turbulence,
            Spread = (float)snow.Spread,
            Opacity = (float)snow.Opacity,
            ShapeVariety = (float)snow.ShapeVariety,
            LargeFlakeChance = (float)snow.LargeFlakeChance,
            ForegroundShare = (float)snow.ForegroundShare,

            AccumulationEnabled = acc.Enabled,
            WindowTops = acc.WindowTops,
            WindowBottoms = acc.WindowBottoms,
            Taskbar = acc.Taskbar,
            ScreenBottom = acc.ScreenBottom,
            MaxThicknessDip = (float)acc.MaxThicknessDip,
            DepositRate = (float)acc.DepositRate,

            ActiveTool = tools.ActiveTool,
            MittenRadiusDip = (float)tools.MittenRadiusDip,
            MittenStrength = (float)tools.MittenStrength,
            ShovelRadiusDip = (float)tools.ShovelRadiusDip,
            InteractionMode = tools.InteractionMode,

            GarlandsEnabled = garlands.Enabled,
            GarlandPalette = garlands.Palette,
            GarlandAnimation = garlands.Animation,
            GarlandSpeed = (float)garlands.Speed,
            GarlandSpacingDip = (float)garlands.SpacingDip,
            GarlandBulbSizeDip = (float)garlands.BulbSizeDip,
            GarlandGlow = (float)garlands.Glow,

            Snowmen = SnapshotSnowmen(settings.Snowmen.Items),

            MaxParticles = budget.MaxParticles,
            QualityScale = budget.QualityScale,
        };
    }

    /// <summary>Глубокая копия списка снеговиков: движок не должен видеть мутации настроек.</summary>
    private static SnowmanRecord[] SnapshotSnowmen(List<SnowmanRecord>? source)
    {
        if (source is null || source.Count == 0) return Array.Empty<SnowmanRecord>();

        var result = new SnowmanRecord[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            var item = source[i];
            result[i] = new SnowmanRecord
            {
                Monitor = item.Monitor,
                X = item.X,
                BaseY = item.BaseY,
                Scale = item.Scale,
                Balls = item.Balls,
            };
        }

        return result;
    }
}
