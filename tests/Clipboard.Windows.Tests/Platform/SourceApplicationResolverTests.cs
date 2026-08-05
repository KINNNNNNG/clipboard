using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class SourceApplicationResolverTests
{
    [Fact]
    public void Format_display_name_prefers_file_description_then_product_name()
    {
        Assert.Equal(
            "Visual Studio Code",
            SourceApplicationResolver.FormatDisplayName(
                "code.exe",
                "  Visual Studio Code  ",
                "Microsoft Visual Studio Code"));
        Assert.Equal(
            "Microsoft Visual Studio Code",
            SourceApplicationResolver.FormatDisplayName(
                "code.exe",
                null,
                " Microsoft Visual Studio Code "));
    }

    [Theory]
    [InlineData("explorer.exe", "文件资源管理器")]
    [InlineData("notepad.exe", "记事本")]
    [InlineData("mspaint.exe", "画图")]
    [InlineData("custom-tool.exe", "custom-tool")]
    public void Format_display_name_uses_system_mapping_or_identifier_fallback(
        string identifier,
        string expected)
    {
        Assert.Equal(
            expected,
            SourceApplicationResolver.FormatDisplayName(identifier, null, null));
    }

    [Fact]
    public void Format_display_name_trims_and_limits_untrusted_version_text()
    {
        string result = SourceApplicationResolver.FormatDisplayName(
            "tool.exe",
            $"  {new string('a', 200)}  ",
            null);

        Assert.Equal(128, result.Length);
        Assert.Equal(
            "Tool Product",
            SourceApplicationResolver.FormatDisplayName(
                "tool.exe",
                "C:\\Program Files\\Tool\\tool.exe",
                "Tool Product"));
    }
}
