using System.Runtime.InteropServices;

namespace Clipboard.Windows.Platform;

internal static partial class NativeMethods
{
    internal const uint InputKeyboard = 1;
    internal const uint KeyEventKeyUp = 0x0002;

    internal static class VirtualKey
    {
        internal const ushort Control = 0x11;
        internal const ushort V = 0x56;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        internal uint Type;
        internal INPUTUNION Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)]
        internal KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    internal static partial nint GetClipboardOwner();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static unsafe partial uint SendInput(
        uint inputCount,
        INPUT* inputs,
        int inputSize);
}
