using System.Globalization;

namespace Clipboard.Windows.Platform;

/// <summary>
/// Moves a damaged history database aside so the client can start with a fresh one.
/// </summary>
/// <remarks>
/// The damaged file is preserved under a timestamped name in the same directory: nothing is
/// deleted, and the vault marker, key and cached objects are left untouched.
/// </remarks>
internal static class VaultRecovery
{
    internal const string HistoryFileName = "history.db";

    private static readonly string[] SidecarSuffixes = ["", "-wal", "-shm"];

    /// <summary>
    /// Renames the history database and its sidecars. Returns the quarantined file name, or null
    /// when no database was present.
    /// </summary>
    public static string? QuarantineHistory(string dataDirectory, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        string stamp = timestamp.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string? quarantined = null;

        foreach (string suffix in SidecarSuffixes)
        {
            string source = Path.Combine(dataDirectory, HistoryFileName + suffix);
            if (!File.Exists(source))
            {
                continue;
            }

            string destination = Path.Combine(
                dataDirectory,
                $"{HistoryFileName}.corrupt-{stamp}{suffix}");
            destination = MakeUnique(destination);
            File.Move(source, destination);
            quarantined ??= Path.GetFileName(destination);
        }

        return quarantined;
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileName(path);
        for (int index = 1; index < 1000; index++)
        {
            string candidate = Path.Combine(directory, $"{name}-{index}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a quarantine file name.");
    }
}
