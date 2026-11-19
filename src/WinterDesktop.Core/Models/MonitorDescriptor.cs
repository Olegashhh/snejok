using WinterDesktop.Core.Geometry;

namespace WinterDesktop.Core.Models;

/// <summary>
/// Описание монитора в физических пикселях виртуального рабочего стола.
/// </summary>
public sealed record MonitorDescriptor(
    string DeviceName,
    string FriendlyName,
    RectI Bounds,
    RectI WorkArea,
    uint Dpi,
    bool IsPrimary)
{
    /// <summary>Масштаб относительно 96 DPI (1.0 = 100%).</summary>
    public float Scale => Dpi / 96f;

    /// <summary>Стабильный ключ для хранения настроек: имя устройства не зависит от порядка подключения.</summary>
    public string StorageKey => string.IsNullOrEmpty(DeviceName) ? PrimaryFallback : DeviceName;

    private string PrimaryFallback => IsPrimary ? "PRIMARY" : $"{Bounds.Left}x{Bounds.Top}";
}
