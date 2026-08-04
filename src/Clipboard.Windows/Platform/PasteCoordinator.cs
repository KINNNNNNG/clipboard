using System.Security.Cryptography;
using Clipboard.Windows.Core;

namespace Clipboard.Windows.Platform;

internal enum PasteResultKind
{
    Pasted,
    ManualPasteRequired,
}

internal sealed record PasteResult(PasteResultKind Kind, Guid ItemId);

internal interface IClipboardItemPasteService
{
    Task<PasteResult> PasteAsync(
        ClipboardItemDto item,
        nint originalHwnd,
        CancellationToken cancellationToken = default);
}

internal sealed class PasteCoordinator : IClipboardItemPasteService
{
    private readonly IClipboardItemContentReader _contentReader;
    private readonly IClipboardWriter _clipboardWriter;
    private readonly IForegroundWindowService _foregroundWindow;
    private readonly ClipboardSuppression _suppression;
    private readonly Action _hidePanel;

    public PasteCoordinator(
        IClipboardItemContentReader contentReader,
        IClipboardWriter clipboardWriter,
        IForegroundWindowService foregroundWindow,
        ClipboardSuppression suppression,
        Action hidePanel)
    {
        _contentReader = contentReader;
        _clipboardWriter = clipboardWriter;
        _foregroundWindow = foregroundWindow;
        _suppression = suppression;
        _hidePanel = hidePanel;
    }

    public async Task<PasteResult> PasteAsync(
        ClipboardItemDto item,
        nint originalHwnd,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ClipboardPayloadKind kind = ParseKind(item.Kind);

        switch (kind)
        {
            case ClipboardPayloadKind.Text:
                if (string.IsNullOrEmpty(item.Preview))
                {
                    throw new InvalidOperationException("Clipboard text is empty.");
                }
                _suppression.RegisterText(item.Preview);
                try
                {
                    await _clipboardWriter.WriteTextAsync(item.Preview, cancellationToken);
                }
                catch
                {
                    _suppression.DiscardText(item.Preview);
                    throw;
                }
                break;

            case ClipboardPayloadKind.Image:
                byte[] png = await _contentReader.ReadImageAsync(item.Id, cancellationToken);
                try
                {
                    if (png.Length == 0)
                    {
                        throw new InvalidDataException("Clipboard image is empty.");
                    }
                    _suppression.RegisterImage(png);
                    try
                    {
                        await _clipboardWriter.WriteImageAsync(png, cancellationToken);
                    }
                    catch
                    {
                        _suppression.DiscardImage(png);
                        throw;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(png);
                }
                break;

            case ClipboardPayloadKind.FileBundle:
                FileBundleResponseDto bundle = await _contentReader.ReadFileBundleAsync(
                    item.Id,
                    cancellationToken);
                if (bundle.Entries.Count == 0)
                {
                    throw new InvalidDataException("Clipboard file bundle is empty.");
                }
                string[] paths = bundle.Entries.Select(entry => entry.Path).ToArray();
                _suppression.RegisterFileBundle(paths);
                try
                {
                    await _clipboardWriter.WriteFilesAsync(bundle.Entries, cancellationToken);
                }
                catch
                {
                    _suppression.DiscardFileBundle(paths);
                    throw;
                }
                break;

            default:
                throw new NotSupportedException("This clipboard item type cannot be pasted yet.");
        }

        _hidePanel();
        if (originalHwnd == nint.Zero
            || !await _foregroundWindow.RestoreAsync(originalHwnd, cancellationToken))
        {
            return new PasteResult(PasteResultKind.ManualPasteRequired, item.Id);
        }

        uint sent = _foregroundWindow.SendPasteInput();
        PasteResultKind result = sent == 4
            ? PasteResultKind.Pasted
            : PasteResultKind.ManualPasteRequired;
        return new PasteResult(result, item.Id);
    }

    private static ClipboardPayloadKind ParseKind(string kind) =>
        kind switch
        {
            "text" => ClipboardPayloadKind.Text,
            "image" => ClipboardPayloadKind.Image,
            "file_bundle" => ClipboardPayloadKind.FileBundle,
            _ => throw new NotSupportedException("This clipboard item type cannot be pasted yet."),
        };
}
