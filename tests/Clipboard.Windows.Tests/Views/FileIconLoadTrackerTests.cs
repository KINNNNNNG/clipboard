using Clipboard.Windows.Views;
using Xunit;

namespace Clipboard.Windows.Tests.Views;

public sealed class FileIconLoadTrackerTests
{
    [Fact]
    public void Recycled_card_replaces_pending_request_even_when_cache_key_is_same()
    {
        var tracker = new FileIconLoadTracker();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        Assert.True(tracker.Begin(first, "file:.docx"));
        Assert.False(tracker.Begin(first, "file:.docx"));
        Assert.True(tracker.Begin(second, "file:.docx"));
        Assert.False(tracker.IsCurrent(first, "file:.docx"));
        Assert.True(tracker.IsCurrent(second, "file:.docx"));
    }
}
