using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class SnapshotPolicyTests
{
    private static readonly string DataDirectory =
        Path.Combine(Path.GetTempPath(), "clipboard-policy-tests", "data");

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(@"C:\clipboard-snapshots", true)]
    public void Snapshot_directory_accepts_absolute_locations(string? directory, bool expected)
    {
        Assert.Equal(expected, SnapshotPolicy.IsValidDirectory(directory, DataDirectory));
    }

    [Fact]
    public void Snapshot_directory_rejects_relative_or_embedded_locations()
    {
        Assert.False(SnapshotPolicy.IsValidDirectory("relative\\path", DataDirectory));
        Assert.False(SnapshotPolicy.IsValidDirectory(DataDirectory, DataDirectory));
        Assert.False(
            SnapshotPolicy.IsValidDirectory(Path.Combine(DataDirectory, "snapshots"), DataDirectory));
    }

    [Fact]
    public void Snapshot_directory_accepts_a_sibling_of_the_data_directory()
    {
        string sibling = Path.Combine(Path.GetTempPath(), "clipboard-policy-tests", "snapshots");

        Assert.True(SnapshotPolicy.IsValidDirectory(sibling, DataDirectory));
    }

    [Fact]
    public void Resolve_directory_falls_back_to_the_default()
    {
        Assert.Equal(
            SnapshotPolicy.DefaultDirectory,
            SnapshotPolicy.ResolveDirectory(ClientSettings.Default));
        Assert.Equal(
            Path.GetFullPath(@"C:\snapshots-elsewhere"),
            SnapshotPolicy.ResolveDirectory(
                ClientSettings.Default with { SnapshotDirectory = @"C:\snapshots-elsewhere" }));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(501, true)]
    public void Write_trigger_fires_at_the_configured_threshold(int writes, bool expected)
    {
        Assert.Equal(expected, SnapshotPolicy.IsWriteTriggerReached(writes));
    }

    [Fact]
    public void Write_counter_fires_once_per_threshold_and_resets()
    {
        int fired = 0;
        var counter = new SnapshotWriteCounter(3, () => fired++);

        for (int index = 0; index < 2; index++)
        {
            counter.OnCaptured(null!);
        }
        Assert.Equal(0, fired);

        counter.OnCaptured(null!);
        Assert.Equal(1, fired);

        counter.OnCaptured(null!);
        Assert.Equal(1, fired);

        counter.OnCaptured(null!);
        counter.OnCaptured(null!);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Settings_reject_invalid_snapshot_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            (ClientSettings.Default with { SnapshotIntervalMinutes = 0 }).Validate);
        Assert.Throws<ArgumentOutOfRangeException>(
            (ClientSettings.Default with { SnapshotIntervalMinutes = 1441 }).Validate);
        Assert.Throws<ArgumentOutOfRangeException>(
            (ClientSettings.Default with { SnapshotKeep = 0 }).Validate);
        Assert.Throws<ArgumentOutOfRangeException>(
            (ClientSettings.Default with { SnapshotKeep = 11 }).Validate);
        Assert.Throws<ArgumentOutOfRangeException>(
            (ClientSettings.Default with { SnapshotDirectory = "relative" }).Validate);

        (ClientSettings.Default with
        {
            SnapshotIntervalMinutes = SnapshotPolicy.MinimumIntervalMinutes,
            SnapshotKeep = SnapshotPolicy.MaximumKeep,
            SnapshotDirectory = @"C:\snapshots-elsewhere",
        }).Validate();

        Assert.Equal(30, ClientSettings.Default.SnapshotIntervalMinutes);
        Assert.Equal(3, ClientSettings.Default.SnapshotKeep);
    }
}
