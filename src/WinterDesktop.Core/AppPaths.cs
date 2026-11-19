namespace WinterDesktop.Core;

/// <summary>
/// Единое место, где приложение хранит свои данные. Всё лежит в
/// <c>%LOCALAPPDATA%\WinterDesktop</c> — без записи в Program Files и без прав администратора (§5, §7).
/// </summary>
public static class AppPaths
{
    private const string FolderName = "WinterDesktop";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string SettingsBackupFile => SettingsFile + ".bak";

    public static string AssetsDirectory => Path.Combine(Root, "assets");

    public static string LogsDirectory => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
