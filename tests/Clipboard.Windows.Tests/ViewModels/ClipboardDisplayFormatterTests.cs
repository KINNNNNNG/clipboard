using Clipboard.Windows.Core;
using Clipboard.Windows.ViewModels;
using Clipboard.Windows.Views;
using Xunit;

namespace Clipboard.Windows.Tests.ViewModels;

public sealed class ClipboardDisplayFormatterTests
{
    [Theory]
    [InlineData("file", "report.DOCX", "file:.docx")]
    [InlineData("file", "archive.tar.gz", "file:.gz")]
    [InlineData("file", ".gitignore", "file")]
    [InlineData("file", "README", "file")]
    [InlineData("file", "report.", "file")]
    [InlineData("file", "", "file")]
    [InlineData("file", @"C:\\Temp\\manual.PDF", "file:.pdf")]
    [InlineData("directory", "Photos", "directory")]
    public void File_icon_cache_key_normalizes_only_the_file_type(
        string representativeKind,
        string representativeName,
        string expected)
    {
        var item = new ClipboardItemViewModel(new ClipboardItemDto(
            Guid.NewGuid(),
            "file_bundle",
            representativeName,
            "explorer.exe",
            100,
            false,
            null,
            null,
            null,
            null,
            1,
            representativeName,
            representativeKind));

        Assert.Equal(expected, item.FileIconCacheKey);
        Assert.Equal(representativeName, item.FileNameSummary);
    }

    [Fact]
    public void File_icon_cache_key_uses_file_when_representative_name_is_null()
    {
        var item = new ClipboardItemViewModel(new ClipboardItemDto(
            Guid.NewGuid(),
            "file_bundle",
            "file bundle",
            "explorer.exe",
            100,
            false,
            null,
            null,
            null,
            null,
            1,
            null,
            "file"));

        Assert.Equal("file", item.FileIconCacheKey);
    }

    [Fact]
    public void File_bundle_card_uses_fluent_summary_and_friendly_source_name()
    {
        var item = new ClipboardItemViewModel(new ClipboardItemDto(
            Guid.NewGuid(),
            "file_bundle",
            "report.docx",
            "explorer.exe",
            100,
            false,
            null,
            null,
            null,
            "文件资源管理器",
            3,
            "report.docx",
            "file"));

        Assert.True(item.IsFileBundle);
        Assert.Equal("\uE8A5", item.FileIconGlyph);
        Assert.Equal("report.docx", item.FileNameSummary);
        Assert.Equal("共 3 项", item.FileCountLabel);
        Assert.Equal("文件资源管理器", item.DisplaySourceApp);
    }

    [Fact]
    public void Single_folder_uses_folder_glyph_and_legacy_source_name_drops_exe_suffix()
    {
        var item = new ClipboardItemViewModel(new ClipboardItemDto(
            Guid.NewGuid(),
            "file_bundle",
            "Photos",
            "explorer.exe",
            100,
            false,
            null,
            null,
            null,
            null,
            1,
            "Photos",
            "directory"));

        Assert.Equal("\uE8B7", item.FileIconGlyph);
        Assert.Equal(string.Empty, item.FileCountLabel);
        Assert.Equal("explorer", item.DisplaySourceApp);
    }

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
