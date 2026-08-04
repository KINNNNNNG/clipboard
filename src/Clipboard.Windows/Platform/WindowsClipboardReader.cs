using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
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
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await content.GetStorageItemsAsync()
                    .AsTask(cancellationToken);
                var files = new List<ClipboardFileEntry>(items.Count);
                foreach (IStorageItem item in items)
                {
                    if (!TryReadFileEntry(item, out ClipboardFileEntry entry))
                    {
                        return ClipboardPayload.Empty;
                    }
                    files.Add(entry);
                }
                return ClipboardPayload.Files(files);
            }
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

    private static bool TryReadFileEntry(IStorageItem item, out ClipboardFileEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(item.Path) || !Path.IsPathFullyQualified(item.Path))
        {
            return false;
        }
        try
        {
            System.IO.FileAttributes attributes = File.GetAttributes(item.Path);
            bool isDirectory = attributes.HasFlag(System.IO.FileAttributes.Directory);
            if (isDirectory)
            {
                var directory = new DirectoryInfo(item.Path);
                entry = new ClipboardFileEntry(
                    item.Path,
                    true,
                    0,
                    new DateTimeOffset(directory.LastWriteTimeUtc).ToUnixTimeMilliseconds());
            }
            else
            {
                var file = new FileInfo(item.Path);
                entry = new ClipboardFileEntry(
                    item.Path,
                    false,
                    checked((ulong)file.Length),
                    new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds());
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
