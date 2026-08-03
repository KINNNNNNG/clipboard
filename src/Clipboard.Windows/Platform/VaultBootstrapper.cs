using System.Security.Cryptography;
using System.Text.Json;

namespace Clipboard.Windows.Platform;

internal interface IKeyProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}

internal sealed class DpapiKeyProtector : IKeyProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        byte[] copy = plaintext.ToArray();
        try
        {
            return ProtectedData.Protect(copy, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData) =>
        ProtectedData.Unprotect(
            protectedData.ToArray(),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
}

internal sealed class VaultBootstrapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private readonly IKeyProtector _protector;

    public VaultBootstrapper(string? localAppDataRoot = null)
        : this(localAppDataRoot, new DpapiKeyProtector())
    {
    }

    internal VaultBootstrapper(string? localAppDataRoot, IKeyProtector protector)
    {
        string root = localAppDataRoot
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        RootDirectory = Path.Combine(root, "Clipboard");
        KeyPath = Path.Combine(RootDirectory, "vault.key");
        DataDirectory = Path.Combine(RootDirectory, "data");
        _protector = protector;
    }

    public string RootDirectory { get; }

    public string KeyPath { get; }

    public string DataDirectory { get; }

    public VaultMaterial LoadOrCreate()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        try
        {
            return File.Exists(KeyPath) ? Load() : Create();
        }
        catch (Exception error) when (IsExpectedBootstrapFailure(error))
        {
            throw new VaultBootstrapException("Unable to initialize the local clipboard vault.");
        }
    }

    private VaultMaterial Create()
    {
        byte[] vaultKey = RandomNumberGenerator.GetBytes(32);
        byte[]? protectedKey = null;
        byte[]? json = null;
        string? temporaryPath = null;
        bool transferred = false;
        try
        {
            protectedKey = _protector.Protect(vaultKey);
            var file = new VaultFile(1, Guid.NewGuid(), Convert.ToBase64String(protectedKey));
            json = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
            temporaryPath = Path.Combine(RootDirectory, $"vault-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, KeyPath, overwrite: true);
            temporaryPath = null;
            transferred = true;
            return new VaultMaterial(file.VaultId, vaultKey, DataDirectory);
        }
        finally
        {
            if (!transferred)
            {
                CryptographicOperations.ZeroMemory(vaultKey);
            }
            if (protectedKey is not null)
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }
            if (json is not null)
            {
                CryptographicOperations.ZeroMemory(json);
            }
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private VaultMaterial Load()
    {
        byte[] json = File.ReadAllBytes(KeyPath);
        byte[]? protectedKey = null;
        byte[]? vaultKey = null;
        bool transferred = false;
        try
        {
            VaultFile file = JsonSerializer.Deserialize<VaultFile>(json, JsonOptions)
                ?? throw new JsonException("Missing vault payload.");
            if (file.Version != 1 || file.VaultId == Guid.Empty)
            {
                throw new JsonException("Unsupported vault payload.");
            }
            protectedKey = Convert.FromBase64String(file.ProtectedKey);
            vaultKey = _protector.Unprotect(protectedKey);
            if (vaultKey.Length != 32)
            {
                throw new CryptographicException("Invalid vault key length.");
            }
            transferred = true;
            return new VaultMaterial(file.VaultId, vaultKey, DataDirectory);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
            if (protectedKey is not null)
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }
            if (!transferred && vaultKey is not null)
            {
                CryptographicOperations.ZeroMemory(vaultKey);
            }
        }
    }

    private static bool IsExpectedBootstrapFailure(Exception error) =>
        error is IOException
            or UnauthorizedAccessException
            or CryptographicException
            or JsonException
            or FormatException;

    private sealed record VaultFile(int Version, Guid VaultId, string ProtectedKey);
}

internal sealed class VaultMaterial : IDisposable
{
    private int _disposed;

    public VaultMaterial(Guid vaultId, byte[] vaultKey, string dataDirectory)
    {
        VaultId = vaultId;
        VaultKey = vaultKey;
        DataDirectory = dataDirectory;
    }

    public Guid VaultId { get; }

    public byte[] VaultKey { get; }

    public string DataDirectory { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CryptographicOperations.ZeroMemory(VaultKey);
        }
    }
}

internal sealed class VaultBootstrapException : Exception
{
    public VaultBootstrapException(string message)
        : base(message)
    {
    }
}
