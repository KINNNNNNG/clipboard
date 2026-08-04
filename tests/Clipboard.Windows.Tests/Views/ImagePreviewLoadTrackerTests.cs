using Clipboard.Windows.Views;
using Xunit;

namespace Clipboard.Windows.Tests.Views;

public sealed class ImagePreviewLoadTrackerTests
{
    [Fact]
    public void New_item_replaces_the_pending_request_and_ignores_the_old_completion()
    {
        var tracker = new ImagePreviewLoadTracker();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        Assert.True(tracker.Begin(first));
        Assert.False(tracker.Begin(first));
        Assert.True(tracker.Begin(second));
        Assert.False(tracker.IsCurrent(first));
        Assert.True(tracker.IsCurrent(second));
    }
}
