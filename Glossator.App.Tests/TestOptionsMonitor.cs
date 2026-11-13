using Microsoft.Extensions.Options;

namespace Glossator.App.Tests;

internal sealed class TestOptionsMonitor<T>(T initialValue) : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];
    private T _currentValue = initialValue;

    public T CurrentValue => _currentValue;

    public T Get(string? name) => _currentValue;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(() => _listeners.Remove(listener));
    }

    public void Update(T newValue, string? name = null)
    {
        _currentValue = newValue;
        foreach (var listener in _listeners.ToArray())
        {
            listener(newValue, name);
        }
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
