using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class ClipboardSuppressionTests
{
    [Fact]
    public void Matching_token_is_consumed_once_without_cross_kind_suppression()
    {
        var clock = new ManualTimeProvider();
        var suppression = new ClipboardSuppression(clock);
        byte[] image = [1, 2, 3, 4];

        suppression.RegisterText("same bytes");
        suppression.RegisterImage(image);

        Assert.False(suppression.TryConsumeText("different"));
        Assert.True(suppression.TryConsumeText("same bytes"));
        Assert.False(suppression.TryConsumeText("same bytes"));
        Assert.True(suppression.TryConsumeImage(image));
        Assert.False(suppression.TryConsumeImage(image));
    }

    [Fact]
    public void Token_expires_after_five_seconds()
    {
        var clock = new ManualTimeProvider();
        var suppression = new ClipboardSuppression(clock);
        suppression.RegisterText("expires");

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.False(suppression.TryConsumeText("expires"));
    }

    [Fact]
    public void File_bundle_token_is_case_insensitive_and_order_sensitive()
    {
        var suppression = new ClipboardSuppression();
        suppression.RegisterFileBundle(["C:\\Docs\\a.txt", "C:\\Docs\\b.txt"]);

        Assert.False(suppression.TryConsumeFileBundle(["C:\\docs\\b.txt", "C:\\docs\\a.txt"]));
        Assert.True(suppression.TryConsumeFileBundle(["c:\\docs\\A.txt", "c:\\docs\\B.txt"]));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
