namespace WinterDesktop.Core.Diagnostics;

/// <summary>
/// Минималистичный контракт логирования. Реализация пишет в локальный файл и в Debug.
/// Никакой телеметрии и сетевых отправок (§7).
/// </summary>
public interface IAppLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
