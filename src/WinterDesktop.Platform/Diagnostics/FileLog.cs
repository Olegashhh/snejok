using System.Text;
using WinterDesktop.Core;
using WinterDesktop.Core.Diagnostics;

namespace WinterDesktop.Platform.Diagnostics;

/// <summary>
/// Файловый лог без внешних зависимостей. Ротация по дням, хранение 7 файлов, ограничение
/// размера. Ничего не отправляется по сети — телеметрии нет (§7).
/// </summary>
public sealed class FileLog : IAppLog
{
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int KeepDays = 7;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _prefix;
    private StreamWriter? _writer;
    private DateTime _currentDate;
    private long _currentBytes;
    private bool _failed;

    public FileLog(string? directory = null, string prefix = "app")
    {
        _directory = directory ?? AppPaths.LogsDirectory;
        _prefix = prefix;
        TryOpen(DateTime.Now);
    }

    /// <summary>Дублирование в Debug-вывод полезно при отладке из IDE.</summary>
    public bool EchoToDebugger { get; set; } = true;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        if (_failed) return;

        lock (_gate)
        {
            try
            {
                var now = DateTime.Now;
                if (_writer is null || now.Date != _currentDate) TryOpen(now);
                if (_writer is null) return;

                var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                _writer.WriteLine(line);
                _writer.Flush();
                _currentBytes += line.Length + Environment.NewLine.Length;

                if (EchoToDebugger) System.Diagnostics.Debug.WriteLine(line);

                if (_currentBytes > MaxFileBytes)
                {
                    // Переоткрываем файл, чтобы не упереться в лимит на диске.
                    _writer.Dispose();
                    _writer = null;
                    TryOpen(now, rotate: true);
                }
            }
            catch
            {
                // Логирование не должно влиять на работу приложения.
                _failed = true;
            }
        }
    }

    private void TryOpen(DateTime date, bool rotate = false)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            _currentDate = date;
            var suffix = rotate ? "-" + date.ToString("HHmmss") : string.Empty;
            var path = Path.Combine(_directory, $"{_prefix}-{date:yyyyMMdd}{suffix}.log");

            _writer?.Dispose();
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _currentBytes = stream.Length;

            CleanupOldFiles();
        }
        catch
        {
            _failed = true;
            _writer = null;
        }
    }

    private void CleanupOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(_directory, $"{_prefix}-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // Удаление старых логов не критично.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
