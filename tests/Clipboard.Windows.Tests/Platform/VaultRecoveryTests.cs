using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class VaultRecoveryTests
{
    [Fact]
    public void Quarantine_moves_the_database_and_sidecars_and_keeps_other_files()
    {
        using var directory = new TemporaryDirectory();
        byte[] damaged = [1, 2, 3, 4];
        File.WriteAllBytes(Path.Combine(directory.Path, "history.db"), damaged);
        File.WriteAllBytes(Path.Combine(directory.Path, "history.db-wal"), damaged);
        File.WriteAllBytes(Path.Combine(directory.Path, "history.db-shm"), damaged);
        File.WriteAllText(Path.Combine(directory.Path, "history.vault"), "{\"version\":1}");
        Directory.CreateDirectory(Path.Combine(directory.Path, "objects"));
        File.WriteAllBytes(Path.Combine(directory.Path, "objects", "blob"), damaged);

        string? quarantined = VaultRecovery.QuarantineHistory(
            directory.Path,
            new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero));

        Assert.Equal("history.db.corrupt-20260915-093000", quarantined);
        Assert.False(File.Exists(Path.Combine(directory.Path, "history.db")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "history.db-wal")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "history.db-shm")));
        Assert.Equal(
            damaged,
            File.ReadAllBytes(Path.Combine(directory.Path, "history.db.corrupt-20260915-093000")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "history.db.corrupt-20260915-093000-wal")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "history.db.corrupt-20260915-093000-shm")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "history.vault")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "objects", "blob")));
    }

    [Fact]
    public void Quarantine_returns_null_when_no_database_exists()
    {
        using var directory = new TemporaryDirectory();

        string? quarantined = VaultRecovery.QuarantineHistory(
            directory.Path,
            new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero));

        Assert.Null(quarantined);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void Quarantine_uses_a_free_name_when_the_timestamp_collides()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "history.db"), "damaged");
        File.WriteAllText(Path.Combine(directory.Path, "history.db.corrupt-20260915-093000"), "earlier");

        string? quarantined = VaultRecovery.QuarantineHistory(
            directory.Path,
            new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero));

        Assert.Equal("history.db.corrupt-20260915-093000-1", quarantined);
        Assert.Equal(
            "earlier",
            File.ReadAllText(Path.Combine(directory.Path, "history.db.corrupt-20260915-093000")));
        Assert.Equal(
            "damaged",
            File.ReadAllText(Path.Combine(directory.Path, "history.db.corrupt-20260915-093000-1")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"clipboard-vault-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
