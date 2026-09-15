namespace Clipboard.Windows.Platform;

/// <summary>
/// Counts captured items and fires once every <c>threshold</c> writes.
/// </summary>
/// <remarks>
/// The counter resets before the callback runs, so a slow snapshot never blocks the next window.
/// </remarks>
internal sealed class SnapshotWriteCounter : IClipboardCaptureObserver
{
    private readonly int _threshold;
    private readonly Action _onThresholdReached;
    private int _count;

    public SnapshotWriteCounter(int threshold, Action onThresholdReached)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        _threshold = threshold;
        _onThresholdReached = onThresholdReached
            ?? throw new ArgumentNullException(nameof(onThresholdReached));
    }

    public void OnCaptured(CaptureNotification notification)
    {
        if (Interlocked.Increment(ref _count) < _threshold)
        {
            return;
        }
        Interlocked.Exchange(ref _count, 0);
        _onThresholdReached();
    }

    public void OnFailure(CaptureFailure failure)
    {
    }
}
