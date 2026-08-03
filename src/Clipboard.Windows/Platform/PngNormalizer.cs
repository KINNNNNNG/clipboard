using Clipboard.Windows.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Clipboard.Windows.Platform;

internal sealed record NormalizedPng(byte[] Bytes, uint Width, uint Height);

internal sealed class PngNormalizer
{
    public async Task<NormalizedPng> NormalizeAsync(
        IRandomAccessStream input,
        CancellationToken cancellationToken)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken);
        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output)
            .AsTask(cancellationToken);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken);
        if (output.Size == 0 || output.Size > (ulong)ClipboardCoreLimits.MaxImageBytes)
        {
            throw new InvalidDataException("Normalized PNG size is outside the supported range.");
        }

        output.Seek(0);
        byte[] png = new byte[checked((int)output.Size)];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync(checked((uint)output.Size)).AsTask(cancellationToken);
        reader.ReadBytes(png);
        return new NormalizedPng(
            png,
            checked((uint)bitmap.PixelWidth),
            checked((uint)bitmap.PixelHeight));
    }
}
