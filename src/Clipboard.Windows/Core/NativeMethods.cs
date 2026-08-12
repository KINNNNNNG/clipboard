using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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

    internal static unsafe string EncodeRecoveryCode(Guid vaultId, ReadOnlySpan<byte> masterKey)
    {
        byte[] vaultIdBytes = new byte[16];
        vaultId.TryWriteBytes(vaultIdBytes, bigEndian: true, out _);
        CoreBuffer output = default;
        try
        {
            fixed (byte* vaultIdPointer = vaultIdBytes)
            fixed (byte* masterKeyPointer = masterKey)
            {
                CoreStatus status = NativeMethods.clipboard_recovery_encode(
                    vaultIdPointer,
                    (nuint)vaultIdBytes.Length,
                    masterKeyPointer,
                    (nuint)masterKey.Length,
                    out output);
                if (status != CoreStatus.Ok || output.Pointer == 0)
                {
                    throw new CryptographicException();
                }
            }
            byte[] bytes = new byte[checked((int)output.Length)];
            try
            {
                Marshal.Copy(output.Pointer, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            if (output.Pointer != 0)
            {
                NativeMethods.clipboard_core_free_buffer(output);
            }
            CryptographicOperations.ZeroMemory(vaultIdBytes);
        }
    }

    internal static CoreStatus DecodeRecoveryCode(
        ReadOnlySpan<byte> code,
        out CoreBuffer output)
    {
        unsafe
        {
            fixed (byte* codePointer = code)
            {
                return NativeMethods.clipboard_recovery_decode(
                    codePointer,
                    (nuint)code.Length,
                    out output);
            }
        }
    }

    internal static void FreeRecoveryBuffer(CoreBuffer buffer) =>
        NativeMethods.clipboard_core_free_buffer(buffer);

    internal static unsafe CoreStatus DecodePairingFile(
        ReadOnlySpan<byte> encoded,
        ReadOnlySpan<byte> password,
        out CoreBuffer output)
    {
        fixed (byte* encodedPointer = encoded)
        fixed (byte* passwordPointer = password)
        {
            return NativeMethods.clipboard_pairing_file_decode(
                encodedPointer,
                (nuint)encoded.Length,
                passwordPointer,
                (nuint)password.Length,
                out output);
        }
    }

    internal static unsafe CoreStatus EncodePairingFile(
        Guid vaultId,
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> provider,
        ReadOnlySpan<byte> endpoint,
        ReadOnlySpan<byte> rootOrPrefix,
        ReadOnlySpan<byte> bucket,
        ReadOnlySpan<byte> region,
        ReadOnlySpan<byte> password,
        out CoreBuffer output)
    {
        byte[] vaultIdBytes = new byte[16];
        vaultId.TryWriteBytes(vaultIdBytes, bigEndian: true, out _);
        fixed (byte* vaultIdPointer = vaultIdBytes)
        fixed (byte* masterKeyPointer = masterKey)
        fixed (byte* providerPointer = provider)
        fixed (byte* endpointPointer = endpoint)
        fixed (byte* rootPointer = rootOrPrefix)
        fixed (byte* bucketPointer = bucket)
        fixed (byte* regionPointer = region)
        fixed (byte* passwordPointer = password)
        {
            return NativeMethods.clipboard_pairing_file_encode(
                vaultIdPointer,
                (nuint)vaultIdBytes.Length,
                masterKeyPointer,
                (nuint)masterKey.Length,
                providerPointer,
                (nuint)provider.Length,
                endpointPointer,
                (nuint)endpoint.Length,
                rootPointer,
                (nuint)rootOrPrefix.Length,
                bucketPointer,
                (nuint)bucket.Length,
                regionPointer,
                (nuint)region.Length,
                passwordPointer,
                (nuint)password.Length,
                out output);
        }
    }
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
    internal static unsafe partial CoreStatus clipboard_recovery_encode(
        byte* vaultId,
        nuint vaultIdLength,
        byte* masterKey,
        nuint masterKeyLength,
        out CoreBuffer output);

    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_recovery_decode(
        byte* code,
        nuint codeLength,
        out CoreBuffer output);

    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_pairing_file_decode(
        byte* file,
        nuint fileLength,
        byte* password,
        nuint passwordLength,
        out CoreBuffer output);

    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_pairing_file_encode(
        byte* vaultId,
        nuint vaultIdLength,
        byte* masterKey,
        nuint masterKeyLength,
        byte* provider,
        nuint providerLength,
        byte* endpoint,
        nuint endpointLength,
        byte* rootOrPrefix,
        nuint rootOrPrefixLength,
        byte* bucket,
        nuint bucketLength,
        byte* region,
        nuint regionLength,
        byte* password,
        nuint passwordLength,
        out CoreBuffer output);

    [LibraryImport("clipboard_ffi")]
    internal static partial void clipboard_core_free_buffer(CoreBuffer buffer);

    [LibraryImport("clipboard_ffi")]
    internal static partial void clipboard_core_close(nint handle);
}
