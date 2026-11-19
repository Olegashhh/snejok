using System.Reflection;

namespace WinterDesktop.Core.Settings;

/// <summary>
/// Обход дерева настроек: нужен хранилищу (для debounce-сохранения) и сбросу.
/// Рекурсия по свойствам-узлам и по словарям узлов.
/// </summary>
public static class SettingsTree
{
    /// <summary>Перечисляет сам корень и все вложенные узлы.</summary>
    public static IEnumerable<SettingsNode> Enumerate(SettingsNode root)
    {
        var stack = new Stack<SettingsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;

            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0) continue;

                if (typeof(SettingsNode).IsAssignableFrom(property.PropertyType))
                {
                    if (property.GetValue(node) is SettingsNode child) stack.Push(child);
                }
                else if (property.GetValue(node) is System.Collections.IDictionary dictionary)
                {
                    foreach (var value in dictionary.Values)
                    {
                        if (value is SettingsNode child) stack.Push(child);
                    }
                }
            }
        }
    }

    /// <summary>Подписывается на изменения всех узлов; возвращает объект для отписки.</summary>
    public static IDisposable Subscribe(SettingsNode root, Action<SettingsNode, string?> handler)
    {
        var nodes = Enumerate(root).ToList();
        foreach (var node in nodes)
        {
            node.PropertyChanged += OnChanged;
        }
        return new Subscription(nodes, OnChanged);

        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is SettingsNode node) handler(node, e.PropertyName);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly List<SettingsNode> _nodes;
        private readonly System.ComponentModel.PropertyChangedEventHandler _handler;

        public Subscription(List<SettingsNode> nodes, System.ComponentModel.PropertyChangedEventHandler handler)
        {
            _nodes = nodes;
            _handler = handler;
        }

        public void Dispose()
        {
            foreach (var node in _nodes) node.PropertyChanged -= _handler;
            _nodes.Clear();
        }
    }
}
