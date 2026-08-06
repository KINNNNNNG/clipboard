using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class FileTypeIconProviderTests
{
    [Fact]
    public void Extension_uses_virtual_placeholder_and_normal_file_attributes()
    {
        var native = new FakeShellIconNativeApi { Icon = (nint)42 };
        var provider = new ShellFileTypeIconProvider(native);

        ShellIconPixels? result = provider.Load("file:.docx");

        Assert.NotNull(result);
        Assert.Equal("placeholder.docx", native.Path);
        Assert.Equal(ShellFileTypeIconProvider.FileAttributeNormal, native.Attributes);
        Assert.Equal((nint)42, Assert.Single(native.DestroyedIcons));
    }

    [Fact]
    public void Directory_uses_virtual_directory_attributes()
    {
        var native = new FakeShellIconNativeApi { Icon = (nint)43 };

        new ShellFileTypeIconProvider(native).Load("directory");

        Assert.Equal("placeholder", native.Path);
        Assert.Equal(ShellFileTypeIconProvider.FileAttributeDirectory, native.Attributes);
        Assert.Equal((nint)43, Assert.Single(native.DestroyedIcons));
    }

    [Fact]
    public void Native_failure_and_conversion_exception_return_no_icon_and_release_handle()
    {
        var native = new FakeShellIconNativeApi
        {
            Icon = (nint)44,
            ThrowOnCopy = true,
        };

        Assert.Null(new ShellFileTypeIconProvider(native).Load("file:.pdf"));
        Assert.Equal((nint)44, Assert.Single(native.DestroyedIcons));
    }

    [Fact]
    public void Empty_icon_returns_no_result_without_destroying_a_handle()
    {
        var native = new FakeShellIconNativeApi();

        Assert.Null(new ShellFileTypeIconProvider(native).Load("file"));
        Assert.Empty(native.DestroyedIcons);
    }

    [Fact]
    public void Production_native_api_returns_a_32_pixel_icon_or_a_safe_null_result()
    {
        ShellIconPixels? result = new ShellFileTypeIconProvider(
            new ShellIconNativeApi()).Load("file:.txt");

        Assert.True(result is null || (result.Width == 32
            && result.Height == 32
            && result.BgraPixels.Length == 32 * 32 * 4));
    }

    private sealed class FakeShellIconNativeApi : IShellIconNativeApi
    {
        public nint Icon { get; init; }

        public bool ThrowOnCopy { get; init; }

        public string? Path { get; private set; }

        public uint Attributes { get; private set; }

        public List<nint> DestroyedIcons { get; } = [];

        public nint GetFileIcon(string virtualName, uint attributes)
        {
            Path = virtualName;
            Attributes = attributes;
            return Icon;
        }

        public ShellIconPixels CopyIconPixels(nint icon, int size)
        {
            if (ThrowOnCopy)
            {
                throw new InvalidOperationException("conversion failed");
            }

            return new ShellIconPixels(size, size, new byte[size * size * 4]);
        }

        public void DestroyIcon(nint icon) => DestroyedIcons.Add(icon);
    }
}
