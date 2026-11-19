using WinterDesktop.Core;
using WinterDesktop.Core.Diagnostics;
using WinterDesktop.Core.Geometry;
using WinterDesktop.Core.Models;
using WinterDesktop.Platform.Interop;

namespace WinterDesktop.Platform;

/// <summary>
/// Перечисление мониторов и их DPI. Обновляется по таймеру и реагирует на изменение
/// конфигурации дисплеев без перезапуска (§3.9).
/// </summary>
public sealed class MonitorManager
{
    private readonly IAppLog _log;
    private List<MonitorDescriptor> _monitors = new();
    private string _signature = string.Empty;

    public MonitorManager(IAppLog log)
    {
        _log = log;
        Refresh(force: true);
    }

    public IReadOnlyList<MonitorDescriptor> Monitors => _monitors;

    public bool HasMonitors => _monitors.Count > 0;

    public RectI VirtualBounds
    {
        get
        {
            if (_monitors.Count == 0) return RectI.Empty;
            var bounds = _monitors[0].Bounds;
            for (int i = 1; i < _monitors.Count; i++) bounds = bounds.Union(_monitors[i].Bounds);
            return bounds;
        }
    }

    /// <summary>Перечитывает конфигурацию. Возвращает true, если что-то изменилось.</summary>
    public bool Refresh(bool force = false)
    {
        var detected = Detect();
        var signature = BuildSignature(detected);
        if (!force && signature == _signature) return false;

        bool changed = signature != _signature;
        _signature = signature;
        _monitors = detected;

        if (changed)
        {
            _log.Info($"Конфигурация мониторов: {_monitors.Count} шт., виртуальный стол {VirtualBounds}");
        }

        return changed;
    }

    public MonitorDescriptor? MonitorAt(int x, int y)
    {
        foreach (var monitor in _monitors)
        {
            if (monitor.Bounds.Contains(x, y)) return monitor;
        }
        return _monitors.FirstOrDefault(m => m.IsPrimary);
    }

    public MonitorDescriptor? Primary => _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();

    private List<MonitorDescriptor> Detect()
    {
        var result = new List<MonitorDescriptor>(4);
        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info)) return true;

                uint dpiX = 96;
                uint dpiY = 96;
                if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out dpiX, out dpiY) != 0)
                {
                    dpiX = 96;
                }

                var device = info.szDevice ?? string.Empty;
                var bounds = RectI.FromLtrb(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
                var work = RectI.FromLtrb(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
                bool primary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;

                result.Add(new MonitorDescriptor(device, device, bounds, work, Math.Max(96u, dpiX), primary));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось перечислить мониторы.", ex);
        }

        if (result.Count == 0)
        {
            // Крайний случай: мониторы недоступны (например, сессия RDP в момент перехода).
            int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            int w = Math.Max(640, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
            int h = Math.Max(480, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
            result.Add(new MonitorDescriptor("PRIMARY", "PRIMARY", RectI.FromSize(x, y, w, h), RectI.FromSize(x, y, w, h), 96, true));
        }

        result.Sort((a, b) => a.Bounds.Left != b.Bounds.Left
            ? a.Bounds.Left.CompareTo(b.Bounds.Left)
            : a.Bounds.Top.CompareTo(b.Bounds.Top));

        return result;
    }

    private static string BuildSignature(List<MonitorDescriptor> monitors)
    {
        var builder = new System.Text.StringBuilder(128);
        foreach (var m in monitors)
        {
            builder.Append(m.DeviceName).Append('|')
                .Append(m.Bounds.Left).Append(',').Append(m.Bounds.Top).Append(',')
                .Append(m.Bounds.Right).Append(',').Append(m.Bounds.Bottom).Append('|')
                .Append(m.WorkArea.Right).Append('|')
                .Append(m.Dpi).Append('|')
                .Append(m.IsPrimary ? '1' : '0').Append(';');
        }
        return builder.ToString();
    }
}
