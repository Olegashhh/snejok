using System.Collections.Concurrent;
using System.Diagnostics;
using WinterDesktop.Core.Diagnostics;
using WinterDesktop.Core.Geometry;
using WinterDesktop.Core.Models;
using WinterDesktop.Renderer.Graphics;
using WinterDesktop.SnowEngine;
using WinterDesktop.Platform;
using WinterDesktop.Platform.Interop;

namespace WinterDesktop.Renderer;

/// <summary>Снимок состояния эффектов для интерфейса и трея.</summary>
public sealed record OverlayStats(
    int FlakeCount,
    int LedgeCount,
    float SnowMass,
    double Fps,
    double FrameMilliseconds,
    bool IsWarp,
    bool IsSuspended,
    int MonitorCount,
    int SnowmanCount,
    int SnowballCount,
    int GarlandCount,
    int GarlandBulbCount);

/// <summary>
/// Владелец потока эффектов. Единственный поток создаёт окна, устройства и симуляцию,
/// поэтому графика и физика не требуют блокировок. Внешний код общается с ним только
/// через неизменяемые снимки (<see cref="SimulationSettings"/>, <see cref="SceneSnapshot"/>)
/// и очередь команд.
/// </summary>
public sealed class OverlayHost : IDisposable
{
    private const string ThreadName = "WinterDesktop.Effects";
    private const int MaxRestartAttempts = 3;

    /// <summary>Насколько далеко от сугроба инструмент ещё считается наведённым (DIP).</summary>
    private const float MouseCaptureToleranceDip = 72f;

    private readonly IAppLog _log;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile OverlayStats _stats = new(0, 0, 0f, 0d, 0d, false, false, 0, 0, 0, 0, 0);
    private bool _disposed;

    public OverlayHost(IAppLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public OverlayStats Stats => _stats;

    /// <summary>
    /// Снеговики изменились. Событие приходит на потоке эффектов и передаёт готовый
    /// снимок для сохранения; подписчик обязан переключиться на UI-поток.
    /// </summary>
    public event Action<IReadOnlyList<SnowmanRecord>>? SnowmenChanged;

    public bool IsRunning => _thread is { IsAlive: true };

    public void Start()
    {
        if (_thread is not null) return;

        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = ThreadName,
        };

        _thread.Start();

        if (!_ready.Wait(5000))
        {
            _log.Warn("Поток эффектов не сообщил о готовности за 5 секунд.");
        }
    }

    /// <summary>Обновляет настройки и бюджет производительности. Потокобезопасно.</summary>
    public void UpdateSettings(SimulationSettings settings, float[]? monitorScales, int targetFps)
    {
        Enqueue(() => ApplySettings(settings, monitorScales, targetFps));
    }

    /// <summary>Новый снимок сцены (мониторы и окна). Потокобезопасно.</summary>
    public void UpdateScene(SceneSnapshot scene)
    {
        Enqueue(() => ApplyScene(scene));
    }

    public void SetInteractionMode(bool enabled)
    {
        Enqueue(() => ApplyInteractionMode(enabled));
    }

    /// <summary>Полная пауза: скрыть overlay и остановить симуляцию (полноэкранные приложения, сон).</summary>
    public void SetSuspended(bool suspended)
    {
        Enqueue(() => ApplySuspended(suspended));
    }

    public void ClearAll()
    {
        Enqueue(() => _simulation?.ClearAllSnow());
    }

