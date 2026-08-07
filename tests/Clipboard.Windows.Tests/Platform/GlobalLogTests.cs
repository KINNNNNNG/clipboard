using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class GlobalLogTests
{
    [Fact]
    public async Task File_log_filters_by_level_and_drops_sensitive_fields()
    {
        using var directory = new TemporaryDirectory();
        await using var log = new FileGlobalLog(directory.Path);
        log.ApplySettings(new LoggingSettings("info", 7, 200 * 1024 * 1024));

        log.Write(LogLevel.Debug, "sync", "sync.probe.start", new Dictionary<string, string>
        {
            ["provider"] = "oss",
        });
        log.Write(LogLevel.Info, "sync", "sync.probe.end", new Dictionary<string, string>
        {
            ["provider"] = "oss",
            ["status"] = "authentication",
            ["authorization"] = "must-not-be-written",
            ["path"] = "must-not-be-written",
        });
        await log.FlushAsync();

        string text = (await log.ReadSnapshotAsync()).Text;
        Assert.DoesNotContain("sync.probe.start", text, StringComparison.Ordinal);
        Assert.Contains("sync.probe.end", text, StringComparison.Ordinal);
        Assert.Contains("provider=oss", text, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-written", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_log_removes_expired_and_oversized_history()
    {
        using var directory = new TemporaryDirectory();
        string expired = Path.Combine(directory.Path, "clipboard-2026-07-01.log");
        string old = Path.Combine(directory.Path, "clipboard-2026-08-04.log");
        string recent = Path.Combine(directory.Path, "clipboard-2026-08-05.log");
        await File.WriteAllBytesAsync(expired, new byte[32]);
        await File.WriteAllBytesAsync(old, new byte[6 * 1024 * 1024]);
        await File.WriteAllBytesAsync(recent, new byte[6 * 1024 * 1024]);
        File.SetLastWriteTimeUtc(expired, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(old, new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(recent, new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc));

        await using var log = new FileGlobalLog(
            directory.Path,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.Zero)));
        log.ApplySettings(new LoggingSettings("info", 7, 10UL * 1024 * 1024));
        await log.CleanAsync();

        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True((await log.ReadSnapshotAsync()).TotalBytes <= 10L * 1024 * 1024);
    }

    [Fact]
    public void Logging_settings_defaults_and_validates_ranges()
    {
        Assert.Equal("info", ClientSettings.Default.Logging?.Level);
        Assert.Equal(7, ClientSettings.Default.Logging?.RetentionDays);
        Assert.Equal(200UL * 1024 * 1024, ClientSettings.Default.Logging?.MaxSizeBytes);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LoggingSettings("verbose", 7, 200UL * 1024 * 1024).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LoggingSettings("info", 0, 200UL * 1024 * 1024).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LoggingSettings("info", 7, 9UL * 1024 * 1024).Validate());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clipboard-log-tests-{Guid.NewGuid():N}");
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
