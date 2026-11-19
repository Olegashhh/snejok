using WinterDesktop.Core.Diagnostics;

namespace WinterDesktop.Settings.Tests;

internal sealed class TestLog : IAppLog
{
    public List<string> Messages { get; } = new();

    public void Info(string message) => Messages.Add("INFO " + message);

    public void Warn(string message) => Messages.Add("WARN " + message);

    public void Error(string message, Exception? exception = null) =>
        Messages.Add("ERROR " + message + (exception is null ? "" : " | " + exception.Message));
}

/// <summary>Временный каталог для тестов: удаляется в Dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wd-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Не влияет на результат теста.
        }
    }
}
