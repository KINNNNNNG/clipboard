using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clipboard.Windows.Core;

namespace Clipboard.Windows.Platform;

internal sealed class PairingFileMaterial : IDisposable
{
    private int _disposed;

    public PairingFileMaterial(
        Guid vaultId,
        byte[] masterKey,
        string provider,
        string endpoint,
        string? rootOrPrefix,
        string? bucket = null,
        string? region = null)
    {
        VaultId = vaultId;
        MasterKey = masterKey ?? throw new ArgumentNullException(nameof(masterKey));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        RootOrPrefix = rootOrPrefix;
        Bucket = bucket;
        Region = region;
    }

    public Guid VaultId { get; }

    public byte[] MasterKey { get; }

    public string Provider { get; }

    public string Endpoint { get; }

    public string? RootOrPrefix { get; }

    public string? Bucket { get; }

    public string? Region { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CryptographicOperations.ZeroMemory(MasterKey);
        }
    }
}

internal interface IPairingFileCodec
{
    byte[] Encode(
        Guid vaultId,
        ReadOnlySpan<byte> masterKey,
        PairingRemoteConfiguration configuration,
        string password);

    PairingFileMaterial Decode(ReadOnlySpan<byte> encoded, string password);
}

internal sealed class FfiPairingFileCodec : IPairingFileCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public PairingFileMaterial Decode(ReadOnlySpan<byte> encoded, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        CoreBuffer output = default;
        try
        {
            CoreStatus status = PInvokeClipboardCoreNative.DecodePairingFile(
                encoded,
                passwordBytes,
                out output);
            if (status != CoreStatus.Ok || output.Pointer == 0 || output.Length == 0)
            {
                throw new FormatException();
            }

            byte[] json = CopyBuffer(output);
            try
            {
                PairingWire wire = JsonSerializer.Deserialize<PairingWire>(json, JsonOptions)
                    ?? throw new FormatException();
                if (!Guid.TryParse(wire.VaultId, out Guid vaultId)
                    || vaultId == Guid.Empty
                    || wire.Provider is not ("webdav" or "oss")
                    || string.IsNullOrWhiteSpace(wire.Endpoint)
                    || !Uri.TryCreate(wire.Endpoint, UriKind.Absolute, out Uri? endpoint)
                    || endpoint.Scheme != Uri.UriSchemeHttps
                    || wire.MasterKeyHex is null
                    || wire.MasterKeyHex.Length != 64)
                {
                    throw new CryptographicException();
                }

                byte[] masterKey;
                try
                {
                    masterKey = Convert.FromHexString(wire.MasterKeyHex);
                }
                catch (FormatException)
                {
                    throw new CryptographicException();
                }
                if (masterKey.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(masterKey);
                    throw new CryptographicException();
                }

                return new PairingFileMaterial(
                    vaultId,
                    masterKey,
                    wire.Provider,
                    wire.Endpoint,
                    wire.RootOrPrefix,
                    wire.Bucket,
                    wire.Region);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
        finally
        {
            if (output.Pointer != 0)
            {
                PInvokeClipboardCoreNative.FreeRecoveryBuffer(output);
            }
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public byte[] Encode(
        Guid vaultId,
        ReadOnlySpan<byte> masterKey,
        PairingRemoteConfiguration configuration,
        string password)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        byte[] provider = Encoding.UTF8.GetBytes(configuration.Provider);
        byte[] endpoint = Encoding.UTF8.GetBytes(configuration.Endpoint);
        byte[] root = configuration.RootOrPrefix is null
            ? []
            : Encoding.UTF8.GetBytes(configuration.RootOrPrefix);
        byte[] bucket = configuration.Bucket is null
            ? []
            : Encoding.UTF8.GetBytes(configuration.Bucket);
        byte[] region = configuration.Region is null
            ? []
            : Encoding.UTF8.GetBytes(configuration.Region);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        CoreBuffer output = default;
        try
        {
            CoreStatus status = PInvokeClipboardCoreNative.EncodePairingFile(
                vaultId,
                masterKey,
                provider,
                endpoint,
                root,
                bucket,
                region,
                passwordBytes,
                out output);
            if (status != CoreStatus.Ok || output.Pointer == 0 || output.Length == 0)
            {
                throw new CryptographicException();
            }
            return CopyBuffer(output);
        }
        finally
        {
            if (output.Pointer != 0)
            {
                PInvokeClipboardCoreNative.FreeRecoveryBuffer(output);
            }
            CryptographicOperations.ZeroMemory(provider);
            CryptographicOperations.ZeroMemory(endpoint);
            CryptographicOperations.ZeroMemory(root);
            CryptographicOperations.ZeroMemory(bucket);
            CryptographicOperations.ZeroMemory(region);
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static byte[] CopyBuffer(CoreBuffer buffer)
    {
        byte[] bytes = new byte[checked((int)buffer.Length)];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Pointer, bytes, 0, bytes.Length);
        return bytes;
    }

    private sealed record PairingWire(
        string VaultId,
        string MasterKeyHex,
        string Provider,
        string Endpoint,
        string? RootOrPrefix,
        string? Bucket,
        string? Region);
}
