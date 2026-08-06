namespace Clipboard.Windows.Views;

internal sealed class FileIconCache<T> where T : class
{
    private readonly BoundedLruCache<string, Entry> _entries;
    private readonly Dictionary<string, Task<T?>> _inFlight = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _failureTtl;

    public FileIconCache(int capacity, TimeProvider timeProvider, TimeSpan failureTtl)
    {
        _entries = new BoundedLruCache<string, Entry>(capacity);
        _timeProvider = timeProvider;
        _failureTtl = failureTtl;
    }

    public Task<T?> GetAsync(string key, Func<Task<T?>> loader)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_entries.TryGetValue(key, out Entry? cached)
            && cached is not null
            && (cached.Value is not null || now < cached.RetryAfter))
        {
            return Task.FromResult(cached.Value);
        }

        if (_inFlight.TryGetValue(key, out Task<T?>? pending))
        {
            return pending;
        }

        var completion = new TaskCompletionSource<T?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlight.Add(key, completion.Task);
        _ = PopulateAsync(key, loader, completion);
        return completion.Task;
    }

    private async Task PopulateAsync(
        string key,
        Func<Task<T?>> loader,
        TaskCompletionSource<T?> completion)
    {
        T? value;
        try
        {
            value = await loader();
        }
        catch
        {
            value = null;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        _entries.Set(key, new Entry(
            value,
            value is null ? now.Add(_failureTtl) : DateTimeOffset.MaxValue));
        _inFlight.Remove(key);
        completion.TrySetResult(value);
    }

    private sealed record Entry(T? Value, DateTimeOffset RetryAfter);
}
