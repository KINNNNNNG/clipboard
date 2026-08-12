using Clipboard.Windows.Platform;
using Clipboard.Windows.ViewModels;
using Xunit;

namespace Clipboard.Windows.Tests.ViewModels;

public sealed class LogViewModelTests
{
    [Fact]
    public async Task Viewer_filters_searches_changes_level_and_clears_logs()
    {
        using var directory = new TemporaryDirectory();
        await using var log = new FileGlobalLog(directory.Path);
        log.Write(LogLevel.Info, "sync", "sync.probe.end", new Dictionary<string, string>
        {
            ["status"] = "success",
        });
        log.Write(LogLevel.Warn, "sync", "sync.remote.end", new Dictionary<string, string>
        {
            ["status"] = "failure",
        });
        await log.FlushAsync();
        var viewModel = new LogViewModel(
            log,
            (level, _) =>
            {
                log.ApplySettings(new LoggingSettings(level, 7, 200UL * 1024 * 1024));
                return Task.FromResult(true);
            });

        await viewModel.RefreshAsync();
        viewModel.SearchText = "probe";
        Assert.Single(viewModel.VisibleLines);
        viewModel.MinimumLevel = LogLevel.Warn;
        Assert.Empty(viewModel.VisibleLines);

        bool saved = await viewModel.ChangeLevelAsync("debug");
        Assert.True(saved);
        Assert.Equal(LogLevel.Debug, log.Level);

        await viewModel.ClearAsync();
        Assert.Empty(viewModel.VisibleLines);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clipboard-log-view-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
