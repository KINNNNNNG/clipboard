namespace Clipboard.Windows.Platform;

internal interface IClipboardReader
{
    Task<ClipboardPayload> ReadAsync(CancellationToken cancellationToken);
}

internal enum ClipboardPayloadKind
{
    None,
    Text,
    Image,
    FileBundle,
}

internal sealed record ClipboardFileEntry(
    string Path,
    bool IsDirectory,
    ulong Size,
    long ModifiedMs);

internal sealed record ClipboardPayload(
    ClipboardPayloadKind Kind,
    string? TextContent,
    ReadOnlyMemory<byte> Png,
    uint Width,
    uint Height,
    IReadOnlyList<ClipboardFileEntry> FileEntries)
{
    public static ClipboardPayload Empty { get; } =
        new(ClipboardPayloadKind.None, null, ReadOnlyMemory<byte>.Empty, 0, 0, []);

    public static ClipboardPayload Text(string text) =>
        new(ClipboardPayloadKind.Text, text, ReadOnlyMemory<byte>.Empty, 0, 0, []);

    public static ClipboardPayload Image(ReadOnlyMemory<byte> png, uint width, uint height) =>
        new(ClipboardPayloadKind.Image, null, png, width, height, []);

    public static ClipboardPayload Files(IReadOnlyList<ClipboardFileEntry> files) =>
        files.Count == 0
            ? Empty
            : new(ClipboardPayloadKind.FileBundle, null, ReadOnlyMemory<byte>.Empty, 0, 0, files);
}

internal sealed class ClipboardBusyException : Exception
{
    public ClipboardBusyException()
        : base("The system clipboard is temporarily unavailable.")
    {
    }

    public ClipboardBusyException(Exception innerException)
        : base("The system clipboard is temporarily unavailable.", innerException)
    {
    }
}
