using System.Text;
using WinterDesktop.Core.Diagnostics;
using WinterDesktop.Core.Settings;

namespace WinterDesktop.Settings;

/// <summary>
/// Хранилище настроек: загрузка с восстановлением, debounce-сохранение, атомарная запись,
/// импорт/экспорт. Потокобезопасно: изменения приходят из UI, сохранение — из таймера.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly object _gate = new();
    private readonly IAppLog _log;
    private readonly string _filePath;
    private readonly string _backupPath;
    private readonly TimeSpan _debounce;
    private readonly Timer _saveTimer;
    private IDisposable? _subscription;
    private AppSettings _current;
    private bool _disposed;
    private bool _schemaUpgraded;
    private volatile bool _pending;

    public SettingsStore(IAppLog log, string? filePath = null, TimeSpan? debounce = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _filePath = filePath ?? Core.AppPaths.SettingsFile;
        _backupPath = _filePath + ".bak";
        _debounce = debounce ?? TimeSpan.FromMilliseconds(700);
        _saveTimer = new Timer(OnSaveTimer, null, Timeout.Infinite, Timeout.Infinite);
        _current = new AppSettings();
    }

    /// <summary>Актуальные настройки. Объект неизменяем по ссылке до <see cref="Reset"/> или импорта.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    /// <summary>Загружены ли настройки из файла (false — используются значения по умолчанию).</summary>
    public bool LoadedFromDisk { get; private set; }

    /// <summary>Последняя ошибка загрузки, если файл был повреждён. Для отображения в UI.</summary>
    public string? LastLoadWarning { get; private set; }

    /// <summary>Вызывается после загрузки/замены корневого объекта настроек (reset/import).</summary>
    public event Action<AppSettings>? SettingsReplaced;

    /// <summary>Загружает настройки. Никогда не бросает исключение: при любой проблеме — дефолты.</summary>
    public AppSettings Load()
    {
        AppSettings settings;
        lock (_gate)
        {
            try
            {
                settings = LoadCore();
            }
            catch (Exception ex)
            {
                _log.Error("Не удалось применить настройки, используются значения по умолчанию.", ex);
                LastLoadWarning = ex.Message;
                settings = new AppSettings();
            }

            ReplaceLocked(settings);
        }

        // Первый запуск: сразу материализуем файл, чтобы настройки существовали
        // для бэкапа и экспорта независимо от того, менял ли пользователь что-то.
        // Старую схему перезаписываем сразу после миграции: файл не должен показывать
        // прежние значения (например, толщину сугробов 18 вместо 24).
        if (!LoadedFromDisk || _schemaUpgraded) SaveSoon();

        // Событие поднимаем вне блокировки: подписчик может читать Current и писать в UI.
        SettingsReplaced?.Invoke(settings);
        return settings;
    }

    private AppSettings LoadCore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        if (!File.Exists(_filePath))
        {
            _log.Info($"Файл настроек отсутствует, создаются значения по умолчанию: {_filePath}");
            LoadedFromDisk = false;
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var settings = Core.Settings.SettingsSerializer.Deserialize(json, out bool upgraded);
            _schemaUpgraded = upgraded;
            LoadedFromDisk = true;
            _log.Info("Настройки загружены.");
            return settings;
        }
        catch (Exception ex)
        {
            _log.Warn($"Файл настроек повреждён ({ex.GetType().Name}: {ex.Message}). Пробую резервную копию.");

            // Сохраняем повреждённый файл для диагностики, но не блокируем запуск (§5).
            TryPreserveCorruptFile();

            if (File.Exists(_backupPath))
            {
                try
                {
                    var backupJson = File.ReadAllText(_backupPath, Encoding.UTF8);
                    var restored = Core.Settings.SettingsSerializer.Deserialize(backupJson);
                    LoadedFromDisk = true;
                    LastLoadWarning = "Файл настроек был повреждён; восстановлена резервная копия.";
                    _log.Info("Настройки восстановлены из резервной копии.");
                    return Core.Settings.SettingsSerializer.Normalize(restored);
                }
                catch (Exception backupEx)
                {
                    _log.Warn($"Резервная копия тоже повреждена: {backupEx.Message}");
                }
            }

            LoadedFromDisk = false;
            LastLoadWarning = "Файл настроек был повреждён; применены значения по умолчанию.";
            return new AppSettings();
        }
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            var copy = Path.Combine(
                Path.GetDirectoryName(_filePath)!,
                $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(_filePath, copy, overwrite: true);
            _log.Info($"Копия повреждённого файла сохранена: {copy}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Не удалось сохранить копию повреждённого файла: {ex.Message}");
        }
    }

    /// <summary>Планирует сохранение (debounce). Безопасно вызывать сколько угодно часто.</summary>
    public void SaveSoon()
    {
        if (_disposed) return;
        _pending = true;
        try
        {
            _saveTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Гонка при завершении приложения — сохранение выполнит Flush().
        }
    }

    /// <summary>Немедленная запись. Возвращает false, если записать не удалось (ошибка уже залогирована).</summary>
    public bool SaveNow()
    {
        if (_disposed) return false;
        _pending = false;
        try
        {
            string json;
            lock (_gate) json = Core.Settings.SettingsSerializer.Serialize(_current);
            WriteAtomic(json);
            _log.Info("Настройки сохранены.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось сохранить настройки.", ex);
            return false;
        }
    }

    private void WriteAtomic(string json)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temp = _filePath + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_filePath))
        {
            try
            {
                File.Replace(temp, _filePath, _backupPath, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException ex)
            {
                // Некоторые антивирусы/ФС держат файл открытым — откатываемся на копирование.
                _log.Warn($"File.Replace недоступен ({ex.Message}); использую копирование.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn($"File.Replace запрещён ({ex.Message}); использую копирование.");
            }

            try
            {
                File.Copy(_filePath, _backupPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.Warn($"Не удалось обновить резервную копию: {ex.Message}");
            }

            File.Copy(temp, _filePath, overwrite: true);
            File.Delete(temp);
        }
        else
        {
            File.Move(temp, _filePath);
        }
    }

    /// <summary>Сброс к значениям по умолчанию с немедленной записью.</summary>
    public AppSettings Reset()
    {
        AppSettings settings;
        lock (_gate)
        {
            settings = new AppSettings();
            ReplaceLocked(settings);
        }

        SettingsReplaced?.Invoke(settings);
        SaveNow();
        return Current;
    }

    /// <summary>Заменяет текущие настройки (используется импортом и сбросом).</summary>
    public void Replace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Core.Settings.SettingsSerializer.Normalize(settings);
        lock (_gate) ReplaceLocked(normalized);
        SettingsReplaced?.Invoke(normalized);
    }

    private void ReplaceLocked(AppSettings settings)
    {
        _subscription?.Dispose();
        _current = settings;
        _subscription = Core.Settings.SettingsTree.Subscribe(settings, (_, _) => SaveSoon());
    }

    public void Export(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json;
        lock (_gate) json = Core.Settings.SettingsSerializer.Serialize(_current);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        _log.Info($"Настройки экспортированы: {path}");
    }

    /// <summary>Импорт с полной валидацией. При ошибке текущие настройки не меняются.</summary>
    public AppSettings Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = File.ReadAllText(path, Encoding.UTF8);
        var imported = Core.Settings.SettingsSerializer.Deserialize(json);
        Replace(imported);
        SaveNow();
        _log.Info($"Настройки импортированы: {path}");
        return Current;
    }

    private void OnSaveTimer(object? state)
    {
        if (!_pending || _disposed) return;
        SaveNow();
    }

    /// <summary>Завершает отложенные записи и освобождает таймер. Вызывается при выходе.</summary>
    public void Flush()
    {
        try
        {
            if (_pending) SaveNow();
        }
        finally
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            Flush();
        }
        catch
        {
            // Завершение не должно падать из-за сохранения.
        }

        _disposed = true;
        _subscription?.Dispose();
        _saveTimer.Dispose();
    }
}
