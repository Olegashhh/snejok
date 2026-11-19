using System.Text;
using WinterDesktop.Platform.Interop;

namespace WinterDesktop.Platform;

/// <summary>
/// Определяет, занимает ли активное окно весь монитор (игра, видео, презентация).
/// Это разрешает автоматическую паузу эффектов (§3.10, §9 Этап 5).
/// </summary>
public sealed class FullscreenDetector
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "LockApp",
        "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow",
    };

    private readonly StringBuilder _buffer = new(128);

    public bool IsFullscreenForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (hwnd == NativeMethods.GetShellWindow() || hwnd == NativeMethods.GetDesktopWindow()) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;

        _buffer.Clear();
        int length = NativeMethods.GetClassNameW(hwnd, _buffer, _buffer.Capacity);
        if (length <= 0) return false;
        if (ShellClasses.Contains(_buffer.ToString())) return false;

        // Развёрнутое окно тоже занимает весь монитор (GetWindowRect у него включает
        // невидимые рамки под панелью задач), но это обычная работа, а не fullscreen.
        // Без этой проверки максимизированный браузер постоянно ставил эффекты на паузу.
        if (NativeMethods.IsZoomed(hwnd)) return false;

        long exStyle = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect)) return false;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return false;

        const int tolerance = 2;
        return Math.Abs(windowRect.Left - info.rcMonitor.Left) <= tolerance
               && Math.Abs(windowRect.Top - info.rcMonitor.Top) <= tolerance
               && Math.Abs(windowRect.Right - info.rcMonitor.Right) <= tolerance
               && Math.Abs(windowRect.Bottom - info.rcMonitor.Bottom) <= tolerance;
    }
}
