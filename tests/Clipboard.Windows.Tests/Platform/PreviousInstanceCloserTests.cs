using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class PreviousInstanceCloserTests
{
    [Fact]
    public void Closes_only_previous_process_with_the_same_executable_path()
    {
        const string executablePath = @"C:\Apps\Clipboard\Clipboard.Windows.exe";
        var previous = new FakeRunningProcess(7, executablePath)
        {
            CloseMainWindowResult = true,
            WaitForExitResult = true,
        };
        var current = new FakeRunningProcess(42, executablePath);
        var otherCopy = new FakeRunningProcess(
            8,
            @"C:\Other\Clipboard\Clipboard.Windows.exe");
        var source = new FakeRunningProcessSource(previous, current, otherCopy);

        var closer = new PreviousInstanceCloser(
            source,
            currentProcessId: () => 42,
            currentProcessPath: () => executablePath);

        closer.Close();

        Assert.Equal("Clipboard.Windows", source.RequestedProcessName);
        Assert.Equal(1, previous.CloseMainWindowCalls);
        Assert.Equal(1, previous.WaitForExitCalls);
        Assert.Equal(0, previous.KillCalls);
        Assert.Equal(0, current.CloseMainWindowCalls);
        Assert.Equal(0, otherCopy.CloseMainWindowCalls);
    }

    [Fact]
    public void Force_kills_previous_process_when_graceful_close_does_not_exit()
    {
        const string executablePath = @"C:\Apps\Clipboard\Clipboard.Windows.exe";
        var previous = new FakeRunningProcess(7, executablePath)
        {
            CloseMainWindowResult = false,
            WaitForExitResult = false,
        };
        var source = new FakeRunningProcessSource(previous);

        var closer = new PreviousInstanceCloser(
            source,
            currentProcessId: () => 42,
            currentProcessPath: () => executablePath);

        closer.Close();

        Assert.Equal(1, previous.CloseMainWindowCalls);
        Assert.Equal(1, previous.WaitForExitCalls);
        Assert.Equal(1, previous.KillCalls);
    }

    private sealed class FakeRunningProcessSource(
        params IRunningProcess[] processes) : IRunningProcessSource
    {
        public string? RequestedProcessName { get; private set; }

        public IEnumerable<IRunningProcess> GetByName(string processName)
        {
            RequestedProcessName = processName;
            return processes;
        }
    }

    private sealed class FakeRunningProcess(int id, string executablePath) : IRunningProcess
    {
        public int Id { get; } = id;

        public string? ExecutablePath { get; } = executablePath;

        public bool HasExited { get; set; }

        public bool CloseMainWindowResult { get; set; }

        public bool WaitForExitResult { get; set; }

        public int CloseMainWindowCalls { get; private set; }

        public int WaitForExitCalls { get; private set; }

        public int KillCalls { get; private set; }

        public bool CloseMainWindow()
        {
            CloseMainWindowCalls++;
            return CloseMainWindowResult;
        }

        public bool WaitForExit(int milliseconds)
        {
            WaitForExitCalls++;
            return WaitForExitResult;
        }

        public void Kill() => KillCalls++;

        public void Dispose()
        {
        }
    }
}
