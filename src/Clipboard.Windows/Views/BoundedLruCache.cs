namespace Clipboard.Windows.Views;

internal sealed class BoundedLruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recency = [];

    public BoundedLruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    public bool TryGetValue(TKey key, out TValue? value)
    {
        if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            value = default;
            return false;
        }

        _recency.Remove(node);
        _recency.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    public void Set(TKey key, TValue value)
    {
        if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
        {
            existing.Value = new Entry(key, value);
            _recency.Remove(existing);
            _recency.AddFirst(existing);
            return;
        }

        var node = new LinkedListNode<Entry>(new Entry(key, value));
        _recency.AddFirst(node);
        _entries.Add(key, node);

        if (_entries.Count <= _capacity)
        {
            return;
        }

        LinkedListNode<Entry> oldest = _recency.Last!;
        _recency.RemoveLast();
        _entries.Remove(oldest.Value.Key);
    }

    private sealed record Entry(TKey Key, TValue Value);
}
