using System.ComponentModel;
using System.Diagnostics;

namespace Clipboard.Windows.Platform;

internal interface IRunningProcess : IDisposable
{
    int Id { get; }

    string? ExecutablePath { get; }

    bool HasExited { get; }

    bool CloseMainWindow();

    bool WaitForExit(int milliseconds);

    void Kill();
}

internal interface IRunningProcessSource
{
    IEnumerable<IRunningProcess> GetByName(string processName);
}

internal sealed class PreviousInstanceCloser
{
    private const int GracefulCloseTimeoutMilliseconds = 2000;

    private readonly IRunningProcessSource _source;
    private readonly Func<int> _currentProcessId;
    private readonly Func<string?> _currentProcessPath;
    private readonly int _gracefulCloseTimeoutMilliseconds;

    public PreviousInstanceCloser()
        : this(
            new SystemRunningProcessSource(),
            () => Environment.ProcessId,
            () => Environment.ProcessPath)
    {
    }

    internal PreviousInstanceCloser(
        IRunningProcessSource source,
        Func<int> currentProcessId,
        Func<string?> currentProcessPath,
        int gracefulCloseTimeoutMilliseconds = GracefulCloseTimeoutMilliseconds)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _currentProcessId = currentProcessId ?? throw new ArgumentNullException(nameof(currentProcessId));
        _currentProcessPath = currentProcessPath ?? throw new ArgumentNullException(nameof(currentProcessPath));
        if (gracefulCloseTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gracefulCloseTimeoutMilliseconds));
        }

        _gracefulCloseTimeoutMilliseconds = gracefulCloseTimeoutMilliseconds;
    }

    public void Close()
    {
        string? currentPath = NormalizePath(_currentProcessPath());
        if (currentPath is null)
        {
            return;
        }

        string processName = Path.GetFileNameWithoutExtension(currentPath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        int currentProcessId = _currentProcessId();
        try
        {
            foreach (IRunningProcess process in _source.GetByName(processName))
            {
                using (process)
                {
                    CloseIfPrevious(process, currentProcessId, currentPath);
                }
            }
        }
        catch (Exception error) when (IsProcessAccessError(error))
        {
            // A process can exit or become inaccessible while the list is being inspected.
        }
    }

    private void CloseIfPrevious(
        IRunningProcess process,
        int currentProcessId,
        string currentPath)
    {
        try
        {
            if (process.Id == currentProcessId
                || !string.Equals(
                    NormalizePath(process.ExecutablePath),
                    currentPath,
                    StringComparison.OrdinalIgnoreCase)
                || process.HasExited)
            {
                return;
            }

            process.CloseMainWindow();
            if (process.HasExited
                || process.WaitForExit(_gracefulCloseTimeoutMilliseconds)
                || process.HasExited)
            {
                return;
            }

            process.Kill();
        }
        catch (Exception error) when (IsProcessAccessError(error))
        {
            // Startup must continue even when an old process disappears mid-close.
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsProcessAccessError(Exception error) =>
        error is InvalidOperationException
            or Win32Exception
            or UnauthorizedAccessException;

    private sealed class SystemRunningProcessSource : IRunningProcessSource
    {
        public IEnumerable<IRunningProcess> GetByName(string processName)
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                yield return new SystemRunningProcess(process);
            }
        }
    }

    private sealed class SystemRunningProcess(Process process) : IRunningProcess
    {
        public int Id => process.Id;

        public string? ExecutablePath
        {
            get
            {
                try
                {
                    return process.MainModule?.FileName;
                }
                catch (Exception error) when (IsProcessAccessError(error))
                {
                    return null;
                }
            }
        }

        public bool HasExited => process.HasExited;

        public bool CloseMainWindow() => process.CloseMainWindow();

        public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);

        public void Kill() => process.Kill(entireProcessTree: true);

        public void Dispose() => process.Dispose();
    }
}
