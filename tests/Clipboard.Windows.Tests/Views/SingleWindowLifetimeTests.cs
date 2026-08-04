using Clipboard.Windows.Views;
using Xunit;

namespace Clipboard.Windows.Tests.Views;

public sealed class SingleWindowLifetimeTests
{
    [Fact]
    public void Repeated_open_reuses_the_current_window_until_it_is_released()
    {
        var lifetime = new SingleWindowLifetime<object>();
        int created = 0;

        object first = lifetime.GetOrCreate(() =>
        {
            created++;
            return new object();
        }, out bool firstCreated);
        object second = lifetime.GetOrCreate(() =>
        {
            created++;
            return new object();
        }, out bool secondCreated);

        Assert.Same(first, second);
        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.Equal(1, created);

        lifetime.Release(first);
        object third = lifetime.GetOrCreate(() => new object(), out bool thirdCreated);

        Assert.NotSame(first, third);
        Assert.True(thirdCreated);
    }

    [Fact]
    public void Releasing_a_stale_window_does_not_clear_the_current_window()
    {
        var lifetime = new SingleWindowLifetime<object>();
        object current = lifetime.GetOrCreate(() => new object(), out _);

        lifetime.Release(new object());

        Assert.Same(current, lifetime.Current);
    }
}
