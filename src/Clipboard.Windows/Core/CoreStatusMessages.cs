namespace Clipboard.Windows.Core;

/// <summary>
/// Fixed, non-sensitive descriptions and log categories for the core failure classes.
/// </summary>
/// <remarks>
/// Only the enum values and constant text leave this type: no path, key, SQL or clipboard content
/// is ever part of a message or a category.
/// </remarks>
internal static class CoreStatusMessages
{
    /// <summary>
    /// Reports whether the failure can plausibly clear up on its own, so callers may retry once.
    /// </summary>
    public static bool IsRetryable(CoreStatus status) =>
        status is CoreStatus.CoreError or CoreStatus.StorageLocked;

    /// <summary>
    /// Describes a history load failure for the clipboard panel.
    /// </summary>
    public static string ForHistoryLoad(CoreStatus status) => status switch
    {
        CoreStatus.StorageLocked => "剪贴板历史数据库正被占用，请稍后重试。",
        CoreStatus.VaultKeyMismatch => "剪贴板数据库与当前密钥不匹配。",
        CoreStatus.VaultUnreadable => "无法解密剪贴板数据库，可能已损坏或密钥不匹配。",
        CoreStatus.VaultCorrupt => "剪贴板历史数据库已损坏。",
        CoreStatus.StorageMigration => "剪贴板历史数据库版本不受支持。",
        _ => "无法加载剪贴板历史。",
    };

    /// <summary>
    /// Describes a vault open failure during startup for the panel status bar.
    /// </summary>
    public static string ForVaultOpen(CoreStatus status) => status switch
    {
        CoreStatus.StorageLocked => "剪贴板历史数据库正被占用，请关闭其他剪贴板实例后重试",
        CoreStatus.VaultKeyMismatch => "剪贴板数据库与当前密钥不匹配",
        CoreStatus.VaultUnreadable => "无法解密剪贴板数据库，可能已损坏或密钥不匹配",
        CoreStatus.VaultCorrupt => "剪贴板历史数据库已损坏",
        CoreStatus.StorageMigration => "剪贴板历史数据库版本不受支持",
        _ => "无法初始化剪贴板服务",
    };

    /// <summary>
    /// Maps a status onto the fixed category written to the global log.
    /// </summary>
    public static string ForLog(CoreStatus status) => status switch
    {
        CoreStatus.InvalidArgument => "invalid_argument",
        CoreStatus.InvalidJson => "invalid_json",
        CoreStatus.InvalidUtf8 => "invalid_utf8",
        CoreStatus.InvalidRegex => "invalid_regex",
        CoreStatus.Panic => "panic",
        CoreStatus.StorageLocked => "storage_locked",
        CoreStatus.VaultKeyMismatch => "vault_key_mismatch",
        CoreStatus.VaultUnreadable => "vault_unreadable",
        CoreStatus.VaultCorrupt => "vault_corrupt",
        CoreStatus.StorageMigration => "storage_migration",
        _ => "core_error",
    };
}
