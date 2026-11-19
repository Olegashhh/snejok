namespace WinterDesktop.SnowEngine;

/// <summary>
/// Ввод за кадр. Курсор передаётся всегда (он опрашивается через GetCursorPos),
/// поэтому «воздушный след» работает и в режиме click-through.
/// </summary>
public readonly record struct SnowInput(
    float CursorX,
    float CursorY,
    bool CursorValid,
    bool PrimaryDown,
    bool PrimaryPressed);
