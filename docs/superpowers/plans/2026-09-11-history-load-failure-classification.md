# 历史加载失败分类与恢复实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers/executing-plans to implement this plan task-by-task. Steps use checkbox (- [x]) syntax for tracking.

**目标：** 让剪贴板历史打开和加载失败按锁、密钥不匹配、无法解密、损坏、迁移失败五类可区分，并把分类接到用户提示、重试策略和脱敏日志。

**架构：** clipboard-storage 新增 vault 作用域打开入口和非秘密 `history.vault` 标记，用手写 `From<rusqlite::Error>` 把 SQLCipher 错误码分类为 `StorageError`；clipboard-core 用该入口打开数据库；clipboard-ffi 追加 `CoreStatus` 数值并在 open/execute 两处统一映射；Windows 客户端扩展枚举、提示、重试和日志类别。

**技术栈：** Rust 1.88、rusqlite 0.36 + SQLCipher、Cargo 集成测试、.NET 8、WinUI 3、xUnit。

---

## 文件结构

    crates/clipboard-storage/src/vault_marker.rs
        读写并原子写入数据目录中的 history.vault 标记。
    crates/clipboard-storage/src/error.rs
        新增锁、无法解密、损坏、vault 不匹配、标记非法、IO 变体和 rusqlite 错误码分类。
    crates/clipboard-storage/src/database.rs
        新增 Database::open_vault，保留 Database::open 与 open_in_memory。
    crates/clipboard-storage/tests/vault_open.rs
        标记建立、vault 不匹配、密钥不匹配、锁和迁移失败的存储层故障注入。
    crates/clipboard-core/src/service.rs
        CoreService::open 改用 Database::open_vault。
    crates/clipboard-ffi/src/status.rs
        追加 StorageLocked、VaultKeyMismatch、VaultUnreadable、VaultCorrupt、StorageMigration。
    crates/clipboard-ffi/src/abi.rs
        status_for_core_error 覆盖 StorageError 分类，并在 open_v2 使用。
    crates/clipboard-ffi/tests/abi.rs
        断言 open_v2 在锁、vault 不匹配、密钥不匹配和迁移失败下返回对应状态码。
    src/Clipboard.Windows/Core/CoreStatus.cs
        C# 枚举同值扩展。
    src/Clipboard.Windows/ViewModels/ClipboardPanelViewModel.cs
        分类提示、仅在 CoreError 与 StorageLocked 重试一次、失败保留历史。
    tests/Clipboard.Windows.Tests/ViewModels/ClipboardPanelViewModelTests.cs
        ViewModel 分类、重试次数、提示文本和恢复后重新加载。
    src/Clipboard.Windows/App.xaml.cs
        打开失败提示与脱敏 error_category。

### Task 1：存储层错误分类与 vault 标记

**文件：**
- Create: crates/clipboard-storage/src/vault_marker.rs
- Modify: crates/clipboard-storage/src/error.rs
- Modify: crates/clipboard-storage/src/database.rs
- Modify: crates/clipboard-storage/src/lib.rs
- Test: crates/clipboard-storage/tests/vault_open.rs

- [x] **Step 1：先写失败的存储层测试。**

在 `crates/clipboard-storage/tests/vault_open.rs` 用 `tempfile::tempdir()` 写以下用例，断言返回的 `StorageError` 变体：

    vault_marker_is_written_on_first_open_and_verified_on_reopen
    mismatched_vault_is_rejected_before_the_database_is_touched
    wrong_key_after_a_matching_marker_reports_corruption
    legacy_database_without_a_marker_reports_an_unreadable_vault
    exclusive_lock_reports_a_locked_database
    newer_schema_version_reports_a_migration_failure

每个用例使用固定 vault UUID 和固定 32 字节 key，不读取真实用户目录。

- [x] **Step 2：确认测试为红。**

运行：`cargo test -p clipboard-storage --test vault_open`

预期：编译失败或断言失败，因为 `Database::open_vault` 和新的 `StorageError` 变体尚不存在。

- [x] **Step 3：实现标记与分类。**

新增 `vault_marker.rs`：`read(data_dir)` 返回 `Option<Uuid>`，空文件和缺失文件返回 `None`，非法内容返回 `StorageError::VaultMarkerInvalid`；`write_if_absent(data_dir, vault_id)` 用临时文件加 `rename` 原子写入 `{uuid}\n`。

在 `error.rs` 增加：

    #[error(transparent)] Io(#[from] std::io::Error),
    #[error("clipboard history database is locked by another connection")] Locked,
    #[error("clipboard history database could not be decrypted")] Unreadable,
    #[error("clipboard history database is damaged")] Corrupt,
    #[error("the on-disk vault marker does not match the requested vault")] VaultMismatch,
    #[error("the on-disk vault marker is not a valid vault identifier")] VaultMarkerInvalid,

