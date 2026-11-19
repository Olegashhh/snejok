using WinterDesktop.Core.Geometry;

namespace WinterDesktop.Core.Models;

/// <summary>
/// Неизменяемый снимок окружения: мониторы и геометрия окон на момент времени.
/// Публикуется трекером и читается движком снега без блокировок.
/// </summary>
public sealed class SceneSnapshot
{
    public static SceneSnapshot Empty { get; } = new(Array.Empty<MonitorDescriptor>(), Array.Empty<WindowDescriptor>(), 0);

    public SceneSnapshot(
        IReadOnlyList<MonitorDescriptor> monitors,
        IReadOnlyList<WindowDescriptor> windows,
        long generation)
    {
        Monitors = monitors;
        Windows = windows;
        Generation = generation;
    }

    public IReadOnlyList<MonitorDescriptor> Monitors { get; }

    /// <summary>Окна, отсортированные по z-order: индекс 0 — самое верхнее.</summary>
    public IReadOnlyList<WindowDescriptor> Windows { get; }

    public long Generation { get; }

    /// <summary>Границы всего виртуального рабочего стола (объединение мониторов).</summary>
    public RectI VirtualBounds
    {
        get
        {
            if (Monitors.Count == 0) return RectI.Empty;
            var result = Monitors[0].Bounds;
            for (int i = 1; i < Monitors.Count; i++) result = result.Union(Monitors[i].Bounds);
            return result;
        }
    }

    public MonitorDescriptor? MonitorAt(int x, int y)
    {
        for (int i = 0; i < Monitors.Count; i++)
        {
            if (Monitors[i].Bounds.Contains(x, y)) return Monitors[i];
        }
        return null;
    }
}
