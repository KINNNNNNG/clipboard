using System.Diagnostics;

namespace Clipboard.Windows.Platform;

internal sealed class SourceApplicationResolver : ISourceApplicationResolver
{
    public string Resolve()
    {
        try
        {
            nint owner = NativeMethods.GetClipboardOwner();
            if (owner == 0)
            {
                return "unknown";
            }
            NativeMethods.GetWindowThreadProcessId(owner, out uint processId);
            if (processId == 0)
            {
                return "unknown";
            }
            using Process process = Process.GetProcessById(checked((int)processId));
            string name = process.ProcessName;
            if (string.IsNullOrWhiteSpace(name))
            {
                return "unknown";
            }
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name}.exe";
        }
        catch
        {
            return "unknown";
        }
    }
}
