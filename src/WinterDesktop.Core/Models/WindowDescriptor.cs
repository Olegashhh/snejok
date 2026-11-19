using WinterDesktop.Core.Geometry;

namespace WinterDesktop.Core.Models;

/// <summary>
/// Только геометрия окна. Ядро намеренно не содержит заголовков, имён процессов и любых
/// других данных, которые могли бы раскрыть содержимое окон (§3.2, §7 «Безопасность»).
/// </summary>
public readonly record struct WindowDescriptor(
    IntPtr Handle,
    RectI Bounds,
    WindowKind Kind,
    int ZOrder)
{
    public bool IsEligibleForLedges => Kind != WindowKind.Shell && !Bounds.IsEmpty;
}
