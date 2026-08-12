using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Clipboard.Windows.Platform;

internal sealed record SyncCredentials(string Account, string Secret);

internal interface ISyncCredentialStore
{
    Task SaveAsync(string profileId, SyncCredentials credentials, CancellationToken cancellationToken = default);

    Task<SyncCredentials?> LoadAsync(string profileId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string profileId, CancellationToken cancellationToken = default);
}

internal sealed class SyncCredentialStore : ISyncCredentialStore
{
    private readonly string _path;

    public SyncCredentialStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipboard",
            "sync-credentials.json"))
    {
    }

    internal SyncCredentialStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public async Task SaveAsync(
        string profileId,
        SyncCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(credentials);
        byte[] plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new ProtectedCredential(profileId, credentials.Account, credentials.Secret)));
        byte[] protectedBytes = [];
        try
        {
            protectedBytes = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporary, Convert.ToBase64String(protectedBytes), cancellationToken);
            File.Move(temporary, _path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public async Task<SyncCredentials?> LoadAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!File.Exists(_path))
        {
            return null;
        }
        byte[] protectedBytes = Convert.FromBase64String(await File.ReadAllTextAsync(_path, cancellationToken));
        byte[] plaintext = [];
        try
        {
            plaintext = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            ProtectedCredential? credential = JsonSerializer.Deserialize<ProtectedCredential>(plaintext);
            return credential is { ProfileId: var storedProfileId } && storedProfileId == profileId
                ? new SyncCredentials(credential.Account, credential.Secret)
                : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        SyncCredentials? stored = await LoadAsync(profileId, cancellationToken);
        if (stored is not null && File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private sealed record ProtectedCredential(string ProfileId, string Account, string Secret);
}
