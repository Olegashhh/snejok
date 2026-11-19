using System.Drawing;
using System.Windows.Forms;
using WinterDesktop.Core.Models;

namespace WinterDesktop.App;

/// <summary>
/// Значок в области уведомлений: быстрый доступ к основным действиям без открытия окна (§3.7).
/// Меню всегда отражает актуальное состояние настроек — источником истины остаётся
/// <see cref="App"/>.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _snowItem;
    private readonly ToolStripMenuItem _garlandsItem;
    private readonly ToolStripMenuItem _interactionItem;
    private readonly ToolStripMenuItem _monitorsItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _clearItem;
    private readonly ToolStripMenuItem _clearUnderCursorItem;
    private bool _disposed;

    public TrayIcon(App app)
    {
        _app = app;

        _snowItem = new ToolStripMenuItem("Снегопад", null, (_, _) => ToggleSnow()) { CheckOnClick = false };
        _garlandsItem = new ToolStripMenuItem("Гирлянды", null, (_, _) => ToggleGarlands()) { CheckOnClick = false };
        _interactionItem = new ToolStripMenuItem("Режим взаимодействия", null, (_, _) => ToggleInteraction())
        {
            CheckOnClick = false,
            ShortcutKeyDisplayString = "Ctrl+Alt+W",
        };
        _clearItem = new ToolStripMenuItem("Очистить весь снег", null, (_, _) => _app.ClearAll());
        _clearUnderCursorItem = new ToolStripMenuItem("Очистить под курсором", null, (_, _) => _app.ClearUnderCursor());
        _monitorsItem = new ToolStripMenuItem("Мониторы");
        _monitorsItem.DropDownOpening += (_, _) => RebuildMonitorMenu();
        _pauseItem = new ToolStripMenuItem("Пауза", null, (_, _) => _app.TogglePause()) { CheckOnClick = false };

        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Открыть настройки", null, (_, _) => _app.ShowSettingsWindow())
        {
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };

        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_snowItem);
        menu.Items.Add(_garlandsItem);
        menu.Items.Add(_interactionItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_monitorsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_clearItem);
        menu.Items.Add(_clearUnderCursorItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => _app.Shutdown()));

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Winter Desktop",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => _app.ShowSettingsWindow();
    }

    public void UpdateState(bool snowEnabled, bool garlandsEnabled, bool interactionMode, bool suspended, bool manuallyPaused)
    {
        _snowItem.Checked = snowEnabled;
        _garlandsItem.Checked = garlandsEnabled;
        _interactionItem.Checked = interactionMode;
        _pauseItem.Checked = manuallyPaused;
        _pauseItem.Text = manuallyPaused ? "Продолжить эффекты" : "Пауза";

        string state = suspended ? "пауза" : snowEnabled ? "снегопад" : "выключено";
        string text = $"Winter Desktop — {state}";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ToggleSnow()
    {
        var settings = _app.Settings;
        settings.Snow.Enabled = !settings.Snow.Enabled;
        _app.Store.SaveSoon();
        _app.ApplyNow();
    }

    private void ToggleGarlands()
    {
        var settings = _app.Settings;
        settings.Garlands.Enabled = !settings.Garlands.Enabled;
        _app.Store.SaveSoon();
        _app.ApplyNow();
    }

    private void ToggleInteraction()
    {
        _app.SetInteractionMode(!_app.Settings.Tools.InteractionMode);
    }

    private void RebuildMonitorMenu()
    {
        _monitorsItem.DropDownItems.Clear();

        var settings = _app.Settings;
        var allItem = new ToolStripMenuItem("Все мониторы")
        {
            CheckOnClick = true,
            Checked = settings.Monitors.AllMonitors,
        };

        allItem.Click += (_, _) =>
        {
            settings.Monitors.AllMonitors = allItem.Checked;
            _app.Store.SaveSoon();
            _app.ApplyNow();
        };

        _monitorsItem.DropDownItems.Add(allItem);
        _monitorsItem.DropDownItems.Add(new ToolStripSeparator());

        var monitors = _app.Monitors;
        if (monitors.Count == 0)
        {
            _monitorsItem.DropDownItems.Add(new ToolStripMenuItem("Мониторы не найдены") { Enabled = false });
            return;
        }

        foreach (var monitor in monitors)
        {
            var item = new ToolStripMenuItem(Describe(monitor))
            {
                CheckOnClick = true,
                Checked = settings.Monitors.IsEnabled(monitor),
            };

            item.Click += (_, _) => _app.SetMonitorEnabled(monitor, item.Checked);
            _monitorsItem.DropDownItems.Add(item);
        }

        _monitorsItem.DropDownItems.Add(new ToolStripSeparator());

        foreach (var monitor in monitors)
        {
            var item = new ToolStripMenuItem($"Очистить {monitor.FriendlyName}");
            item.Click += (_, _) => _app.ClearMonitorFromTray(monitor.DeviceName);
            _monitorsItem.DropDownItems.Add(item);
        }
    }

    private static string Describe(MonitorDescriptor monitor)
    {
        string suffix = monitor.IsPrimary ? " (основной)" : string.Empty;
        return $"{monitor.FriendlyName} · {monitor.Bounds.Width}×{monitor.Bounds.Height}{suffix}";
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("/Resources/app.ico", UriKind.Relative));
            if (resource?.Stream is { } stream)
            {
                using (stream) return new Icon(stream);
            }
        }
        catch
        {
            // Ниже — запасной вариант через иконку исполняемого файла.
        }

        try
        {
            var path = Environment.ProcessPath;
            if (path is not null)
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // Иконка — не критичный ресурс.
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}
