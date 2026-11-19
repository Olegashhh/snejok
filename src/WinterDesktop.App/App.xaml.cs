using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinterDesktop.App.Services;
using WinterDesktop.Core;
using WinterDesktop.Core.Diagnostics;
using WinterDesktop.Core.Models;
using WinterDesktop.Core.Performance;
using WinterDesktop.Core.Settings;
using WinterDesktop.Platform;
using WinterDesktop.Platform.Diagnostics;
using WinterDesktop.Renderer;
using WinterDesktop.Settings;
using WinterDesktop.SnowEngine;
using Application = System.Windows.Application;

namespace WinterDesktop.App;

/// <summary>
/// Точка сборки приложения: одно окно настроек, один трей, один поток эффектов.
/// Здесь же живёт правило «настройка изменилась → пересобрать снимок для движка».
/// </summary>
public partial class App : Application, ISettingsHost
{
    private const string MutexName = @"Local\WinterDesktop.SingleInstance";

    private FileLog? _log;
    private SettingsStore? _store;
    private MonitorManager? _monitors;
    private WindowTracker? _tracker;
    private OverlayHost? _overlay;
    private FullscreenDetector? _fullscreen;
    private PowerStatusProvider? _power;
    private TrayIcon? _tray;
    private InteractionHotkey? _hotkey;
    private MainWindow? _settingsWindow;
    private bool _settingsTopmost;
    private DispatcherTimer? _applyTimer;
    private DispatcherTimer? _watchTimer;
    private IDisposable? _settingsSubscription;
    private Mutex? _instanceMutex;

    private string _monitorSignature = string.Empty;
    private bool _suspended;
    private bool _fullscreenActive;
    private bool _manuallyPaused;
    private int _adaptiveStep;
    private int _stableSeconds;
    private bool _applying;