去掉 `Sqlite` 上的 `#[from]`，手写 `impl From<rusqlite::Error> for StorageError`，把 `DatabaseBusy`/`DatabaseLocked` 映射为 `Locked`、`NotADatabase` 映射为 `Unreadable`、`DatabaseCorrupt` 映射为 `Corrupt`，其余保持 `Sqlite`。

在 `database.rs` 增加 `Database::open_vault(data_dir, vault_id, key)`：先按标记判定 `VaultMismatch`；打开失败时，标记匹配或缺失下的 `Unreadable` 升级为 `Corrupt`（缺失时不升级，保持 `Unreadable`）；成功后尽力补写标记。

- [x] **Step 4：确认测试为绿。**

运行：`cargo test -p clipboard-storage --test vault_open` 与 `cargo test -p clipboard-storage`

### Task 2：Core 与 FFI 状态码

**文件：**
- Modify: crates/clipboard-core/src/service.rs
- Modify: crates/clipboard-ffi/src/status.rs
- Modify: crates/clipboard-ffi/src/abi.rs
- Test: crates/clipboard-ffi/tests/abi.rs

- [x] **Step 1：先写失败的 FFI 测试。**

在 `crates/clipboard-ffi/tests/abi.rs` 增加用例：先用 `clipboard_core_open_v2` 建立 vault，再用第二个 vault 打开同一目录断言 `VaultKeyMismatch`；用错误密钥打开断言 `VaultCorrupt`；对已锁定的数据库断言 `StorageLocked`；对更高 `schema_migrations` 版本的数据库断言 `StorageMigration`。断言状态码数值为 8、10、7、11。

- [x] **Step 2：确认测试为红。**

运行：`cargo test -p clipboard-ffi --test abi`

- [x] **Step 3：实现映射。**

`status.rs` 追加 `StorageLocked = 7`、`VaultKeyMismatch = 8`、`VaultUnreadable = 9`、`VaultCorrupt = 10`、`StorageMigration = 11`。`service.rs` 的 `CoreService::open` 改用 `Database::open_vault`。`abi.rs` 的 `status_for_core_error` 覆盖 `CoreError::Storage` 的新变体，并让 `open_impl` 在打开失败时调用它，而不是直接返回 `CoreError`。

- [x] **Step 4：确认测试为绿。**

运行：`cargo test -p clipboard-ffi` 与 `cargo test --workspace --all-targets`

### Task 3：Windows 提示、重试与日志

**文件：**
- Modify: src/Clipboard.Windows/Core/CoreStatus.cs
- Modify: src/Clipboard.Windows/ViewModels/ClipboardPanelViewModel.cs
- Modify: src/Clipboard.Windows/App.xaml.cs
- Test: tests/Clipboard.Windows.Tests/ViewModels/ClipboardPanelViewModelTests.cs

- [x] **Step 1：先写失败的 ViewModel 测试。**

在 `ClipboardPanelViewModelTests` 增加用例：`StorageLocked` 重试一次后显示占用提示；`VaultKeyMismatch`、`VaultUnreadable`、`VaultCorrupt`、`StorageMigration` 各只调用一次 Core 且显示对应提示；所有分类失败后保留上一次有效历史；把首次失败改为成功后重新加载显示新结果。

- [x] **Step 2：确认测试为红。**

运行：`dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj -c Debug -p:Platform=x64 --filter ClipboardPanelViewModelTests`

- [x] **Step 3：实现枚举、提示与重试策略。**

`CoreStatus.cs` 追加同值枚举成员。`ClipboardPanelViewModel` 把状态映射为提示文本，重试条件改为 `CoreError` 或 `StorageLocked`，其余分类立即失败并保留 `Items`。`App.InitializeAsync` 的兜底 `catch` 区分 `ClipboardCoreException.Status`，显示对应提示并把固定枚举写入 `error_category`。

- [x] **Step 4：确认测试为绿。**

运行：`dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj -c Debug -p:Platform=x64 --filter ClipboardPanelViewModelTests|ClipboardCoreClientTests`

### Task 4：整体验证与文档回填

- [x] **Step 1：运行 Rust 全量验证。**

运行：`pwsh -NoProfile -File scripts/test-core.ps1`

- [x] **Step 2：运行 Windows 发布验证（无 GUI）。**

运行：`pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke`

- [x] **Step 3：回填状态。**

更新 `docs/STATUS.md` 的 P0.2 状态与本计划复选框，记录分类状态码和测试证据；更新 `README.md` 的文档索引。

