using System.Diagnostics;
using System.Text;

namespace Clipboard.Windows.Views;

internal static class SelectionDiagnostics
{
    private static readonly object Gate = new();
    private static readonly string LogPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "clipboard-windows-selection.log");

    public static string Path => LogPath;

    public static void StartSession()
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    LogPath,
                    $"\r\n--- session {DateTimeOffset.Now:O} pid={Environment.ProcessId} ---\r\n",
                    Encoding.UTF8);
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Selection diagnostics unavailable: {error.Message}");
        }
    }

    public static void Write(string message)
    {
        try
        {
            string line = $"{DateTimeOffset.Now:O} tid={Environment.CurrentManagedThreadId} {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Selection diagnostics unavailable: {error.Message}");
        }
    }
}
