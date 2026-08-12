using System.Security.Cryptography;
using System.Text;
using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class VaultBootstrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"clipboard-vault-test-{Guid.NewGuid():N}");

    [Fact]
    public void Load_or_create_persists_same_vault_and_dpapi_protected_key()
    {
        var bootstrapper = new VaultBootstrapper(_root);
        byte[] firstKey;
        Guid firstId;
        using (VaultMaterial first = bootstrapper.LoadOrCreate())
        {
            firstKey = first.VaultKey.ToArray();
            firstId = first.VaultId;
            Assert.Equal(32, firstKey.Length);
        }

        using VaultMaterial second = bootstrapper.LoadOrCreate();
        Assert.Equal(firstId, second.VaultId);
        Assert.Equal(firstKey, second.VaultKey);
        string persisted = File.ReadAllText(bootstrapper.KeyPath);
        Assert.DoesNotContain(Convert.ToBase64String(firstKey), persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(firstKey), persisted, StringComparison.OrdinalIgnoreCase);
        CryptographicOperations.ZeroMemory(firstKey);
    }

    [Fact]
    public void Has_local_vault_tracks_the_dpapi_vault_file()
    {
        var bootstrapper = new VaultBootstrapper(_root);
        Assert.False(bootstrapper.HasLocalVault);

        using VaultMaterial _ = bootstrapper.LoadOrCreate();

        Assert.True(bootstrapper.HasLocalVault);
    }

    [Fact]
    public void Protection_errors_are_sanitized_and_leave_no_key_file()
    {
        var protector = new ThrowingProtector();
        var bootstrapper = new VaultBootstrapper(_root, protector);

        VaultBootstrapException error = Assert.Throws<VaultBootstrapException>(
            bootstrapper.LoadOrCreate);

        Assert.NotNull(protector.ObservedKey);
        Assert.DoesNotContain(
            Convert.ToBase64String(protector.ObservedKey!),
            error.Message,
            StringComparison.Ordinal);
        Assert.False(File.Exists(bootstrapper.KeyPath));
        Assert.Empty(Directory.GetFiles(bootstrapper.RootDirectory, "*.tmp"));
        CryptographicOperations.ZeroMemory(protector.ObservedKey!);
    }

    [Fact]
    public void Export_and_import_recovery_code_round_trip_without_writing_plaintext_key()
    {
        var codec = new FakeRecoveryCodeCodec();
        var bootstrapper = new VaultBootstrapper(_root, new DpapiKeyProtector(), codec);
        Guid vaultId;
        byte[] key;
        using (VaultMaterial material = bootstrapper.LoadOrCreate())
        {
            vaultId = material.VaultId;
            key = material.VaultKey.ToArray();
            string code = bootstrapper.ExportRecoveryCode(material);
            Assert.DoesNotContain(Convert.ToHexString(key), code, StringComparison.OrdinalIgnoreCase);

            string importedRoot = Path.Combine(_root, "imported");
            var imported = new VaultBootstrapper(importedRoot, new DpapiKeyProtector(), codec);
            imported.ImportRecoveryCode(code);
            using VaultMaterial restored = imported.LoadOrCreate();
            Assert.Equal(vaultId, restored.VaultId);
            Assert.Equal(key, restored.VaultKey);
        }
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public void Invalid_recovery_code_does_not_replace_existing_vault_file()
    {
        var codec = new FakeRecoveryCodeCodec { ThrowOnDecode = true };
        var bootstrapper = new VaultBootstrapper(_root, new DpapiKeyProtector(), codec);
        using VaultMaterial original = bootstrapper.LoadOrCreate();
        string before = File.ReadAllText(bootstrapper.KeyPath);

        Assert.Throws<VaultBootstrapException>(() => bootstrapper.ImportRecoveryCode("bad-code"));

        Assert.Equal(before, File.ReadAllText(bootstrapper.KeyPath));
        Assert.Equal(original.VaultId, bootstrapper.LoadOrCreate().VaultId);
    }

    [Fact]
    public void Recovery_code_import_refuses_to_replace_an_existing_vault()
    {
        var codec = new FakeRecoveryCodeCodec();
        var bootstrapper = new VaultBootstrapper(_root, new DpapiKeyProtector(), codec);
        using VaultMaterial original = bootstrapper.LoadOrCreate();
        string code = codec.Encode(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));

        Assert.Throws<VaultBootstrapException>(() => bootstrapper.ImportRecoveryCode(code));
        Assert.Equal(original.VaultId, bootstrapper.LoadOrCreate().VaultId);
    }

    [Fact]
    public void Pairing_file_import_persists_vault_and_returns_only_non_sensitive_remote_configuration()
    {
        var codec = new FakePairingFileCodec();
        Guid vaultId = Guid.NewGuid();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        codec.Material = new PairingFileMaterial(
            vaultId,
            key.ToArray(),
            "webdav",
            "https://sync.example.test",
            "/clipboard");
        var bootstrapper = new VaultBootstrapper(
            _root,
            new DpapiKeyProtector(),
            new FfiRecoveryCodeCodec(),
            codec);

        PairingRemoteConfiguration configuration = bootstrapper.ImportPairingFile(
            "pairing-file"u8,
            "one-time-password");

        Assert.Equal("webdav", configuration.Provider);
        Assert.Equal("https://sync.example.test", configuration.Endpoint);
        Assert.Equal("/clipboard", configuration.RootOrPrefix);
        using VaultMaterial restored = bootstrapper.LoadOrCreate();
        Assert.Equal(vaultId, restored.VaultId);
        Assert.Equal(key, restored.VaultKey);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public void Pairing_file_import_refuses_to_replace_an_existing_vault()
    {
        var codec = new FakePairingFileCodec
        {
            Material = new PairingFileMaterial(
                Guid.NewGuid(),
                RandomNumberGenerator.GetBytes(32),
                "webdav",
                "https://sync.example.test",
                null),
        };
        var bootstrapper = new VaultBootstrapper(
            _root,
            new DpapiKeyProtector(),
            new FfiRecoveryCodeCodec(),
            codec);
        using VaultMaterial original = bootstrapper.LoadOrCreate();
        string before = File.ReadAllText(bootstrapper.KeyPath);

        Assert.Throws<VaultBootstrapException>(() =>
            bootstrapper.ImportPairingFile("pairing-file"u8, "password"));

        Assert.Equal(before, File.ReadAllText(bootstrapper.KeyPath));
        Assert.Equal(original.VaultId, bootstrapper.LoadOrCreate().VaultId);
    }

    [Fact]
    public void Pairing_file_export_round_trips_without_credentials()
    {
        var bootstrapper = new VaultBootstrapper(
            _root,
            new DpapiKeyProtector(),
            new FfiRecoveryCodeCodec(),
            new FfiPairingFileCodec());
        using VaultMaterial material = bootstrapper.LoadOrCreate();
        var configuration = new PairingRemoteConfiguration(
            "webdav",
            "https://sync.example.test",
            "/clipboard");

        byte[] file = bootstrapper.ExportPairingFile(
            material,
            configuration,
            "one-time-password");

        Assert.NotEmpty(file);
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(file), StringComparison.OrdinalIgnoreCase);
        var importedBootstrapper = new VaultBootstrapper(
            Path.Combine(_root, "imported"),
            new DpapiKeyProtector(),
            new FfiRecoveryCodeCodec(),
            new FfiPairingFileCodec());
        PairingRemoteConfiguration imported = importedBootstrapper.ImportPairingFile(
            file,
            "one-time-password");
        Assert.Equal(configuration, imported);
        CryptographicOperations.ZeroMemory(file);
    }

    [Fact]
    public async Task Pairing_file_import_saves_disabled_remote_settings_without_credentials()
    {
        var codec = new FakePairingFileCodec
        {
            Material = new PairingFileMaterial(
                Guid.NewGuid(),
                RandomNumberGenerator.GetBytes(32),
                "webdav",
                "https://sync.example.test",
                "/clipboard"),
        };
        var bootstrapper = new VaultBootstrapper(
            _root,
            new DpapiKeyProtector(),
            new FfiRecoveryCodeCodec(),
            codec);
        string settingsPath = Path.Combine(_root, "settings.json");
        var settingsStore = new ClientSettingsStore(settingsPath);

        await bootstrapper.ImportPairingFileAsync(
            "pairing-file"u8.ToArray(),
            "one-time-password",
            settingsStore);

        ClientSettings settings = await settingsStore.LoadAsync();
        Assert.False(settings.Sync!.Enabled);
        Assert.Equal("https://sync.example.test", settings.Sync.Endpoint);
        Assert.DoesNotContain("one-time-password", await File.ReadAllTextAsync(settingsPath));
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(settingsPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ffi_recovery_code_codec_round_trips_vault_material()
    {
        Guid vaultId = Guid.NewGuid();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var codec = new FfiRecoveryCodeCodec();
            string code = codec.Encode(vaultId, key);
            Assert.Equal(85, code.Length);
            Assert.All([5, 11, 17, 23, 29, 35, 41, 47, 53, 59, 65, 71, 77, 83], index =>
                Assert.Equal('-', code[index]));
            RecoveryCodeMaterial decoded = codec.Decode(code);

            Assert.Equal(vaultId, decoded.VaultId);
            Assert.Equal(key, decoded.MasterKey);
            Assert.DoesNotContain(Convert.ToHexString(key), code, StringComparison.OrdinalIgnoreCase);
            CryptographicOperations.ZeroMemory(decoded.MasterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ThrowingProtector : IKeyProtector
    {
        public byte[]? ObservedKey { get; private set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            ObservedKey = plaintext.ToArray();
            throw new CryptographicException("simulated DPAPI failure");
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRecoveryCodeCodec : IRecoveryCodeCodec
    {
        private readonly Dictionary<string, RecoveryCodeMaterial> _materials = new();

        public bool ThrowOnDecode { get; init; }

        public string Encode(Guid vaultId, ReadOnlySpan<byte> masterKey)
        {
            string code = $"recovery-{Guid.NewGuid():N}";
            _materials[code] = new RecoveryCodeMaterial(vaultId, masterKey.ToArray());
            return code;
        }

        public RecoveryCodeMaterial Decode(string code)
        {
            if (ThrowOnDecode || !_materials.Remove(code, out RecoveryCodeMaterial material))
            {
                throw new FormatException();
            }
            return material;
        }
    }

    private sealed class FakePairingFileCodec : IPairingFileCodec
    {
        public PairingFileMaterial? Material { get; set; }

        public PairingFileMaterial Decode(ReadOnlySpan<byte> encoded, string password) =>
            Material ?? throw new FormatException();

        public byte[] Encode(
            Guid vaultId,
            ReadOnlySpan<byte> masterKey,
            PairingRemoteConfiguration configuration,
            string password) =>
            Encoding.UTF8.GetBytes("pairing-file");
    }
}
