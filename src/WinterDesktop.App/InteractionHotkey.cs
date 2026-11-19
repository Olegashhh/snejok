using System.Windows.Forms;
using WinterDesktop.Platform.Interop;

namespace WinterDesktop.App;

/// <summary>
/// Глобальная горячая клавиша Ctrl+Alt+W: переключает режим взаимодействия.
/// Нужна как аварийный выход — пока режим включён, overlay перехватывает мышь на всём
/// экране, и ни окно настроек, ни меню трея не открыть. Клавиатуру overlay не забирает
/// (<c>WS_EX_NOACTIVATE</c>), поэтому комбинация срабатывает поверх любого приложения.
/// </summary>
internal sealed class InteractionHotkey : IDisposable
{
    /// <summary>Идентификатор горячей клавиши в пределах потока.</summary>
    private const int HotkeyId = 0x5744; // 'WD'

    private readonly HotkeyWindow _window;
    private bool _registered;
    private bool _disposed;

    public InteractionHotkey(Action onPressed)
    {
        _window = new HotkeyWindow(onPressed);
        _registered = NativeMethods.RegisterHotKey(
            _window.Handle,
            HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_W);
    }

    /// <summary>False, если комбинацию уже заняла другая программа.</summary>
    public bool Registered => _registered;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
        }

        _window.DestroyHandle();
    }

    /// <summary>
    /// Message-only окно: не видно, не попадает в Alt+Tab и не мешает перечислению окон,
    /// но исправно получает <c>WM_HOTKEY</c> на потоке UI.
    /// </summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action _onPressed;

        public HotkeyWindow(Action onPressed)
        {
            _onPressed = onPressed;
            CreateHandle(new CreateParams
            {
                Caption = "WinterDesktopHotkey",
                Parent = NativeMethods.HWND_MESSAGE,
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                _onPressed();
                return;
            }

            base.WndProc(ref m);
        }
    }
}
