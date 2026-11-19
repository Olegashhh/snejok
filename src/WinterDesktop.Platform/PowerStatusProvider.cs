using WinterDesktop.Platform.Interop;

namespace WinterDesktop.Platform;

/// <summary>
/// Состояние питания. На батарее профиль производительности понижается (§3.10).
/// </summary>
public sealed class PowerStatusProvider
{
    public bool IsOnBattery { get; private set; }

    public int BatteryPercent { get; private set; } = 100;

    public bool IsAvailable { get; private set; }

    public void Refresh()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
        {
            IsAvailable = false;
            return;
        }

        IsAvailable = true;
        IsOnBattery = status.ACLineStatus == 0;
        BatteryPercent = status.BatteryLifePercent == 255 ? 100 : status.BatteryLifePercent;
    }
}
