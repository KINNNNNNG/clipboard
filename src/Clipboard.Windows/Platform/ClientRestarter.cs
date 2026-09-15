using System.ComponentModel;
using System.Diagnostics;

namespace Clipboard.Windows.Platform;

/// <summary>
/// Starts a fresh client instance and lets the caller exit the current one.
/// </summary>
internal static class ClientRestarter
{
    public static bool Restart(Action requestExit)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
            });
            if (process is null)
            {
                return false;
            }
        }
        catch (Exception error) when (error is Win32Exception
            or InvalidOperationException
            or FileNotFoundException)
        {
            return false;
        }

        requestExit?.Invoke();
        return true;
    }
}
