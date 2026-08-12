using System.Security.Cryptography;
using System.Text;
using Clipboard.Windows.Core;

namespace Clipboard.Windows.Platform;

internal sealed class FfiRecoveryCodeCodec : IRecoveryCodeCodec
{
    public string Encode(Guid vaultId, ReadOnlySpan<byte> masterKey) =>
        PInvokeClipboardCoreNative.EncodeRecoveryCode(vaultId, masterKey);

    public RecoveryCodeMaterial Decode(string code)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(code);
        CoreBuffer output = default;
        try
        {
            CoreStatus status = PInvokeClipboardCoreNative.DecodeRecoveryCode(bytes, out output);
            if (status != CoreStatus.Ok)
            {
                throw new FormatException();
            }
            if (output.Length != 48 || output.Pointer == 0)
            {
                throw new CryptographicException();
            }
            byte[] material = CopyBuffer(output);
            try
            {
                Guid vaultId = new(material.AsSpan(0, 16), bigEndian: true);
                byte[] masterKey = material[16..];
                return new RecoveryCodeMaterial(vaultId, masterKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }
        finally
        {
            if (output.Pointer != 0)
            {
                PInvokeClipboardCoreNative.FreeRecoveryBuffer(output);
            }
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] CopyBuffer(CoreBuffer buffer)
    {
        byte[] bytes = new byte[checked((int)buffer.Length)];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Pointer, bytes, 0, bytes.Length);
        return bytes;
    }
}
