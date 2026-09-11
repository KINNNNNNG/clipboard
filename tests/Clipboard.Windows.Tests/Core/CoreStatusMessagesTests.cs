using Clipboard.Windows.Core;
using Xunit;

namespace Clipboard.Windows.Tests.Core;

public sealed class CoreStatusMessagesTests
{
    [Fact]
    public void Vault_failures_map_to_fixed_log_categories()
    {
        Assert.Equal("storage_locked", CoreStatusMessages.ForLog(CoreStatus.StorageLocked));
        Assert.Equal("vault_key_mismatch", CoreStatusMessages.ForLog(CoreStatus.VaultKeyMismatch));
        Assert.Equal("vault_unreadable", CoreStatusMessages.ForLog(CoreStatus.VaultUnreadable));
        Assert.Equal("vault_corrupt", CoreStatusMessages.ForLog(CoreStatus.VaultCorrupt));
        Assert.Equal("storage_migration", CoreStatusMessages.ForLog(CoreStatus.StorageMigration));
        Assert.Equal("core_error", CoreStatusMessages.ForLog(CoreStatus.CoreError));
    }

    [Fact]
    public void Only_transient_failures_are_retryable()
    {
        Assert.True(CoreStatusMessages.IsRetryable(CoreStatus.CoreError));
        Assert.True(CoreStatusMessages.IsRetryable(CoreStatus.StorageLocked));
        Assert.False(CoreStatusMessages.IsRetryable(CoreStatus.VaultKeyMismatch));
        Assert.False(CoreStatusMessages.IsRetryable(CoreStatus.VaultUnreadable));
        Assert.False(CoreStatusMessages.IsRetryable(CoreStatus.VaultCorrupt));
        Assert.False(CoreStatusMessages.IsRetryable(CoreStatus.StorageMigration));
    }

    [Fact]
    public void Messages_and_categories_never_contain_paths_or_keys()
    {
        foreach (CoreStatus status in Enum.GetValues<CoreStatus>())
        {
            string[] texts =
            [
                CoreStatusMessages.ForHistoryLoad(status),
                CoreStatusMessages.ForVaultOpen(status),
                CoreStatusMessages.ForLog(status),
            ];
            foreach (string text in texts)
            {
                Assert.DoesNotContain("\\", text, StringComparison.Ordinal);
                Assert.DoesNotContain("/", text, StringComparison.Ordinal);
                Assert.DoesNotContain("history.db", text, StringComparison.Ordinal);
                Assert.DoesNotContain("x'", text, StringComparison.Ordinal);
            }
        }
    }
}
