namespace Clipboard.Windows.Platform;

/// <summary>
/// Records that local history should be rebuilt from the remote on the next start.
/// </summary>
/// <remarks>
/// The database cannot be replaced while the client holds it open, so the request is written now
/// and honoured by the next start, before the clipboard core opens.
/// </remarks>
internal static class VaultRestoreRequest
{
    internal const string FileName = "restore-from-remote.pending";

    public static string PathFor(string root) => Path.Combine(root, FileName);

    public static bool Exists(string root) => File.Exists(PathFor(root));

    public static void Write(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Directory.CreateDirectory(root);
        File.WriteAllText(PathFor(root), DateTimeOffset.UtcNow.ToString("O"));
    }

    public static void Clear(string root)
    {
        string path = PathFor(root);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
