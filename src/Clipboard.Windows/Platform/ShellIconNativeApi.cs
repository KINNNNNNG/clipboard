using System.Runtime.InteropServices;

namespace Clipboard.Windows.Platform;

internal sealed class ShellIconNativeApi : IShellIconNativeApi
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint DiNormal = 0x3;

    public nint GetFileIcon(string virtualName, uint attributes)
    {
        nint result = SHGetFileInfo(
            virtualName,
            attributes,
            out SHFILEINFO info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            ShgfiIcon | ShgfiLargeIcon | ShgfiUseFileAttributes);
        return result == nint.Zero ? nint.Zero : info.HIcon;
    }

    public unsafe ShellIconPixels CopyIconPixels(nint icon, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        nint screenDc = nint.Zero;
        nint memoryDc = nint.Zero;
        nint bitmap = nint.Zero;
        nint previous = nint.Zero;
        bool bitmapSelected = false;
        try
        {
            screenDc = GetDC(nint.Zero);
            if (screenDc == nint.Zero)
            {
                throw new InvalidOperationException("Unable to acquire a screen device context.");
            }

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == nint.Zero)
            {
                throw new InvalidOperationException("Unable to create a compatible device context.");
            }

            var bitmapInfo = new BITMAPINFO
            {
                Header = new BITMAPINFOHEADER
                {
                    Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    Width = size,
                    Height = -size,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                },
            };
            bitmap = CreateDIBSection(
                screenDc,
                ref bitmapInfo,
                DibRgbColors,
                out nint pixels,
                nint.Zero,
                0);
            if (bitmap == nint.Zero || pixels == nint.Zero)
            {
                throw new InvalidOperationException("Unable to create a DIB section for a shell icon.");
            }

            previous = SelectObject(memoryDc, bitmap);
            if (previous == nint.Zero || previous == new nint(-1))
            {
                throw new InvalidOperationException("Unable to select the DIB section.");
            }
            bitmapSelected = true;

            int byteCount = checked(size * size * 4);
            new Span<byte>((void*)pixels, byteCount).Clear();
            if (!DrawIconEx(memoryDc, 0, 0, icon, size, size, 0, nint.Zero, DiNormal))
            {
                throw new InvalidOperationException("Unable to draw a shell icon.");
            }

            byte[] bgra = new byte[byteCount];
            Marshal.Copy(pixels, bgra, 0, bgra.Length);
            return new ShellIconPixels(size, size, bgra);
        }
        finally
        {
            if (bitmapSelected)
            {
                _ = SelectObject(memoryDc, previous);
            }
            if (bitmap != nint.Zero)
            {
                _ = DeleteObject(bitmap);
            }
            if (memoryDc != nint.Zero)
            {
                _ = DeleteDC(memoryDc);
            }
            if (screenDc != nint.Zero)
            {
                _ = ReleaseDC(nint.Zero, screenDc);
            }
        }
    }

    public void DestroyIcon(nint icon)
    {
        if (icon != nint.Zero)
        {
            _ = DestroyIconNative(icon);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(
        string path,
        uint fileAttributes,
        out SHFILEINFO fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(
        nint deviceContext,
        ref BITMAPINFO bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint graphicsObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint graphicsObject);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(
        nint deviceContext,
        int xLeft,
        int yTop,
        nint icon,
        int width,
        int height,
        uint stepIfAnimated,
        nint brush,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIconNative(nint icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        internal nint HIcon;
        internal int IIcon;
        internal uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string? DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        internal string? TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint ImageSize;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        internal BITMAPINFOHEADER Header;
    }
}
