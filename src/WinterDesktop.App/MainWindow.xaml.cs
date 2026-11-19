using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WinterDesktop.App.Services;
using WinterDesktop.Core;
using WinterDesktop.Core.Models;
using WinterDesktop.Core.Settings;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

namespace WinterDesktop.App;

/// <summary>
/// Окно настроек. Сознательно тонкое: значения живут в <see cref="AppSettings"/>, а применение
/// и побочные эффекты — в <see cref="ISettingsHost"/>. Изменения уходят в движок с задержкой,
/// поэтому быстрое перетаскивание ползунка не создаёт лишних перестроений сцены.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] ModeNames =
    {
        "Лёгкий снег",
        "Обычный снег",
        "Метель",
        "Свой режим",
    };

    private static readonly string[] ToolNames = { "Варежка", "Лопата", "Снежок", "Снеговик" };

    private static readonly string[] GarlandPaletteNames =
    {
        "Зимняя (голубые и белые)",
        "Праздничная (красный, золото, зелень)",
        "Тёплая (янтарь и оранж)",
        "Карамельная (розовый и мята)",
    };

    private static readonly string[] GarlandAnimationNames =
    {
        "Мерцание",
        "Переливы",
        "Бегущий огонь",
        "Ровный свет",
    };

    private static readonly string[] ProfileNames = { "Экономный", "Сбалансированный", "Высокое качество" };
    private static readonly string[] FrameRateNames = { "Авто (до 60)", "30 FPS", "60 FPS", "Максимум" };

    private readonly ISettingsHost _host;
    private IDisposable? _settingsSubscription;
    private AppSettings _settings;
    private bool _suppress;
    private bool _applyingPreset;

    public MainWindow(ISettingsHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        InitializeComponent();

        _settings = host.Settings;
        Bind(_settings);
    }

    /// <summary>Перепривязывает окно к новому объекту настроек (после сброса или импорта).</summary>
    public void Bind(AppSettings settings)
    {
        _settings = settings;

        _settingsSubscription?.Dispose();
        _settingsSubscription = SettingsTree.Subscribe(settings, OnSettingChanged);

        _suppress = true;
        try
        {
            DataContext = settings;

            ModeCombo.ItemsSource = ModeNames;
            ToolCombo.ItemsSource = ToolNames;
            GarlandPaletteCombo.ItemsSource = GarlandPaletteNames;
            GarlandAnimationCombo.ItemsSource = GarlandAnimationNames;
            ProfileCombo.ItemsSource = ProfileNames;
            FpsCombo.ItemsSource = FrameRateNames;

            ModeCombo.SelectedIndex = (int)settings.Snow.Mode;
            ToolCombo.SelectedIndex = (int)settings.Tools.ActiveTool;
            GarlandPaletteCombo.SelectedIndex = (int)settings.Garlands.Palette;
            GarlandAnimationCombo.SelectedIndex = (int)settings.Garlands.Animation;
            ProfileCombo.SelectedIndex = (int)settings.Performance.Profile;
            FpsCombo.SelectedIndex = (int)settings.Performance.FrameRateLimit;
            AllMonitorsCheck.IsChecked = settings.Monitors.AllMonitors;

            SettingsPathText.Text = AppPaths.SettingsFile;

            int particleCap = settings.Performance.MaxParticles;
            AboutText.Text =
                $"Winter Desktop {typeof(App).Assembly.GetName().Version} — прозрачный слой снега поверх рабочего стола.\n" +
                $"Предел частиц по профилю: {(particleCap > 0 ? particleCap.ToString() : "автоматически")}.\n" +
                "Данные хранятся локально, сетевые функции отсутствуют.";
        }
        finally
        {
            _suppress = false;
        }

        RebuildMonitors();
        RefreshStats();
    }

    // ------------------------------------------------------------------
    // Обработчики элементов
    // ------------------------------------------------------------------

    private void OnSettingChanged(SettingsNode node, string? property)
    {
        if (_suppress || _applyingPreset) return;

        if (node == _settings.Snow && property == nameof(SnowSettings.Mode))
        {
            _suppress = true;
            ModeCombo.SelectedIndex = (int)_settings.Snow.Mode;
            _suppress = false;
            return;
        }

        // Любая правка параметров пресета означает «свой режим» (§3.1).
        if (node == _settings.Snow && property is not null && property != nameof(SnowSettings.Enabled))
        {
            _suppress = true;
            _settings.Snow.Mode = SnowMode.Custom;
            ModeCombo.SelectedIndex = (int)SnowMode.Custom;
            _suppress = false;
        }
    }

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _applyingPreset) return;

        var mode = (SnowMode)Math.Clamp(ModeCombo.SelectedIndex, 0, ModeNames.Length - 1);

        _applyingPreset = true;
        try
        {
            if (mode == SnowMode.Custom)
            {
                _settings.Snow.Mode = SnowMode.Custom;
            }
            else
            {
                SnowPresets.Apply(_settings.Snow, mode);
            }
        }
        finally
        {
            _applyingPreset = false;
        }

        _host.ApplyNow();
    }

    private void OnToolSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _settings.Tools.ActiveTool = (BrushTool)Math.Clamp(ToolCombo.SelectedIndex, 0, ToolNames.Length - 1);
        _host.ApplyNow();
    }

    private void OnGarlandPaletteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _settings.Garlands.Palette = (GarlandPalette)Math.Clamp(GarlandPaletteCombo.SelectedIndex, 0, GarlandPaletteNames.Length - 1);
        _host.ApplyNow();
    }

    private void OnGarlandAnimationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _settings.Garlands.Animation = (GarlandAnimation)Math.Clamp(GarlandAnimationCombo.SelectedIndex, 0, GarlandAnimationNames.Length - 1);
        _host.ApplyNow();
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _settings.Performance.Profile = (PerformanceProfile)Math.Clamp(ProfileCombo.SelectedIndex, 0, ProfileNames.Length - 1);
        _host.ApplyNow();
    }

    private void OnFpsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _settings.Performance.FrameRateLimit = (FrameRateLimit)Math.Clamp(FpsCombo.SelectedIndex, 0, FrameRateNames.Length - 1);
        _host.ApplyNow();
    }

    private void OnAllMonitorsClick(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _settings.Monitors.AllMonitors = AllMonitorsCheck.IsChecked == true;
        _host.Store.SaveSoon();
        _host.ApplyNow();
        RebuildMonitors();
    }

    private void OnMonitorChecked(object sender, RoutedEventArgs e)
    {
        if (_suppress || sender is not CheckBox box || box.Tag is not MonitorDescriptor monitor) return;

        _host.SetMonitorEnabled(monitor, box.IsChecked == true);

        _suppress = true;
        AllMonitorsCheck.IsChecked = _settings.Monitors.AllMonitors;
        _suppress = false;

        RebuildMonitors();
    }

    private void OnClearAll(object sender, RoutedEventArgs e) => _host.ClearAll();

    private void OnClearUnderCursor(object sender, RoutedEventArgs e) => _host.ClearUnderCursor();

    private void OnApplyAll(object sender, RoutedEventArgs e) => _host.ApplyNow();

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        _host.TogglePause();
        UpdatePauseButton();
    }

    private void UpdatePauseButton()
    {
        PauseButton.Content = _host.IsPaused ? "Продолжить" : "Пауза";
    }

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Сбросить все настройки к значениям по умолчанию? Текущие параметры будут потеряны.",
            "Winter Desktop",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _host.ResetDefaults();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт настроек",
            FileName = "winter-desktop-settings.json",
            Filter = "Настройки Winter Desktop (*.json)|*.json|Все файлы (*.*)|*.*",
            DefaultExt = ".json",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _host.ExportSettings(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError("Не удалось экспортировать настройки.", ex);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт настроек",
            Filter = "Настройки Winter Desktop (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _host.ImportSettings(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError("Файл настроек не подходит: импорт отменён, текущие настройки не изменены.", ex);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => _host.OpenLogsFolder();

    // ------------------------------------------------------------------
    // Обновление данных
    // ------------------------------------------------------------------

    private void RebuildMonitors()
    {
        MonitorsPanel.Children.Clear();

        var monitors = _host.Monitors;
        if (monitors.Count == 0)
        {
            MonitorsPanel.Children.Add(new TextBlock
            {
                Text = "Мониторы не обнаружены.",
                Style = (Style)FindResource("Hint"),
            });

            return;
        }

        _suppress = true;
        try
        {
            foreach (var monitor in monitors)
            {
                var checkBox = new CheckBox
                {
                    Content = $"{monitor.FriendlyName} · {monitor.Bounds.Width}×{monitor.Bounds.Height} · {monitor.Dpi} DPI" +
                              (monitor.IsPrimary ? " · основной" : string.Empty),
                    IsChecked = _settings.Monitors.IsEnabled(monitor),
                    Margin = new Thickness(0, 4, 0, 4),
                    Tag = monitor,
                };

                checkBox.Checked += OnMonitorChecked;
                checkBox.Unchecked += OnMonitorChecked;
                MonitorsPanel.Children.Add(checkBox);
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    public void RefreshStats()
    {
        if (!IsVisible) return;

        var stats = _host.Stats;
        UpdatePauseButton();

        string state = stats.IsSuspended ? " · пауза" : string.Empty;
        string renderer = stats.IsWarp ? " · программный рендер (WARP)" : string.Empty;

        StatsText.Text =
            $"FPS: {stats.Fps:0.#} · кадр {stats.FrameMilliseconds:0.0} мс · снежинок: {stats.FlakeCount} · " +
            $"кромок: {stats.LedgeCount} · снеговиков: {stats.SnowmanCount} · снежков: {stats.SnowballCount} · " +
            $"гирлянд: {stats.GarlandCount} ({stats.GarlandBulbCount} ламп.) · " +
            $"масса сугробов: {stats.SnowMass:0}{renderer}{state}";

        PerfStatsText.Text =
            $"Активных мониторов: {stats.MonitorCount} · частиц: {stats.FlakeCount} · " +
            $"FPS: {stats.Fps:0.#} · время кадра: {stats.FrameMilliseconds:0.0} мс{renderer}{state}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _settingsSubscription?.Dispose();
        _settingsSubscription = null;
        base.OnClosed(e);
    }

    private void ShowError(string message, Exception exception)
    {
        MessageBox.Show(this, $"{message}\n\n{exception.Message}", "Winter Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
