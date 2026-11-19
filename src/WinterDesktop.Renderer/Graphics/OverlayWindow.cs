using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinterDesktop.Core.Diagnostics;
using WinterDesktop.Core.Models;
using WinterDesktop.Platform.Interop;
using AlphaMode = Vortice.DCommon.AlphaMode;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;

namespace WinterDesktop.Renderer.Graphics;

/// <summary>
/// Одно прозрачное окно-оверлей на монитор: <c>WS_POPUP</c> без редирекции поверхности,
/// flip-model swapchain с премультиплицированной альфой, подключённый через DirectComposition.
///
/// Окно никогда не активируется, не показывается в панели задач и по умолчанию прозрачно
/// для мыши. В режиме взаимодействия <c>WS_EX_TRANSPARENT</c> снимается, и окно начинает
/// получать ввод (§3.3).
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "WinterDesktopOverlayWindow";
    private const int IdcArrow = 32512;

    private const uint WM_NCCREATE = 0x0081;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_NCACTIVATE = 0x0086;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_PAINT = 0x000F;
    private const int ErrorClassAlreadyExists = 1410;
    private const int D2DErrRecreateTarget = unchecked((int)0x8899000C);

    private static readonly object RegistryGate = new();
    private static readonly Dictionary<IntPtr, OverlayWindow> Registry = new();
    private static readonly NativeMethods.WndProc WindowProcDelegate = StaticWindowProc;
    private static bool _classRegistered;
    private static bool _classFailed;

    private readonly GraphicsDevice _graphics;
    private readonly MonitorDescriptor _monitor;
    private readonly IAppLog _log;
    private readonly int _width;
    private readonly int _height;

    private IntPtr _hwnd;
    private IDXGISwapChain1? _swapChain;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _visual;
    private ID2D1Bitmap1? _targetBitmap;
    private bool _clickThrough = true;
    private bool _visible;
    private bool _disposed;

    public OverlayWindow(GraphicsDevice graphics, MonitorDescriptor monitor, IAppLog log)
    {
        _graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        var bounds = monitor.Bounds;
        _width = Math.Max(1, bounds.Width);
        _height = Math.Max(1, bounds.Height);

        EnsureClassRegistered();

        long exStyle = NativeMethods.WS_EX_NOREDIRECTIONBITMAP
                       | NativeMethods.WS_EX_TOPMOST
                       | NativeMethods.WS_EX_TOOLWINDOW
                       | NativeMethods.WS_EX_NOACTIVATE
                       | NativeMethods.WS_EX_LAYERED;

        if (_clickThrough) exStyle |= NativeMethods.WS_EX_TRANSPARENT;

        _hwnd = NativeMethods.CreateWindowExW(
            unchecked((int)exStyle),
            ClassName,
            "Winter Desktop",
            NativeMethods.WS_POPUP,
            bounds.Left,
            bounds.Top,
            _width,
            _height,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandleW(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowExW не создал overlay-окно (код {Marshal.GetLastWin32Error()}).");
        }

        lock (RegistryGate) Registry[_hwnd] = this;

        // WS_EX_LAYERED обязателен: только у слоёного окна WS_EX_TRANSPARENT
        // отключает попадание мыши (в том числе в чужие процессы). Без него
        // прозрачный DComp-оверлей перехватывает клики на весь экран.
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, NativeMethods.LWA_ALPHA);

        try
        {
            CreateGraphicsResources();
            log.Info($"Overlay создан для {monitor.DeviceName}: {bounds} ({_width}x{_height}).");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public MonitorDescriptor Monitor => _monitor;

    public IntPtr Handle => _hwnd;

    /// <summary>Устройство потеряно (смена адаптера, выход из спящего режима).</summary>
    public bool NeedsRecreate { get; private set; }

    public bool IsVisible => _visible;

    private static void EnsureClassRegistered()
    {
        if (_classRegistered) return;
        if (_classFailed) throw new InvalidOperationException("Класс overlay-окна не зарегистрирован.");

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcDelegate),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = NativeMethods.GetModuleHandleW(null),
            hIcon = IntPtr.Zero,
            hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, new IntPtr(IdcArrow)),
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = ClassName,
            hIconSm = IntPtr.Zero,
        };

        if (NativeMethods.RegisterClassExW(ref wc) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorClassAlreadyExists)
            {
                _classFailed = true;
                throw new InvalidOperationException($"RegisterClassExW завершился с кодом {error}.");
            }
        }

        _classRegistered = true;
    }

    private void CreateGraphicsResources()
    {
        var description = new SwapChainDescription1(
            (uint)_width,
            (uint)_height,
            Format.B8G8R8A8_UNorm,
            stereo: false,
            Usage.RenderTargetOutput,
            bufferCount: 2,
            Scaling.Stretch,
            SwapEffect.FlipSequential,
            DxgiAlphaMode.Premultiplied,
            SwapChainFlags.None);

        _swapChain = _graphics.Factory.CreateSwapChainForComposition(_graphics.D3DDevice, description);

        using (var surface = _swapChain.GetBuffer<IDXGISurface>(0))
        {
            // Для flip-model back buffer обязательна комбинация Target | CannotDraw:
            // поверхность служит целью отрисовки, но не может быть источником для DrawBitmap.
            var properties = new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.Target | BitmapOptions.CannotDraw);

            _targetBitmap = _graphics.D2DContext.CreateBitmapFromDxgiSurface(surface, properties);
        }

        _graphics.Composition.CreateTargetForHwnd(_hwnd, true, out _compositionTarget).CheckError();
        _visual = _graphics.Composition.CreateVisual();
        _visual.SetContent(_swapChain);
        _compositionTarget!.SetRoot(_visual);
        _graphics.Composition.Commit().CheckError();
    }

    public void Show()
    {
        if (_hwnd == IntPtr.Zero || _visible) return;

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(
            _hwnd,
            new IntPtr(NativeMethods.HWND_TOPMOST),
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        _visible = true;
    }

    public void Hide()
    {
        if (_hwnd == IntPtr.Zero || !_visible) return;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        _visible = false;
    }

    public void SetClickThrough(bool enabled)
    {
        if (_clickThrough == enabled) return;
        _clickThrough = enabled;

        long exStyle = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        long updated = enabled
            ? exStyle | NativeMethods.WS_EX_TRANSPARENT
            : exStyle & ~(long)NativeMethods.WS_EX_TRANSPARENT;

        NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(updated));
    }

    /// <summary>Начинает кадр: назначает цель и очищает поверхность в прозрачный цвет.</summary>
    public void BeginDraw()
    {
        var context = _graphics.D2DContext;
        context.Target = _targetBitmap;
        context.Transform = Matrix3x2.CreateTranslation(-_monitor.Bounds.Left, -_monitor.Bounds.Top);
        context.BeginDraw();
        context.Clear(new Color4(0f, 0f, 0f, 0f));
    }

    /// <summary>Завершает кадр и показывает его. Возвращает false, если устройство нужно пересоздать.</summary>
    public bool EndDrawAndPresent()
    {
        var result = _graphics.D2DContext.EndDraw();
        if (result.Failure)
        {
            if (result.Code == D2DErrRecreateTarget)
            {
                NeedsRecreate = true;
                return false;
            }

            _log.Warn($"EndDraw завершился ошибкой 0x{result.Code:X8}.");
            return false;
        }

        var present = _swapChain!.Present(1);
        if (present.Failure)
        {
            if (present.Code == D2DErrRecreateTarget)
            {
                NeedsRecreate = true;
            }
            else
            {
                _log.Warn($"Present завершился ошибкой 0x{present.Code:X8}.");
            }

            return false;
        }

        return true;
    }

    private static IntPtr StaticWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                // Клики проходят сквозь окно, пока не включён режим взаимодействия.
                // Основную работу делает WS_EX_TRANSPARENT у слоёного окна;
                // HTTRANSPARENT оставлен как дополнительная страховка.
                return TryGetWindow(hwnd, out var window) && window._clickThrough
                    ? new IntPtr(NativeMethods.HTTRANSPARENT)
                    : new IntPtr(NativeMethods.HTCLIENT);

            case WM_ERASEBKGND:
                return new IntPtr(1);

            case WM_PAINT:
                // Рисуем только через DirectComposition: обычная отрисовка не нужна.
                NativeMethods.ValidateRect(hwnd, IntPtr.Zero);
                return IntPtr.Zero;

            case WM_NCACTIVATE:
                return new IntPtr(1);
        }

        // WM_SETCURSOR не перехватываем: DefWindowProc ставит курсор класса (стрелку).
        // Раньше здесь возвращалась «1» без SetCursor, и курсор залипал в том виде,
        // который был до входа на overlay (ожидание/занято/текст).
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static bool TryGetWindow(IntPtr hwnd, out OverlayWindow window)
    {
        lock (RegistryGate)
        {
            return Registry.TryGetValue(hwnd, out window!);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                lock (RegistryGate) Registry.Remove(_hwnd);
            }

            // Общий Direct2D-контекст не должен держать ссылку на освобождаемую цель.
            if (_targetBitmap is not null && ReferenceEquals(_graphics.D2DContext.Target, _targetBitmap))
            {
                _graphics.D2DContext.Target = null;
            }
        }
        catch (Exception)
        {
            // Освобождение ресурсов не должно бросать исключения.
        }

        _targetBitmap?.Dispose();
        _targetBitmap = null;

        _visual?.Dispose();
        _visual = null;

        _compositionTarget?.Dispose();
        _compositionTarget = null;

        _swapChain?.Dispose();
        _swapChain = null;

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
