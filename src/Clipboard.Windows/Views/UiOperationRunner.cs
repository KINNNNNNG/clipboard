namespace Clipboard.Windows.Views;

internal sealed class UiOperationRunner
{
    private readonly Action<string> _reportError;
    private readonly Action? _clearError;

    public UiOperationRunner(Action<string> reportError, Action? clearError = null)
    {
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _clearError = clearError;
    }

    public async Task<bool> RunAsync(Func<Task> operation, string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        _clearError?.Invoke();

        try
        {
            await operation();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            _reportError(failureMessage);
            return false;
        }
    }
}
