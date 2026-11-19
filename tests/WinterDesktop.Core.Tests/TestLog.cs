using WinterDesktop.Core.Diagnostics;

namespace WinterDesktop.Core.Tests;

internal sealed class TestLog : IAppLog
{
    public List<string> Messages { get; } = new();
    public void Info(string message) => Messages.Add("INFO " + message);
    public void Warn(string message) => Messages.Add("WARN " + message);
    public void Error(string message, Exception? exception = null) => Messages.Add("ERROR " + message);
}