    public void ClearMonitor(string deviceName)
    {
        Enqueue(() =>
        {
            var scene = _scene;
            for (int i = 0; i < scene.Monitors.Count; i++)
            {
                if (!string.Equals(scene.Monitors[i].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                _simulation?.ClearMonitor(scene.Monitors[i]);
                return;
            }
        });
    }

    public void ClearWindowUnderCursor()
    {
        Enqueue(() =>
        {
            if (_simulation is null) return;
            if (!NativeMethods.GetCursorPos(out var position))
            {
                _log.Info("Очистка под курсором: позиция курсора недоступна.");
                return;
            }

            var ledge = _simulation.FindLedgeAt(position.X, position.Y);
            if (ledge is null)
            {
                _log.Info($"Очистка под курсором: под точкой {position.X},{position.Y} нет кромки со снегом.");
                return;
            }

            if (ledge.IsDesktopLedge)
            {
                var monitor = _scene.MonitorAt(position.X, position.Y);
                if (monitor is not null)
                {
                    _log.Info($"Очистка под курсором: кромка рабочего стола, счищено " +
                              $"{_simulation.ClearMonitor(monitor)} на {monitor.DeviceName}.");
                }
            }
            else if (ledge.Owner != IntPtr.Zero)
            {
                _log.Info($"Очистка под курсором: кромка окна 0x{ledge.Owner:X}, " +
                          $"счищено {_simulation.ClearWindow(ledge.Owner)}.");
            }
        });
    }

    public void Stop()
    {
        if (_thread is null) return;

        _stopRequested = true;
        _wake.Set();
        _thread.Join(3000);
        _thread = null;
    }

    private void Enqueue(Action action)
    {
        if (_stopRequested || _disposed) return;
        _commands.Enqueue(action);
        _wake.Set();
    }

    // ------------------------------------------------------------------
    // Поток эффектов
    // ------------------------------------------------------------------

    private GraphicsDevice? _graphics;
    private SnowRenderer? _renderer;
    private SnowSimulation? _simulation;
    private readonly List<OverlayWindow> _windows = new();
    private readonly InputState _input = new();

    private SceneSnapshot _scene = SceneSnapshot.Empty;
    private SimulationSettings _settings = SimulationSettings.Default;
    private float[]? _monitorScales;
    private int _targetFps = 60;
    private bool _interactive;
    private bool _suspended;
    private bool _warmed;
    private bool _sceneApplied;

    private void ThreadProc()
    {
        double fpsAccumulator = 0d;
        int fpsFrames = 0;
        double fps = 0d;
        double frameMilliseconds = 0d;

        try
        {
            _graphics = GraphicsDevice.Create(_log);
            _renderer = new SnowRenderer(_graphics);
            _simulation = new SnowSimulation();
            _simulation.SnowmenChanged += records => SnowmenChanged?.Invoke(records);
            _simulation.UpdateScene(_scene, _monitorScales, _settings);
            _sceneApplied = true;

            _log.Info("Поток эффектов готов.");
            _ready.Set();
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось инициализировать поток эффектов.", ex);
            _ready.Set();
            Cleanup();
            return;
        }

        var clock = Stopwatch.StartNew();
        double previous = 0d;
        int consecutiveFailures = 0;

        while (!_stopRequested)
        {
            PumpMessages();
            ExecuteCommands();

            double now = clock.Elapsed.TotalSeconds;
            double delta = now - previous;
            previous = now;
            if (delta > 0.5) delta = 0.016;     // выход из сна / подвисание системы
            if (delta < 0d) delta = 0d;

            if (_suspended)
            {
                _wake.WaitOne(40);
                continue;
            }

            var frameStart = Stopwatch.GetTimestamp();

            try
            {
                var cursor = _input.Poll();
                var snowInput = new SnowInput(cursor.X, cursor.Y, cursor.IsValid, cursor.PrimaryDown, cursor.PrimaryPressed);

                _simulation!.Update(delta, snowInput, _settings);
                UpdateMouseCapture(cursor);

                bool deviceLost = false;
                for (int i = 0; i < _windows.Count; i++)
                {
                    var window = _windows[i];
                    if (!_renderer!.Render(window, _simulation, _settings))
                    {
                        if (window.NeedsRecreate) deviceLost = true;
                    }
                }

                if (deviceLost)
                {
                    _log.Warn("Устройство потеряно: пересоздаю overlay-окна.");
                    RecreateAllWindows();
                }

                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                _log.Error($"Ошибка кадра ({consecutiveFailures}).", ex);

                if (consecutiveFailures >= MaxRestartAttempts)
                {
                    _log.Warn("Пересоздаю графические ресурсы после серии ошибок.");
                    RecreateGraphics();
                    consecutiveFailures = 0;
                }
                else
                {
                    Thread.Sleep(100);
                }
            }

            double frameMs = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
            frameMilliseconds = frameMilliseconds <= 0d ? frameMs : (frameMilliseconds * 0.9d) + (frameMs * 0.1d);

            fpsAccumulator += delta;
            fpsFrames++;
            if (fpsAccumulator >= 0.5d)
            {
                fps = fpsFrames / fpsAccumulator;
                fpsAccumulator = 0d;
                fpsFrames = 0;

                _stats = new OverlayStats(
                    _simulation?.ParticleCount ?? 0,
                    _simulation?.LedgeCount ?? 0,
                    _simulation?.TotalSnowMass ?? 0f,
                    fps,
                    frameMilliseconds,
                    _graphics?.IsWarp ?? false,
                    _suspended,
                    _windows.Count,
                    _simulation?.SnowmanCount ?? 0,
                    _simulation?.SnowballCount ?? 0,
                    _simulation?.GarlandCount ?? 0,
                    _simulation?.GarlandBulbCount ?? 0);
            }

            // Ограничение частоты кадров. Present(1) уже синхронизирован с вертикальной
            // синхронизацией, поэтому здесь только «досыпаем» до целевого интервала.
            double targetSeconds = 1d / Math.Clamp(_targetFps, 10, 240);
            double remaining = targetSeconds - Stopwatch.GetElapsedTime(frameStart).TotalSeconds;
            if (remaining > 0.001d)
            {
                _wake.WaitOne(TimeSpan.FromSeconds(Math.Min(remaining, 0.05d)));
            }
        }

        _log.Info("Поток эффектов завершается.");
        Cleanup();
    }

    private void PumpMessages()
    {
        while (NativeMethods.PeekMessageW(out var message, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
        {
            if (message.message == NativeMethods.WM_QUIT) return;
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
    }

    private void ExecuteCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            try
            {
                command();
            }
            catch (Exception ex)
            {
                _log.Error("Команда потока эффектов завершилась ошибкой.", ex);
            }
        }
    }

    // ------------------------------------------------------------------
    // Применение состояния (только на потоке эффектов)
    // ------------------------------------------------------------------

    private void ApplySettings(SimulationSettings settings, float[]? monitorScales, int targetFps)
    {
        _settings = settings with { InteractionMode = _interactive };
        _monitorScales = monitorScales;
        _targetFps = Math.Clamp(targetFps, 10, 240);

        if (_simulation is not null && _sceneApplied)
        {
            _simulation.UpdateScene(_scene, _monitorScales, _settings);
            EnsureWindows();
        }
    }

    private void ApplyScene(SceneSnapshot scene)
    {
        _scene = scene;
        _sceneApplied = true;

        if (_simulation is null) return;

        _simulation.UpdateScene(scene, _monitorScales, _settings);

        if (!_warmed && scene.Monitors.Count > 0)
        {
            _simulation.WarmStart(_settings);
            _warmed = true;
        }

        EnsureWindows();
    }

    private void ApplyInteractionMode(bool enabled)
    {
        if (_interactive == enabled)
        {
            _settings = _settings with { InteractionMode = enabled };
            return;
        }

        _interactive = enabled;
        _settings = _settings with { InteractionMode = enabled };

        // Перехват мыши включается только у снега и только для инструментов, которым
        // нужен клик (см. UpdateMouseCapture). Вне этих зон экран остаётся кликабельным.
        if (!enabled)
        {
            for (int i = 0; i < _windows.Count; i++)
            {
                _windows[i].SetClickThrough(true);
            }
        }

        _log.Info(enabled ? "Режим взаимодействия включён." : "Режим взаимодействия выключен.");
    }

    /// <summary>
    /// Перехватывает мышь только там, где инструмент действительно работает: рядом
    /// с сугробом/снеговиком для снежка и снеговика. Варежка и лопата работают по
    /// глобальному опросу курсора, поэтому клики им не нужны и весь экран остаётся
    /// кликабельным. Пока в руке снежок, перехват держится, чтобы бросок не уходил в окно.
    /// </summary>
    private void UpdateMouseCapture(CursorState cursor)
    {
        bool capture = false;

        if (_interactive && cursor.IsValid && _simulation is not null)
        {
            switch (_settings.ActiveTool)
            {
                case BrushTool.Snowball:
                case BrushTool.Snowman:
                    capture = _simulation.IsInteractiveAt(cursor.X, cursor.Y, MouseCaptureToleranceDip);
                    break;
            }
        }

        for (int i = 0; i < _windows.Count; i++)
        {
            var window = _windows[i];
            bool onCursorMonitor = capture && window.Monitor.Bounds.Contains((int)cursor.X, (int)cursor.Y);
            window.SetClickThrough(!onCursorMonitor);
        }
    }

    private void ApplySuspended(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;

        for (int i = 0; i < _windows.Count; i++)
        {
            if (suspended) _windows[i].Hide();
            else _windows[i].Show();
        }

        _log.Info(suspended ? "Эффекты приостановлены." : "Эффекты возобновлены.");
    }

    private void EnsureWindows()
    {
        var wanted = new List<MonitorDescriptor>(_scene.Monitors.Count);
        for (int i = 0; i < _scene.Monitors.Count; i++)
        {
            if (ScaleFor(i) <= 0.001f) continue;
            wanted.Add(_scene.Monitors[i]);
        }

        if (wanted.Count == _windows.Count)
        {
            bool same = true;
            for (int i = 0; i < wanted.Count; i++)
            {
                if (!IsSameMonitor(wanted[i], _windows[i].Monitor))
                {
                    same = false;
                    break;
                }
            }

            if (same) return;
        }

        DisposeWindows();

        foreach (var monitor in wanted)
        {
            CreateWindow(monitor);
        }
    }

    private void CreateWindow(MonitorDescriptor monitor)
    {
        if (_graphics is null) return;

        try
        {
            var window = new OverlayWindow(_graphics, monitor, _log);
            window.SetClickThrough(!_interactive);
            _windows.Add(window);
            if (!_suspended) window.Show();
        }
        catch (Exception ex)
        {
            _log.Error($"Не удалось создать overlay для {monitor.DeviceName}.", ex);
        }
    }

    private void DisposeWindows()
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            _windows[i].Dispose();
        }

        _windows.Clear();
    }

    private void RecreateAllWindows()
    {
        DisposeWindows();
        EnsureWindows();
    }

    private void RecreateGraphics()
    {
        DisposeWindows();

        _renderer?.Dispose();
        _renderer = null;

        _graphics?.Dispose();
        _graphics = null;

        // Перед пересозданием даём DirectComposition время освободить цели.
        Thread.Sleep(250);

        try
        {
            _graphics = GraphicsDevice.Create(_log);
            _renderer = new SnowRenderer(_graphics);
            EnsureWindows();
        }
        catch (Exception ex)
        {
            _log.Error("Пересоздание графических ресурсов не удалось.", ex);
        }
    }

    private float ScaleFor(int monitorIndex)
    {
        if (_monitorScales is null) return 1f;
        if (monitorIndex < 0 || monitorIndex >= _monitorScales.Length) return 1f;
        return _monitorScales[monitorIndex];
    }

    private static bool IsSameMonitor(MonitorDescriptor a, MonitorDescriptor b) =>
        string.Equals(a.DeviceName, b.DeviceName, StringComparison.OrdinalIgnoreCase)
        && a.Bounds == b.Bounds
        && a.Dpi == b.Dpi;

    private void Cleanup()
    {
        DisposeWindows();

        _renderer?.Dispose();
        _renderer = null;

        _graphics?.Dispose();
        _graphics = null;

        _ready.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _wake.Dispose();
        _ready.Dispose();
    }
}
