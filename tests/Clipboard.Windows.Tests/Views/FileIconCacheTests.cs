using Clipboard.Windows.Views;
using Xunit;

namespace Clipboard.Windows.Tests.Views;

public sealed class FileIconCacheTests
{
    [Fact]
    public async Task Overlapping_requests_for_one_key_share_one_loader()
    {
        var cache = new FileIconCache<string>(128, TimeProvider.System, TimeSpan.FromMinutes(5));
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        Task<string?> Load()
        {
            calls++;
            return completion.Task;
        }

        Task<string?> first = cache.GetAsync("file:.docx", Load);
        Task<string?> second = cache.GetAsync("file:.docx", Load);

        Assert.Same(first, second);
        Assert.Equal(1, calls);
        completion.SetResult("icon");

        Assert.Equal("icon", await first);
    }

    [Fact]
    public async Task Failure_is_cached_for_five_minutes_then_retried()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new FileIconCache<string>(128, time, TimeSpan.FromMinutes(5));
        int calls = 0;

        Task<string?> Load()
        {
            calls++;
            return Task.FromResult<string?>(null);
        }

        Assert.Null(await cache.GetAsync("file:.unknown", Load));
        Assert.Null(await cache.GetAsync("file:.unknown", Load));
        Assert.Equal(1, calls);

        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Null(await cache.GetAsync("file:.unknown", Load));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Loader_exception_becomes_a_cached_fallback_result()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new FileIconCache<string>(128, time, TimeSpan.FromMinutes(5));
        int calls = 0;

        Task<string?> Load()
        {
            calls++;
            throw new InvalidOperationException("Shell failed");
        }

        Assert.Null(await cache.GetAsync("file:.broken", Load));
        Assert.Null(await cache.GetAsync("file:.broken", Load));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Capacity_evicts_the_least_recently_used_key()
    {
        var cache = new FileIconCache<string>(2, TimeProvider.System, TimeSpan.FromMinutes(5));
        int calls = 0;

        Task<string?> Load(string value) => Task.FromResult<string?>($"{value}-{++calls}");

        await cache.GetAsync("file:.one", () => Load("one"));
        await cache.GetAsync("file:.two", () => Load("two"));
        await cache.GetAsync("file:.one", () => Load("one"));
        await cache.GetAsync("file:.three", () => Load("three"));
        await cache.GetAsync("file:.two", () => Load("two"));

        Assert.Equal(4, calls);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}
