using System.Security.Cryptography;
using System.Text.Json;
using Clipboard.Windows.Core;

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

internal readonly record struct RecoveryCodeMaterial(Guid VaultId, byte[] MasterKey);

internal sealed record PairingRemoteConfiguration(
    string Provider,
    string Endpoint,
    string? RootOrPrefix,
    string? Bucket = null,
    string? Region = null);

internal interface IRecoveryCodeCodec
{
    string Encode(Guid vaultId, ReadOnlySpan<byte> masterKey);

    RecoveryCodeMaterial Decode(string code);
}

internal sealed class VaultBootstrapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private readonly IKeyProtector _protector;
    private readonly IRecoveryCodeCodec _recoveryCodeCodec;
    private readonly IPairingFileCodec _pairingFileCodec;

    public VaultBootstrapper(string? localAppDataRoot = null)
        : this(localAppDataRoot, new DpapiKeyProtector(), new FfiRecoveryCodeCodec(), new FfiPairingFileCodec())
    {
    }

    internal VaultBootstrapper(string? localAppDataRoot, IKeyProtector protector)
        : this(localAppDataRoot, protector, new FfiRecoveryCodeCodec(), new FfiPairingFileCodec())
    {
    }

    internal VaultBootstrapper(
        string? localAppDataRoot,
        IKeyProtector protector,
        IRecoveryCodeCodec recoveryCodeCodec,
        IPairingFileCodec? pairingFileCodec = null)
    {
        string root = localAppDataRoot
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        RootDirectory = Path.Combine(root, "Clipboard");
        KeyPath = Path.Combine(RootDirectory, "vault.key");
        DataDirectory = Path.Combine(RootDirectory, "data");
        _protector = protector;
        _recoveryCodeCodec = recoveryCodeCodec;
        _pairingFileCodec = pairingFileCodec ?? new FfiPairingFileCodec();
    }

    public string RootDirectory { get; }

    public string KeyPath { get; }

    public string DataDirectory { get; }

    public bool HasLocalVault => File.Exists(KeyPath);

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

    public string ExportRecoveryCode(VaultMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        try
        {
            return _recoveryCodeCodec.Encode(material.VaultId, material.VaultKey);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException or FormatException)
        {
            throw new VaultBootstrapException("Unable to export the vault recovery code.");
        }
    }

    public void ImportRecoveryCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (HasLocalVault)
        {
            throw new VaultBootstrapException("A local vault already exists.");
        }
        byte[]? masterKey = null;
        try
        {
            RecoveryCodeMaterial material = _recoveryCodeCodec.Decode(code);
            masterKey = material.MasterKey;
            if (material.VaultId == Guid.Empty || masterKey.Length != 32)
            {
                throw new CryptographicException();
            }
            Persist(material.VaultId, masterKey);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new VaultBootstrapException("Unable to import the vault recovery code.");
        }
        finally
        {
            if (masterKey is not null)
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
    }

    public PairingRemoteConfiguration ImportPairingFile(
        ReadOnlySpan<byte> encoded,
        string password)
    {
        if (encoded.IsEmpty)
        {
            throw new VaultBootstrapException("Unable to import the pairing file.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (HasLocalVault)
        {
            throw new VaultBootstrapException("A local vault already exists.");
        }

        PairingFileMaterial? material = null;
        try
        {
            material = _pairingFileCodec.Decode(encoded, password);
            if (material.VaultId == Guid.Empty || material.MasterKey.Length != 32)
            {
                throw new CryptographicException();
            }
            ValidatePairingConfiguration(material);
            Persist(material.VaultId, material.MasterKey);
            return new PairingRemoteConfiguration(
                material.Provider,
                material.Endpoint,
                material.RootOrPrefix,
                material.Bucket,
                material.Region);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new VaultBootstrapException("Unable to import the pairing file.");
        }
        finally
        {
            material?.Dispose();
        }
    }

    public async Task<PairingRemoteConfiguration> ImportPairingFileAsync(
        ReadOnlyMemory<byte> encoded,
        string password,
        IClientSettingsStore settingsStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        PairingRemoteConfiguration configuration = ImportPairingFile(encoded.Span, password);
        ClientSettings current = await settingsStore.LoadAsync(cancellationToken);
        SyncSettings sync = configuration.Provider == "oss"
            ? new SyncSettings(
                false,
                configuration.Provider,
                configuration.Endpoint,
                null,
                configuration.Bucket,
                configuration.Region,
                configuration.RootOrPrefix,
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("N"))
            : new SyncSettings(
                false,
                configuration.Provider,
                configuration.Endpoint,
                configuration.RootOrPrefix,
                null,
                null,
                null,
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("N"));
        await settingsStore.SaveAsync(current with { Sync = sync }, cancellationToken);
        return configuration;
    }

    public byte[] ExportPairingFile(
        VaultMaterial material,
        PairingRemoteConfiguration configuration,
        string password)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ValidatePairingConfiguration(configuration);
        try
        {
            return _pairingFileCodec.Encode(
                material.VaultId,
                material.VaultKey,
                configuration,
                password);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException or FormatException)
        {
            throw new VaultBootstrapException("Unable to export the pairing file.");
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

    private static void ValidatePairingConfiguration(PairingFileMaterial material)
    {
        if (material.Provider is not ("webdav" or "oss")
            || !Uri.TryCreate(material.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || material.Endpoint.Length > 2048
            || material.RootOrPrefix?.Length > 1024
            || material.Bucket?.Length > 256
            || material.Region?.Length > 128
            || (material.Provider == "oss" && (string.IsNullOrWhiteSpace(material.Bucket) || string.IsNullOrWhiteSpace(material.Region)))
            || (material.Provider == "webdav" && (material.Bucket is not null || material.Region is not null)))
        {
            throw new CryptographicException();
        }
    }

    private static void ValidatePairingConfiguration(PairingRemoteConfiguration configuration)
    {
        if (configuration.Provider is not ("webdav" or "oss")
            || !Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || configuration.Endpoint.Length > 2048
            || configuration.RootOrPrefix?.Length > 1024
            || configuration.Bucket?.Length > 256
            || configuration.Region?.Length > 128
            || (configuration.Provider == "oss" && (string.IsNullOrWhiteSpace(configuration.Bucket) || string.IsNullOrWhiteSpace(configuration.Region)))
            || (configuration.Provider == "webdav" && (configuration.Bucket is not null || configuration.Region is not null)))
        {
            throw new CryptographicException();
        }
    }

    private void Persist(Guid vaultId, byte[] vaultKey)
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        byte[]? protectedKey = null;
        byte[]? json = null;
        string? temporaryPath = null;
        try
        {
            protectedKey = _protector.Protect(vaultKey);
            var file = new VaultFile(1, vaultId, Convert.ToBase64String(protectedKey));
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
        }
        finally
        {
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
