using System.Diagnostics;
using System.Text;
using WinterDesktop.Core.Diagnostics;
using WinterDesktop.Core.Geometry;
using WinterDesktop.Core.Models;
using WinterDesktop.Platform.Interop;

namespace WinterDesktop.Platform;

/// <summary>
/// Отслеживание геометрии верхнеуровневых окон через <c>SetWinEventHook</c>.
///
/// Что читается: HWND, прямоугольник (DWM extended frame bounds), флаги видимости/свёрнутости,
/// класс окна и z-order. Что НЕ читается никогда: заголовок, текст, содержимое, изображения,
/// имя процесса и любые пользовательские данные (§3.2 «Важно», §7 «Безопасность»).
/// </summary>
public sealed class WindowTracker : IDisposable
{
    private const int MaxTrackedWindows = 96;

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "TaskListThumbnailWnd",
        "ForegroundStaging",
        "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow",
        "Windows.Internal.Shell.TabProxyWindow",
        "Shell_InputSwitchTopLevelWindow",
        "SysShadow",
        "#32768",          // системные меню
        "tooltips_class32",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow",
        "ApplicationManager_DesktopShellWindow",
        "DV2ControlHost",
        "MsgrIMEWindowClass",
    };

    private readonly IAppLog _log;
    private readonly MonitorManager _monitors;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _hookReady = new(false);
    private readonly StringBuilder _classNameBuffer = new(256);
    private readonly NativeMethods.WinEventDelegate _winEventCallback;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookObjects;
    private IntPtr _hookForeground;
    private Timer? _refreshTimer;
    private volatile bool _dirty = true;
    private volatile bool _disposed;
    private long _generation;
    private SceneSnapshot _snapshot = SceneSnapshot.Empty;
    private long _lastMonitorCheckTicks;
    private uint _ownProcessId;

    public WindowTracker(IAppLog log, MonitorManager monitors)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _winEventCallback = OnWinEvent;
        _ownProcessId = (uint)Environment.ProcessId;
    }

    /// <summary>Текущий снимок сцены. Всегда валиден (может быть пустым).</summary>
    public SceneSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public IReadOnlyList<MonitorDescriptor> Monitors => _monitors.Monitors;

    /// <summary>Уведомление об изменении геометрии. Вызывается на фоновом потоке.</summary>
    public event Action<SceneSnapshot>? SceneChanged;

    public void Start()
    {
        if (_hookThread is not null) return;

        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "WinterDesktop.WinEventHook",
        };
        _hookThread.Start();

        if (!_hookReady.Wait(3000))
        {
            _log.Warn("Поток WinEventHook не запустился за 3 секунды.");
        }

        // Обновление сцены — не чаще 30 раз в секунду и только при наличии изменений.
        _refreshTimer = new Timer(_ => RefreshIfDirty(), null, 60, 33);

        _log.Info("Отслеживание окон запущено.");
    }

    private void HookThreadProc()
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            const uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;

            _hookObjects = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_CREATE,
                NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                flags);

            _hookForeground = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                flags);

            if (_hookObjects == IntPtr.Zero || _hookForeground == IntPtr.Zero)
            {
                _log.Warn("Часть WinEvent-хуков не установлена; z-order может обновляться с задержкой.");
            }

            _dirty = true;
            _hookReady.Set();

            while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Поток отслеживания окон завершился с ошибкой.", ex);
            _hookReady.Set();
        }
        finally
        {
            if (_hookObjects != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hookObjects);
                _hookObjects = IntPtr.Zero;
            }
            if (_hookForeground != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hookForeground);
                _hookForeground = IntPtr.Zero;
            }
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed) return;

        // События курсора и каретки приходят с другими idObject — они не нужны.
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;

        _dirty = true;
    }

    private void RefreshIfDirty()
    {
        if (_disposed) return;

        long now = Stopwatch.GetTimestamp();
        bool monitorCheckDue = now - _lastMonitorCheckTicks > Stopwatch.Frequency; // раз в секунду
        if (monitorCheckDue)
        {
            _lastMonitorCheckTicks = now;
            if (_monitors.Refresh()) _dirty = true;
        }

        if (!_dirty) return;
        _dirty = false;

        try
        {
            var next = BuildSnapshot();
            var previous = _snapshot;
            if (IsSame(previous, next)) return;

            _snapshot = next;
            SceneChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка обновления сцены окон.", ex);
        }
    }

    private SceneSnapshot BuildSnapshot()
    {
        var monitors = _monitors.Monitors;
        var virtualBounds = _monitors.VirtualBounds;
        var windows = new List<WindowDescriptor>(32);

        NativeMethods.EnumWindows((IntPtr hwnd, IntPtr _) =>
        {
            if (windows.Count >= MaxTrackedWindows) return false;

            try
            {
                if (TryDescribe(hwnd, windows.Count, virtualBounds, out var descriptor))
                {
                    windows.Add(descriptor);
                }
            }
            catch (Exception ex)
            {
                // Одно «странное» окно не должно ломать весь снимок.
                _log.Warn($"Окно 0x{hwnd.ToInt64():X} пропущено: {ex.Message}");
            }

            return true;
        }, IntPtr.Zero);

        return new SceneSnapshot(monitors, windows, Interlocked.Increment(ref _generation));
    }

    private bool TryDescribe(IntPtr hwnd, int zOrder, RectI virtualBounds, out WindowDescriptor descriptor)
    {
        descriptor = default;

        if (!NativeMethods.IsWindowVisible(hwnd)) return false;
        if (NativeMethods.IsIconic(hwnd)) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == _ownProcessId) return false;

        if (IsCloaked(hwnd)) return false;

        _classNameBuffer.Clear();
        int length = NativeMethods.GetClassNameW(hwnd, _classNameBuffer, _classNameBuffer.Capacity);
        if (length <= 0) return false;
        var className = _classNameBuffer.ToString();

        WindowKind kind;
        if (className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
        {
            kind = WindowKind.Taskbar;
        }
        else if (IgnoredClasses.Contains(className))
        {
            return false;
        }
        else
        {
            kind = WindowKind.Normal;
        }

        long exStyle = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        bool noActivate = (exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0;
        bool toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        if (toolWindow && noActivate && kind != WindowKind.Taskbar) return false;

        if (!TryGetVisibleBounds(hwnd, out var bounds)) return false;
        if (bounds.Width < 80 || bounds.Height < 40) return false;

        var clipped = bounds.Intersect(virtualBounds);
        if (clipped.IsEmpty) return false;

        descriptor = new WindowDescriptor(hwnd, clipped, kind, zOrder);
        return true;
    }

    private static bool TryGetVisibleBounds(IntPtr hwnd, out RectI bounds)
    {
        // DWMWA_EXTENDED_FRAME_BOUNDS — видимые границы без невидимой рамки изменения размера.
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT dwmRect,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0)
        {
            bounds = RectI.FromLtrb(dwmRect.Left, dwmRect.Top, dwmRect.Right, dwmRect.Bottom);
            if (!bounds.IsEmpty) return true;
        }

        if (NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            bounds = RectI.FromLtrb(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return !bounds.IsEmpty;
        }

        bounds = default;
        return false;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        int cloaked;
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_CLOAKED,
                out cloaked,
                sizeof(int)) != 0)
        {
            return false;
        }
        return cloaked != 0;
    }

    private static bool IsSame(SceneSnapshot a, SceneSnapshot b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Windows.Count != b.Windows.Count || a.Monitors.Count != b.Monitors.Count) return false;

        for (int i = 0; i < a.Monitors.Count; i++)
        {
            if (a.Monitors[i] != b.Monitors[i]) return false;
        }

        for (int i = 0; i < a.Windows.Count; i++)
        {
            var x = a.Windows[i];
            var y = b.Windows[i];
            if (x.Handle != y.Handle || x.Bounds != y.Bounds || x.Kind != y.Kind) return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _refreshTimer?.Dispose();
        _refreshTimer = null;

        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _hookThread?.Join(2000);
        _hookThread = null;
        _hookReady.Dispose();

        _log.Info("Отслеживание окон остановлено.");
    }
}
