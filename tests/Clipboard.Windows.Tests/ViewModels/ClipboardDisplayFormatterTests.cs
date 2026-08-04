using Clipboard.Windows.Views;
using Xunit;

namespace Clipboard.Windows.Tests.ViewModels;

public sealed class ClipboardDisplayFormatterTests
{
    [Fact]
    public void Preview_is_limited_to_six_lines_without_trailing_ellipsis_line()
    {
        string preview = ClipboardDisplayFormatter.LimitPreview(
            "one\ntwo\nthree\nfour\nfive\nsix\nseven");

        Assert.Equal("one\ntwo\nthree\nfour\nfive\nsix", preview);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void Image_bytes_use_compact_windows_style_units(ulong bytes, string expected)
    {
        Assert.Equal(expected, ClipboardDisplayFormatter.FormatBytes(bytes));
    }

    [Fact]
    public void Timestamp_uses_relative_labels_for_recent_items()
    {
        DateTimeOffset now = new(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal("刚刚", ClipboardDisplayFormatter.FormatTimestamp(
            now.AddSeconds(-5).ToUnixTimeMilliseconds(), now));
        Assert.Equal("12 分钟前", ClipboardDisplayFormatter.FormatTimestamp(
            now.AddMinutes(-12).ToUnixTimeMilliseconds(), now));
        Assert.Equal("昨天", ClipboardDisplayFormatter.FormatTimestamp(
            now.AddDays(-1).ToUnixTimeMilliseconds(), now));
    }
}
