namespace Clipboard.Windows.Platform;

internal interface IClipboardWriter
{
    Task WriteTextAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task WriteImageAsync(
        ReadOnlyMemory<byte> png,
        CancellationToken cancellationToken = default);
}

internal interface IClipboardItemContentReader
{
    Task<byte[]> ReadImageAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);
}
