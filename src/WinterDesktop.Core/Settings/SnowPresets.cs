using WinterDesktop.Core.Models;

namespace WinterDesktop.Core.Settings;

/// <summary>
/// Пресеты режимов снегопада (§3.1). Режим — это не отдельный движок, а согласованный набор
/// значений, который пользователь затем может свободно менять (переход в <see cref="SnowMode.Custom"/>).
/// </summary>
public static class SnowPresets
{
    public static void Apply(SnowSettings target, SnowMode mode)
    {
        switch (mode)
        {
            case SnowMode.Light:
                target.Intensity = 0.22;
                target.FallSpeedScale = 0.75;
                target.WindStrength = 0.05;
                target.Turbulence = 0.25;
                target.Spread = 0.35;
                target.LargeFlakeChance = 0.02;
                target.ForegroundShare = 0.4;
                target.Mode = SnowMode.Light;
                break;

            case SnowMode.Normal:
                target.Intensity = 0.55;
                target.FallSpeedScale = 1.0;
                target.WindStrength = 0.15;
                target.Turbulence = 0.5;
                target.Spread = 0.5;
                target.LargeFlakeChance = 0.06;
                target.ForegroundShare = 0.35;
                target.Mode = SnowMode.Normal;
                break;

            case SnowMode.Blizzard:
                target.Intensity = 1.0;
                target.FallSpeedScale = 1.55;
                target.WindStrength = 0.8;
                target.Turbulence = 1.0;
                target.Spread = 0.8;
                target.LargeFlakeChance = 0.12;
                target.ForegroundShare = 0.3;
                target.Mode = SnowMode.Blizzard;
                break;

            case SnowMode.Custom:
            default:
                target.Mode = SnowMode.Custom;
                break;
        }
    }

    public static string DisplayName(SnowMode mode) => mode switch
    {
        SnowMode.Light => "Лёгкий снег",
        SnowMode.Normal => "Обычный снег",
        SnowMode.Blizzard => "Метель",
        SnowMode.Custom => "Свой режим",
        _ => mode.ToString(),
    };
}
