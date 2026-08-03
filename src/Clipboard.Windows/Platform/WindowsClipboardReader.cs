using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using SystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Clipboard.Windows.Platform;

internal sealed class WindowsClipboardReader : IClipboardReader
{
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);
    private readonly PngNormalizer _pngNormalizer;

    public WindowsClipboardReader(PngNormalizer? pngNormalizer = null)
    {
        _pngNormalizer = pngNormalizer ?? new PngNormalizer();
    }

    public async Task<ClipboardPayload> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            DataPackageView content = SystemClipboard.GetContent();
            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await content.GetBitmapAsync().AsTask(cancellationToken);
                using var stream = await reference.OpenReadAsync().AsTask(cancellationToken);
                NormalizedPng image = await _pngNormalizer.NormalizeAsync(stream, cancellationToken);
                return ClipboardPayload.Image(image.Bytes, image.Width, image.Height);
            }
            if (content.Contains(StandardDataFormats.Text))
            {
                string text = await content.GetTextAsync().AsTask(cancellationToken);
                return string.IsNullOrEmpty(text) ? ClipboardPayload.Empty : ClipboardPayload.Text(text);
            }
            return ClipboardPayload.Empty;
        }
        catch (COMException error) when (error.HResult == ClipboardCannotOpen)
        {
            throw new ClipboardBusyException(error);
        }
    }
}
