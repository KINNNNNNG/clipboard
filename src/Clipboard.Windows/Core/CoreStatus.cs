namespace Clipboard.Windows.Core;

internal enum CoreStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidUtf8 = 2,
    InvalidJson = 3,
    CoreError = 4,
    Panic = 5,
    InvalidRegex = 6,
    StorageLocked = 7,
    VaultKeyMismatch = 8,
    VaultUnreadable = 9,
    VaultCorrupt = 10,
    StorageMigration = 11,
    UpdateCheckFailed = 12,
    UpdateDownloadFailed = 13,
    UpdateChecksumMismatch = 14,
    SnapshotFailed = 15,
    SnapshotRestoreFailed = 16,
}

internal sealed class ClipboardCoreException : Exception
{
    public ClipboardCoreException(CoreStatus status)
        : base($"Clipboard core operation failed with status {status}.")
    {
        Status = status;
    }

    public CoreStatus Status { get; }
}
