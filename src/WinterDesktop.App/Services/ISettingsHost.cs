using WinterDesktop.Core.Models;
using WinterDesktop.Core.Settings;
using WinterDesktop.Renderer;
using WinterDesktop.Settings;

namespace WinterDesktop.App.Services;

/// <summary>
/// Контракт между окном настроек и контроллером приложения. Окно не знает, как устроены
/// поток эффектов, трекер и хранилище, — это упрощает тестирование и замену UI.
/// </summary>
public interface ISettingsHost
{
    AppSettings Settings { get; }

    SettingsStore Store { get; }

    IReadOnlyList<MonitorDescriptor> Monitors { get; }

    OverlayStats Stats { get; }

    bool IsSuspended { get; }

    /// <summary>Эффекты полностью приостановлены (вручную или из-за fullscreen).</summary>
    bool IsPaused { get; }

    string LogsPath { get; }

    /// <summary>Применить текущие настройки немедленно (кнопка «Применить»).</summary>
    void ApplyNow();

    void ClearAll();

    void ClearUnderCursor();

    /// <summary>Пауза/продолжение всех эффектов (кнопка «Пауза» и меню трея).</summary>
    void TogglePause();

    void SetMonitorEnabled(MonitorDescriptor monitor, bool enabled);

    void SetInteractionMode(bool enabled);

    void ResetDefaults();

    void ExportSettings(string path);

    void ImportSettings(string path);

    void OpenLogsFolder();
}
