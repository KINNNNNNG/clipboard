using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using SystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Clipboard.Windows.Platform;

internal sealed class WindowsClipboardWriter : IClipboardWriter
{
    public Task WriteTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        cancellationToken.ThrowIfCancellationRequested();
        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        package.SetText(text);
        SystemClipboard.SetContent(package);
        SystemClipboard.Flush();
        return Task.CompletedTask;
    }

    public async Task WriteImageAsync(
        ReadOnlyMemory<byte> png,
        CancellationToken cancellationToken = default)
    {
        if (png.IsEmpty)
        {
            throw new InvalidDataException("Clipboard image is empty.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        byte[] copy = png.ToArray();
        try
        {
            using (var dataWriter = new DataWriter(stream))
            {
                dataWriter.WriteBytes(copy);
                await dataWriter.StoreAsync().AsTask(cancellationToken);
                await dataWriter.FlushAsync().AsTask(cancellationToken);
                dataWriter.DetachStream();
            }

            stream.Seek(0);
            _ = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            stream.Seek(0);
            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            SystemClipboard.SetContent(package);
            SystemClipboard.Flush();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }
}
