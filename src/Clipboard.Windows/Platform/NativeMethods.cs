using System.Runtime.InteropServices;

namespace Clipboard.Windows.Platform;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    internal static partial nint GetClipboardOwner();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
