using WinterDesktop.Platform.Interop;

namespace WinterDesktop.Platform;

/// <summary>
/// Состояние курсора и кнопок мыши в глобальных координатах виртуального рабочего стола.
/// Опрашивается через <c>GetCursorPos</c> и <c>GetAsyncKeyState</c>: это единственный
/// источник ввода, потому что overlay-окно прозрачно для кликов и не получает WM_MOUSE*.
/// Никакие данные о содержимом окон при этом не читаются (§7).
/// </summary>
public readonly record struct CursorState(
    bool IsValid,
    float X,
    float Y,
    bool PrimaryDown,
    bool PrimaryPressed);

/// <summary>Опрос ввода с подавлением дребезга и вычислением фронта нажатия.</summary>
public sealed class InputState
{
    private bool _primaryWasDown;

    public CursorState Poll()
    {
        bool valid = NativeMethods.GetCursorPos(out var point);

        // Старший бит означает «клавиша нажата прямо сейчас»; младший — «нажата с прошлого опроса».
        bool down = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        bool pressed = down && !_primaryWasDown;
        _primaryWasDown = down;

        return new CursorState(valid, point.X, point.Y, down, pressed);
    }
}
