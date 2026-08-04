using Clipboard.Windows.Views;
using Xunit;

namespace Clipboard.Windows.Tests.Views;

public sealed class BoundedLruCacheTests
{
    [Fact]
    public void Adding_past_capacity_evicts_the_least_recently_used_value()
    {
        var cache = new BoundedLruCache<int, string>(2);
        cache.Set(1, "first");
        cache.Set(2, "second");

        Assert.True(cache.TryGetValue(1, out _));
        cache.Set(3, "third");

        Assert.False(cache.TryGetValue(2, out _));
        Assert.True(cache.TryGetValue(1, out string? first));
        Assert.Equal("first", first);
        Assert.True(cache.TryGetValue(3, out string? third));
        Assert.Equal("third", third);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Updating_an_existing_key_replaces_its_value_and_refreshes_recency()
    {
        var cache = new BoundedLruCache<int, string>(2);
        cache.Set(1, "old");
        cache.Set(2, "second");

        cache.Set(1, "new");
        cache.Set(3, "third");

        Assert.True(cache.TryGetValue(1, out string? value));
        Assert.Equal("new", value);
        Assert.False(cache.TryGetValue(2, out _));
    }
}
