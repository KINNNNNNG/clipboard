using System.Runtime.InteropServices.WindowsRuntime;
using Clipboard.Windows.Platform;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Clipboard.Windows.Views;

internal static class SoftwareBitmapSourceFactory
{
    public static async Task<SoftwareBitmapSource> CreateAsync(ShellIconPixels pixels)
    {
        int expectedLength = checked(pixels.Width * pixels.Height * 4);
        if (pixels.Width <= 0 || pixels.Height <= 0 || pixels.BgraPixels.Length != expectedLength)
        {
            throw new ArgumentException("Shell icon pixels are not a complete BGRA bitmap.", nameof(pixels));
        }

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.BgraPixels.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            pixels.Width,
            pixels.Height,
            BitmapAlphaMode.Premultiplied);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        return source;
    }
}
