namespace Clipboard.Windows.Core;

internal enum CoreStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidUtf8 = 2,
    InvalidJson = 3,
    CoreError = 4,
    Panic = 5,
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
