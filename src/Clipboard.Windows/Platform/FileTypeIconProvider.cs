namespace Clipboard.Windows.Platform;

internal sealed record ShellIconPixels(int Width, int Height, byte[] BgraPixels);

internal interface IShellIconNativeApi
{
    nint GetFileIcon(string virtualName, uint attributes);

    ShellIconPixels CopyIconPixels(nint icon, int size);

    void DestroyIcon(nint icon);
}

internal sealed class ShellFileTypeIconProvider
{
    internal const uint FileAttributeNormal = 0x80;
    internal const uint FileAttributeDirectory = 0x10;

    private readonly IShellIconNativeApi _native;

    public ShellFileTypeIconProvider(IShellIconNativeApi native)
    {
        _native = native;
    }

    public ShellIconPixels? Load(string cacheKey)
    {
        if (!TryCreateRequest(cacheKey, out string virtualName, out uint attributes))
        {
            return null;
        }

        nint icon = nint.Zero;
        try
        {
            icon = _native.GetFileIcon(virtualName, attributes);
            return icon == nint.Zero ? null : _native.CopyIconPixels(icon, 32);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icon != nint.Zero)
            {
                try
                {
                    _native.DestroyIcon(icon);
                }
                catch
                {
                }
            }
        }
    }

    private static bool TryCreateRequest(
        string cacheKey,
        out string virtualName,
        out uint attributes)
    {
        if (string.Equals(cacheKey, "directory", StringComparison.Ordinal))
        {
            virtualName = "placeholder";
            attributes = FileAttributeDirectory;
            return true;
        }

        if (string.Equals(cacheKey, "file", StringComparison.Ordinal))
        {
            virtualName = "placeholder";
            attributes = FileAttributeNormal;
            return true;
        }

        const string filePrefix = "file:.";
        if (cacheKey.StartsWith(filePrefix, StringComparison.Ordinal)
            && cacheKey.Length > filePrefix.Length)
        {
            virtualName = $"placeholder{cacheKey["file:".Length..]}";
            attributes = FileAttributeNormal;
            return true;
        }

        virtualName = string.Empty;
        attributes = 0;
        return false;
    }
}
