using System.Security.Cryptography;
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
}
