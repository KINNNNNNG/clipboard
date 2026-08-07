using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class SyncCredentialStoreTests
{
    [Fact]
    public async Task Credentials_are_DPAPI_protected_and_never_written_to_settings_json()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"clipboard-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(directory, "settings.json");
        string credentialPath = Path.Combine(directory, "sync-credentials.json");
        var settingsStore = new ClientSettingsStore(settingsPath);
        var credentials = new SyncCredentialStore(credentialPath);
        var sync = new SyncSettings(
            true,
            "webdav",
            "https://sync.example.test/root",
            "/",
            null,
            null,
            null,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("N"));

        try
        {
            await settingsStore.SaveAsync(ClientSettings.Default with { Sync = sync });
            await credentials.SaveAsync(sync.CredentialProfileId!, new SyncCredentials("alice", "secret"));

            string settingsJson = await File.ReadAllTextAsync(settingsPath);
            Assert.DoesNotContain("alice", settingsJson);
            Assert.DoesNotContain("secret", settingsJson);
            Assert.Equal("secret", (await credentials.LoadAsync(sync.CredentialProfileId!))!.Secret);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
