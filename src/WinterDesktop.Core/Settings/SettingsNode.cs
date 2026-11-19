using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinterDesktop.Core.Settings;

/// <summary>
/// База для узлов дерева настроек. Наследует <see cref="INotifyPropertyChanged"/>,
/// чтобы UI и движки могли реагировать на изменения без ручной подписки на каждое поле.
/// </summary>
public abstract class SettingsNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
