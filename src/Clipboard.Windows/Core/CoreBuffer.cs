using System.Runtime.InteropServices;

namespace Clipboard.Windows.Core;

[StructLayout(LayoutKind.Sequential)]
internal struct CoreBuffer
{
    public CoreBuffer(nint pointer, nuint length, nuint capacity)
    {
        Pointer = pointer;
        Length = length;
        Capacity = capacity;
    }

    public nint Pointer;
    public nuint Length;
    public nuint Capacity;
}
