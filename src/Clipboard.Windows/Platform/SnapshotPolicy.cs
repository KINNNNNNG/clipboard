namespace Clipboard.Windows.Platform;

/// <summary>
/// Snapshot cadence, location rules and validation.
/// </summary>
internal static class SnapshotPolicy
{
    public const int DefaultIntervalMinutes = 30;
    public const int DefaultKeep = 3;
    public const int MinimumIntervalMinutes = 1;
    public const int MaximumIntervalMinutes = 1440;
    public const int MinimumKeep = 1;
    public const int MaximumKeep = 10;
    /// <summary>Captured items written before a snapshot is forced.</summary>
    public const int WritesPerSnapshot = 500;

    public static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clipboard");

    public static string DataDirectory => Path.Combine(LocalRoot, "data");

    public static string DefaultDirectory => Path.Combine(LocalRoot, "snapshots");

    /// <summary>Uses the configured directory, falling back to the default one.</summary>
    public static string ResolveDirectory(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.IsNullOrWhiteSpace(settings.SnapshotDirectory)
            ? DefaultDirectory
            : Path.GetFullPath(settings.SnapshotDirectory);
    }

    /// <summary>Reports whether enough items were captured to force a snapshot.</summary>
    public static bool IsWriteTriggerReached(int writesSinceSnapshot) =>
        writesSinceSnapshot >= WritesPerSnapshot;

    /// <summary>
    /// A snapshot directory must be absolute and must not live inside the data directory, so a
    /// snapshot never gets deleted together with the database it is meant to protect.
    /// </summary>
    public static bool IsValidDirectory(string? directory, string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return true;
        }
        if (!Path.IsPathRooted(directory))
        {
            return false;
        }

        string candidate = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string data = Path.GetFullPath(dataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !candidate.Equals(data, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