    // ------------------------------------------------------------------
    // Жизненный цикл
    // ------------------------------------------------------------------

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!AcquireSingleInstance()) return;

        AppPaths.EnsureCreated();
        _log = new FileLog();
        _log.Info($"Winter Desktop запускается. Версия {typeof(App).Assembly.GetName().Version}.");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        _store = new SettingsStore(_log);
        _store.SettingsReplaced += OnSettingsReplaced;
        var settings = _store.Load();

        if (_store.LastLoadWarning is not null)
        {
            _log.Warn(_store.LastLoadWarning);
        }

        _monitors = new MonitorManager(_log);
        _fullscreen = new FullscreenDetector();
        _power = new PowerStatusProvider();
        _power.Refresh();

        _overlay = new OverlayHost(_log);
        _overlay.SnowmenChanged += OnSnowmenChanged;
        _overlay.Start();

        _tracker = new WindowTracker(_log, _monitors);
        _tracker.SceneChanged += OnSceneChanged;
        _overlay.UpdateScene(_tracker.Snapshot);
        _tracker.Start();

        HookSettings(settings);
        ApplyNow();

        _tray = new TrayIcon(this);
        _tray.UpdateState(settings.Snow.Enabled, settings.Garlands.Enabled, settings.Tools.InteractionMode, _suspended, _manuallyPaused);

        // Аварийный выход из режима взаимодействия: в нём overlay перехватывает мышь,
        // поэтому меню трея и окно настроек мышью недоступны.
        _hotkey = new InteractionHotkey(ToggleInteractionFromHotkey);
        if (!_hotkey.Registered)
        {
            _log.Warn("Не удалось зарегистрировать Ctrl+Alt+W: комбинацию занимает другая программа. " +
                      "Режим взаимодействия придётся выключать клавиатурой в окне настроек.");

            // Без горячей клавиши старт в режиме взаимодействия оставил бы мышь
            // заблокированной без понятного способа выйти.
            if (settings.Tools.InteractionMode)
            {
                SetInteractionMode(false);
                _log.Warn("Режим взаимодействия выключен при старте: аварийная клавиша недоступна.");
            }
        }

        _watchTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnWatchTick, Dispatcher);
        _watchTimer.Start();

        // На первом запуске показываем окно настроек, иначе приложение «молча» уйдёт в трей
        // и пользователь не поймёт, что оно работает.
        bool forceTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        bool firstRun = !_store.LoadedFromDisk;
        bool startMinimized = forceTray || (settings.General.StartMinimized && !firstRun);
        if (!startMinimized) ShowSettingsWindow();

        _log.Info("Запуск завершён.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _log?.Info("Winter Desktop завершается.");

            _watchTimer?.Stop();
            _applyTimer?.Stop();
            _settingsSubscription?.Dispose();
            _store?.Flush();
            _hotkey?.Dispose();
            _tray?.Dispose();
            _overlay?.Dispose();
            _tracker?.Dispose();
            _store?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            _log?.Info("Завершение выполнено.");
            _log?.Dispose();
        }
        catch
        {
            // Выход не должен падать из-за очистки ресурсов.
        }

        base.OnExit(e);
    }

    private bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (createdNew) return true;

        System.Windows.MessageBox.Show(
            "Winter Desktop уже запущен. Значок программы находится в области уведомлений.",
            "Winter Desktop",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        Shutdown();
        return false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Необработанная ошибка интерфейса.", e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) _log?.Error("Необработанная ошибка.", exception);
    }

    // ------------------------------------------------------------------
    // Настройки
    // ------------------------------------------------------------------

    public AppSettings Settings => _store?.Current ?? new AppSettings();

    public SettingsStore Store => _store!;

    public IReadOnlyList<MonitorDescriptor> Monitors => _monitors?.Monitors ?? Array.Empty<MonitorDescriptor>();

    public OverlayStats Stats => _overlay?.Stats ?? new OverlayStats(0, 0, 0f, 0d, 0d, false, false, 0, 0, 0, 0, 0);

    public bool IsSuspended => _suspended;

    public bool IsPaused => _suspended;

    public string LogsPath => AppPaths.LogsDirectory;

    private void HookSettings(AppSettings settings)
    {
        _settingsSubscription?.Dispose();
        _settingsSubscription = SettingsTree.Subscribe(settings, (_, _) => ScheduleApply());
    }

    private void OnSettingsReplaced(AppSettings settings)
    {
        HookSettings(settings);
        _adaptiveStep = 0;
        _settingsWindow?.Bind(settings);
        ApplyNow();
    }

    /// <summary>
    /// Снеговики изменились в движке. Событие приходит с потока эффектов: снимок
    /// уже неизменяем, а запись в настройки выполняется на UI-потоке.
    /// </summary>
    private void OnSnowmenChanged(IReadOnlyList<SnowmanRecord> snowmen)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => PersistSnowmen(snowmen)));
    }

    private void PersistSnowmen(IReadOnlyList<SnowmanRecord> snowmen)
    {
        if (_store is null) return;

        var current = _store.Current.Snowmen.Items;
        if (SameSnowmen(current, snowmen)) return;

        var copy = new List<SnowmanRecord>(snowmen.Count);
        foreach (var record in snowmen)
        {
            copy.Add(new SnowmanRecord
            {
                Monitor = record.Monitor,
                X = record.X,
                BaseY = record.BaseY,
                Scale = record.Scale,
                Balls = record.Balls,
            });
        }

        _store.Current.Snowmen.Items = copy;
        _store.SaveSoon();
        _log?.Info($"Снеговики сохранены: {copy.Count}.");
    }

    private static bool SameSnowmen(IReadOnlyList<SnowmanRecord> a, IReadOnlyList<SnowmanRecord> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            var left = a[i];
            var right = b[i];
            if (!string.Equals(left.Monitor, right.Monitor, StringComparison.OrdinalIgnoreCase)) return false;
            if (Math.Abs(left.X - right.X) > 0.5 || Math.Abs(left.BaseY - right.BaseY) > 0.5) return false;
            if (Math.Abs(left.Scale - right.Scale) > 0.01 || left.Balls != right.Balls) return false;
        }

        return true;
    }

    /// <summary>Изменения из UI приходят пачками: применяем их один раз через 140 мс.</summary>
    private void ScheduleApply()
    {
        if (_applying) return;

        if (_applyTimer is null)
        {
            _applyTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(140),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    _applyTimer?.Stop();
                    ApplyNow();
                },
                Dispatcher);
        }

        _applyTimer.Stop();
        _applyTimer.Start();
    }

    public void ApplyNow()
    {
        if (_store is null || _overlay is null) return;

        _applying = true;
        try
        {
            var settings = _store.Current;
            var budget = BuildBudget(settings);
            var simulation = SimulationSettings.From(settings, budget);
            var scales = BuildMonitorScales(settings);

            _overlay.UpdateSettings(simulation, scales, budget.TargetFps);
            _overlay.SetInteractionMode(settings.Tools.InteractionMode);
            UpdateSettingsWindowTopmost(settings.Tools.InteractionMode);
            SyncAutoStart(settings);
            UpdateTrayState();
        }
        finally
        {
            _applying = false;
        }
    }

    private PerformanceBudget BuildBudget(AppSettings settings)
    {
        var budget = PerformanceBudget.ForProfile(settings.Performance.Profile);

        if (settings.Performance.MaxParticles > 0)
        {
            budget = budget.WithParticles(settings.Performance.MaxParticles);
        }

        budget = budget.WithFpsLimit(settings.Performance.FrameRateLimit, 60);

        if (settings.Performance.ReduceOnBattery && _power is { IsAvailable: true, IsOnBattery: true })
        {
            budget = budget.Degrade(1);
        }

        if (_adaptiveStep > 0) budget = budget.Degrade(_adaptiveStep);
        return budget;
    }

    private float[] BuildMonitorScales(AppSettings settings)
    {
        var monitors = _monitors?.Monitors ?? Array.Empty<MonitorDescriptor>();
        var result = new float[monitors.Count];

        for (int i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i];
            bool enabled = settings.Monitors.IsEnabled(monitor);
            result[i] = enabled ? (float)settings.Monitors.IntensityScaleFor(monitor) : 0f;
        }

        return result;
    }

    private void SyncAutoStart(AppSettings settings)
    {
        bool desired = settings.General.RunAtStartup;
        if (AutoStartManager.IsEnabled() == desired) return;

        if (!AutoStartManager.SetEnabled(desired))
        {
            _log?.Warn("Не удалось изменить запись автозапуска.");
        }
    }

    // ------------------------------------------------------------------
    // Сцена и таймеры
    // ------------------------------------------------------------------

    private void OnSceneChanged(SceneSnapshot snapshot)
    {
        _overlay?.UpdateScene(snapshot);

        // При смене набора мониторов набор масштабов нужно пересчитать.
        var signature = BuildMonitorSignature(snapshot.Monitors);
        if (signature == _monitorSignature) return;

        _monitorSignature = signature;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScheduleApply));
    }

    private static string BuildMonitorSignature(IReadOnlyList<MonitorDescriptor> monitors)
    {
        var builder = new System.Text.StringBuilder(96);
        foreach (var monitor in monitors)
        {
            builder.Append(monitor.DeviceName).Append('|').Append(monitor.Bounds).Append('|').Append(monitor.Dpi).Append(';');
        }

        return builder.ToString();
    }

    private void OnWatchTick(object? sender, EventArgs e)
    {
        if (_store is null) return;
        var settings = _store.Current;

        // Пауза в полноэкранном режиме (§3.10).
        bool fullscreen = settings.Performance.PauseInFullscreen && _fullscreen?.IsFullscreenForeground() == true;
        SetFullscreen(fullscreen);

        // Питание: при переходе на батарею бюджет пересчитывается.
        if (_power is not null)
        {
            bool wasOnBattery = _power.IsOnBattery;
            _power.Refresh();
            if (wasOnBattery != _power.IsOnBattery) ApplyNow();
        }

        ApplyAdaptiveQuality(settings);

        _settingsWindow?.RefreshStats();
    }

    /// <summary>
    /// Адаптивное качество: при устойчивой просадке FPS снижаем бюджет, при запасе — возвращаем.
    /// Значения не сохраняются в файл: это временная поправка на текущую нагрузку (§3.10).
    /// </summary>
    private void ApplyAdaptiveQuality(AppSettings settings)
    {
        if (!settings.Performance.AdaptiveQuality || _overlay is null) return;

        var stats = _overlay.Stats;
        if (stats.Fps < 1d) return;

        bool slow = stats.Fps < 42d;

        if (slow && _adaptiveStep < 2)
        {
            _adaptiveStep++;
            _stableSeconds = 0;
            _log?.Info($"Просадка FPS ({stats.Fps:0.#}); качество понижено до шага {_adaptiveStep}.");
            ApplyNow();
            return;
        }

        if (_adaptiveStep > 0 && stats.Fps > 55d)
        {
            _stableSeconds++;
            if (_stableSeconds >= 12)
            {
                _adaptiveStep--;
                _stableSeconds = 0;
                _log?.Info($"Стабильные {stats.Fps:0.#} FPS; качество повышено до шага {_adaptiveStep}.");
                ApplyNow();
            }
        }
        else
        {
            _stableSeconds = 0;
        }
    }

    private void SetFullscreen(bool active)
    {
        if (_fullscreenActive == active) return;
        _fullscreenActive = active;
        ApplySuspension();
    }

    /// <summary>
    /// Пауза — это вручную включённая пользователем остановка эффектов. Fullscreen-пауза
    /// живёт отдельным флагом: снятие одного не должно возобновлять эффекты, если активен другой.
    /// </summary>
    public void TogglePause()
    {
        _manuallyPaused = !_manuallyPaused;
        _log?.Info(_manuallyPaused ? "Эффекты приостановлены пользователем." : "Эффекты возобновлены пользователем.");
        ApplySuspension();
    }

    private void ApplySuspension()
    {
        bool suspended = _manuallyPaused || _fullscreenActive;
        if (_suspended != suspended)
        {
            _suspended = suspended;
            _overlay?.SetSuspended(suspended);
        }

        UpdateTrayState();
    }

    private void UpdateTrayState()
    {
        _tray?.UpdateState(
            Settings.Snow.Enabled,
            Settings.Garlands.Enabled,
            Settings.Tools.InteractionMode,
            _suspended,
            _manuallyPaused);
    }

    /// <summary>
    /// В режиме взаимодействия overlay перехватывает мышь на всём экране, поэтому окно
    /// настроек поднимается над ним — иначе снять режим мышью невозможно.
    /// </summary>
    private void UpdateSettingsWindowTopmost(bool enabled)
    {
        if (_settingsTopmost == enabled) return;
        _settingsTopmost = enabled;

        if (_settingsWindow is not null)
        {
            _settingsWindow.Topmost = enabled;
        }
    }

    // ------------------------------------------------------------------
    // ISettingsHost
    // ------------------------------------------------------------------

    public void ClearAll()
    {
        _overlay?.ClearAll();
        _log?.Info("Снег очищен по команде пользователя.");
    }

    public void ClearUnderCursor()
    {
        _overlay?.ClearWindowUnderCursor();
    }

    /// <summary>Очистка снега на одном мониторе из меню трея (§3.9).</summary>
    public void ClearMonitorFromTray(string deviceName)
    {
        _overlay?.ClearMonitor(deviceName);
    }

    public void SetMonitorEnabled(MonitorDescriptor monitor, bool enabled)
    {
        if (_store is null) return;
        var collection = _store.Current.Monitors;

        if (enabled)
        {
            if (collection.AllMonitors) return;

            collection.GetOrCreate(monitor.StorageKey).Enabled = true;
        }
        else
        {
            if (collection.AllMonitors)
            {
                // Материализуем текущее состояние, чтобы отключить только один монитор.
                collection.AllMonitors = false;
                foreach (var other in _monitors?.Monitors ?? Array.Empty<MonitorDescriptor>())
                {
                    collection.GetOrCreate(other.StorageKey).Enabled = true;
                }
            }

            collection.GetOrCreate(monitor.StorageKey).Enabled = false;
        }

        _store.SaveSoon();
        ApplyNow();
    }

    public void SetInteractionMode(bool enabled)
    {
        Settings.Tools.InteractionMode = enabled;
        ApplyNow();
    }

    /// <summary>
    /// Ctrl+Alt+W переключает режим взаимодействия. Горячая клавиша — единственный способ
    /// выйти из режима, когда мышь уже перехвачена overlay-слоем.
    /// </summary>
    private void ToggleInteractionFromHotkey()
    {
        bool enabled = !Settings.Tools.InteractionMode;
        SetInteractionMode(enabled);
        _log?.Info(enabled
            ? "Режим взаимодействия включён горячей клавишей Ctrl+Alt+W."
            : "Режим взаимодействия выключен горячей клавишей Ctrl+Alt+W.");
    }

    public void ResetDefaults()
    {
        if (_store is null) return;
        _store.Reset();
        _log?.Info("Настройки сброшены к значениям по умолчанию.");
    }

    public void ExportSettings(string path) => _store?.Export(path);

    public void ImportSettings(string path) => _store?.Import(path);

    public void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogsDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log?.Warn($"Не удалось открыть папку логов: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Окно настроек
    // ------------------------------------------------------------------

    public void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new MainWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Topmost = _settingsTopmost;
            _settingsWindow.Show();
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
        }

        _settingsWindow.Focus();
    }
}
