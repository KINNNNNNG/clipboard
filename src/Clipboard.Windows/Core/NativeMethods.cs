using System.Runtime.InteropServices;

namespace Clipboard.Windows.Core;

internal interface IClipboardCoreNative
{
    CoreStatus OpenV2(
        ReadOnlySpan<byte> dataDirectory,
        ReadOnlySpan<byte> vaultKey,
        ReadOnlySpan<byte> vaultId,
        out nint handle);

    CoreStatus Execute(nint handle, ReadOnlySpan<byte> request, out CoreBuffer response);

    CoreStatus IngestImage(
        nint handle,
        ReadOnlySpan<byte> metadata,
        ReadOnlySpan<byte> png,
        out CoreBuffer response);

    CoreStatus ReadImage(nint handle, ReadOnlySpan<byte> itemId, out CoreBuffer response);

    void FreeBuffer(CoreBuffer buffer);

    void Close(nint handle);
}

internal sealed class PInvokeClipboardCoreNative : IClipboardCoreNative
{
    public static PInvokeClipboardCoreNative Instance { get; } = new();

    private PInvokeClipboardCoreNative()
    {
    }

    public unsafe CoreStatus OpenV2(
        ReadOnlySpan<byte> dataDirectory,
        ReadOnlySpan<byte> vaultKey,
        ReadOnlySpan<byte> vaultId,
        out nint handle)
    {
        fixed (byte* dataDirectoryPointer = dataDirectory)
        fixed (byte* vaultKeyPointer = vaultKey)
        fixed (byte* vaultIdPointer = vaultId)
        {
            return NativeMethods.clipboard_core_open_v2(
                dataDirectoryPointer,
                (nuint)dataDirectory.Length,
                vaultKeyPointer,
                (nuint)vaultKey.Length,
                vaultIdPointer,
                (nuint)vaultId.Length,
                out handle);
        }
    }

    public unsafe CoreStatus Execute(
        nint handle,
        ReadOnlySpan<byte> request,
        out CoreBuffer response)
    {
        fixed (byte* requestPointer = request)
        {
            return NativeMethods.clipboard_core_execute(
                handle,
                requestPointer,
                (nuint)request.Length,
                out response);
        }
    }

    public unsafe CoreStatus IngestImage(
        nint handle,
        ReadOnlySpan<byte> metadata,
        ReadOnlySpan<byte> png,
        out CoreBuffer response)
    {
        fixed (byte* metadataPointer = metadata)
        fixed (byte* pngPointer = png)
        {
            return NativeMethods.clipboard_core_ingest_image(
                handle,
                metadataPointer,
                (nuint)metadata.Length,
                pngPointer,
                (nuint)png.Length,
                out response);
        }
    }

    public unsafe CoreStatus ReadImage(
        nint handle,
        ReadOnlySpan<byte> itemId,
        out CoreBuffer response)
    {
        fixed (byte* itemIdPointer = itemId)
        {
            return NativeMethods.clipboard_core_read_image(
                handle,
                itemIdPointer,
                (nuint)itemId.Length,
                out response);
        }
    }

    public void FreeBuffer(CoreBuffer buffer) => NativeMethods.clipboard_core_free_buffer(buffer);

    public void Close(nint handle) => NativeMethods.clipboard_core_close(handle);
}

internal static partial class NativeMethods
{
    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_core_open_v2(
        byte* dataDirectory,
        nuint dataDirectoryLength,
        byte* vaultKey,
        nuint vaultKeyLength,
        byte* vaultId,
        nuint vaultIdLength,
        out nint handle);

    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_core_execute(
        nint handle,
        byte* request,
        nuint requestLength,
        out CoreBuffer response);

    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_core_ingest_image(
        nint handle,
        byte* metadata,
        nuint metadataLength,
        byte* png,
        nuint pngLength,
        out CoreBuffer response);

    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_core_read_image(
        nint handle,
        byte* itemId,
        nuint itemIdLength,
        out CoreBuffer response);

    [LibraryImport("clipboard_ffi")]
    internal static partial void clipboard_core_free_buffer(CoreBuffer buffer);

    [LibraryImport("clipboard_ffi")]
    internal static partial void clipboard_core_close(nint handle);
}
