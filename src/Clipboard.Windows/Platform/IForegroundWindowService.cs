namespace Clipboard.Windows.Platform;

internal interface IForegroundWindowService
{
    Task<bool> RestoreAsync(
        nint originalHwnd,
        CancellationToken cancellationToken = default);

    uint SendPasteInput();
}
