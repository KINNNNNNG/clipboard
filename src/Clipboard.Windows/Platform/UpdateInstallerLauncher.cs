using System.ComponentModel;
using System.Diagnostics;

namespace Clipboard.Windows.Platform;

internal interface IUpdateInstallerLauncher
{
    /// <summary>
    /// Starts the verified installer. Returns false when the process could not be created.
    /// </summary>
    bool Launch(string installerPath);
}

internal sealed class UpdateInstallerLauncher : IUpdateInstallerLauncher
{
    public bool Launch(string installerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
            });
            return process is not null;
        }
        catch (Exception error) when (error is Win32Exception
            or InvalidOperationException
            or FileNotFoundException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
