using System.Runtime.InteropServices;

internal enum CoreStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidUtf8 = 2,
    InvalidJson = 3,
    CoreError = 4,
    Panic = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct CoreBuffer
{
    public nint Pointer;
    public nuint Length;
    public nuint Capacity;
}

internal static partial class NativeMethods
{
    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_core_open(
        byte* dataDirectory,
        nuint dataDirectoryLength,
        byte* vaultKey,
        nuint vaultKeyLength,
        out nint handle);

    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_core_execute(
        nint handle,
        byte* request,
        nuint requestLength,
        out CoreBuffer response);

    [LibraryImport("clipboard_ffi")]
    internal static partial void clipboard_core_free_buffer(CoreBuffer buffer);

    [LibraryImport("clipboard_ffi")]
    internal static partial void clipboard_core_close(nint handle);
}
