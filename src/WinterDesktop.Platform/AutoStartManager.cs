using Microsoft.Win32;
using WinterDesktop.Platform.Interop;

namespace WinterDesktop.Platform;

/// <summary>
/// Автозапуск через HKCU\Software\Microsoft\Windows\CurrentVersion\Run (§3.8).
/// HKCU выбран осознанно: не требует прав администратора и не создаёт служб.
/// </summary>
public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinterDesktop";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled, string? executablePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var path = executablePath ?? Environment.ProcessPath;
                if (string.IsNullOrEmpty(path)) return false;
                key.SetValue(ValueName, $"\"{path}\" --tray", RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Позиция курсора. Опрашивается раз в кадр — глобальный хук мыши не нужен.</summary>
public static class CursorInfo
{
    public static bool TryGetPosition(out int x, out int y)
    {
        if (NativeMethods.GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }
}
