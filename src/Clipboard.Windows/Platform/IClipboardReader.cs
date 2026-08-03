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
}

internal sealed record ClipboardPayload(
    ClipboardPayloadKind Kind,
    string? TextContent,
    ReadOnlyMemory<byte> Png,
    uint Width,
    uint Height)
{
    public static ClipboardPayload Empty { get; } =
        new(ClipboardPayloadKind.None, null, ReadOnlyMemory<byte>.Empty, 0, 0);

    public static ClipboardPayload Text(string text) =>
        new(ClipboardPayloadKind.Text, text, ReadOnlyMemory<byte>.Empty, 0, 0);

    public static ClipboardPayload Image(ReadOnlyMemory<byte> png, uint width, uint height) =>
        new(ClipboardPayloadKind.Image, null, png, width, height);
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
