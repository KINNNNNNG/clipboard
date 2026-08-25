# Clipboard Core Foundation Implementation Plan

> **回填状态：** 截至 2026-08-25，已按 `codex/phase4-image-sync` 的 `2e4c3f6` 回填。
> `[x]` 表示该步骤的最终交付结果可由当前代码、提交或自动测试证明；不重新声称历史红灯命令的原始输出仍可复现。
> 当前总状态见 [Clipboard 开发状态](../../STATUS.md)。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立可由 Windows 和未来 macOS 原生客户端复用的 Rust 剪贴板内核，并通过稳定 C ABI 在 .NET 冒烟程序中完成本地写入、持久化和搜索。

**Architecture:** Rust workspace 将领域模型、加密、存储、搜索、保留策略、应用服务和 FFI 分成独立 crate。SQLCipher 负责数据库整体加密，大对象使用 XChaCha20-Poly1305；C ABI 通过显式长度缓冲区和版本化 JSON 命令隔离 C#/Rust 所有权。

**Tech Stack:** Rust 1.88、edition 2024、Cargo、serde、uuid、regex、rusqlite/SQLCipher、HKDF-SHA-256、XChaCha20-Poly1305、proptest、.NET 8 C# 控制台冒烟程序、PowerShell 7。

---

## 文件结构

本计划创建以下结构：

```text
Cargo.toml                              Rust workspace 和统一依赖版本
Cargo.lock                              首次成功解析后提交的依赖锁
rust-toolchain.toml                     固定 Rust 1.88 工具链
global.json                             固定 .NET 8 SDK 大版本
.editorconfig                           Rust/C#/Markdown 基础格式
scripts/verify-toolchain.ps1            工具链前置检查
scripts/test-core.ps1                   阶段 1 单一验证入口
crates/clipboard-domain/                纯领域类型和合并规则
crates/clipboard-crypto/                密钥派生和对象认证加密
crates/clipboard-storage/               SQLCipher、迁移和 outbox
crates/clipboard-search/                子串、正则和筛选
crates/clipboard-core/                  用例编排和命令协议
crates/clipboard-ffi/                   稳定 C ABI 动态库
src/Clipboard.FfiSmoke/                 .NET 8 FFI 冒烟程序
tests/fixtures/                         非敏感固定测试数据
```

每个 Rust 源文件只承担一个职责；不得建立包含全部逻辑的 `utils.rs`、`service.rs` 或单一巨型 `lib.rs`。

### Task 0: 固定并验证工具链

**Files:**
- Create: `rust-toolchain.toml`
- Create: `global.json`
- Create: `.editorconfig`
- Create: `scripts/verify-toolchain.ps1`

- [x] **Step 1: 写入工具链版本文件**

```toml
# rust-toolchain.toml
[toolchain]
channel = "1.88.0"
profile = "minimal"
targets = ["x86_64-pc-windows-msvc"]
components = ["clippy", "rustfmt"]
```

```json
{
  "sdk": {
    "version": "8.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

```ini
root = true

[*]
charset = utf-8
end_of_line = crlf
insert_final_newline = true
indent_style = space
indent_size = 4

[*.{yml,yaml,json,toml}]
indent_size = 2

[*.rs]
indent_size = 4
```

- [x] **Step 2: 写入会在当前机器失败的前置检查**

```powershell
# scripts/verify-toolchain.ps1
$ErrorActionPreference = 'Stop'

$rustVersion = (& rustc --version).Trim()
if ($rustVersion -notmatch '^rustc 1\.88\.') {
    throw "Rust 1.88.x is required; found: $rustVersion"
}

$sdkOutput = & dotnet --list-sdks
if (-not ($sdkOutput | Select-String '^8\.0\.')) {
    throw '.NET 8 SDK is required; no 8.0 SDK was found.'
}

$targetList = & rustup target list --installed
if ($targetList -notcontains 'x86_64-pc-windows-msvc') {
    throw 'Rust target x86_64-pc-windows-msvc is required.'
}

Write-Host 'Toolchain verification passed.'
```

- [x] **Step 3: 运行检查并确认当前环境失败原因**

Run: `pwsh -NoProfile -File scripts/verify-toolchain.ps1`

Expected before setup: FAIL，指出当前 Rust 为 `1.73.0`，并且没有 .NET 8 SDK。不得把“只有 .NET 8 Runtime”当作 SDK 通过。

- [x] **Step 4: 安装缺失工具链**

Run: `rustup toolchain install 1.88.0-x86_64-pc-windows-msvc --profile minimal --component clippy,rustfmt`

Expected: `1.88.0-x86_64-pc-windows-msvc installed`。

Run: `winget install --id Microsoft.DotNet.SDK.8 --exact --accept-package-agreements --accept-source-agreements`

Expected: .NET 8 SDK 安装成功。若系统要求管理员确认，只批准该精确包，不安装预览 SDK。

- [x] **Step 5: 重新运行检查**

Run: `pwsh -NoProfile -File scripts/verify-toolchain.ps1`

Expected: `Toolchain verification passed.`

- [x] **Step 6: 提交工具链基线**

```powershell
git add rust-toolchain.toml global.json .editorconfig scripts/verify-toolchain.ps1
git commit -m "build: pin core toolchains"
```

### Task 1: 创建 Rust workspace 和空 crate 边界

**Files:**
- Create: `Cargo.toml`
- Create: `crates/clipboard-domain/Cargo.toml`
- Create: `crates/clipboard-domain/src/lib.rs`
- Create: `crates/clipboard-crypto/Cargo.toml`
- Create: `crates/clipboard-crypto/src/lib.rs`
- Create: `crates/clipboard-storage/Cargo.toml`
- Create: `crates/clipboard-storage/src/lib.rs`
- Create: `crates/clipboard-search/Cargo.toml`
- Create: `crates/clipboard-search/src/lib.rs`
- Create: `crates/clipboard-core/Cargo.toml`
- Create: `crates/clipboard-core/src/lib.rs`
- Create: `crates/clipboard-ffi/Cargo.toml`
- Create: `crates/clipboard-ffi/src/lib.rs`

- [x] **Step 1: 写 workspace 清单**

```toml
[workspace]
resolver = "2"
members = [
  "crates/clipboard-domain",
  "crates/clipboard-crypto",
  "crates/clipboard-storage",
  "crates/clipboard-search",
  "crates/clipboard-core",
  "crates/clipboard-ffi",
]

[workspace.package]
version = "0.1.0"
edition = "2024"
license = "MIT"
rust-version = "1.88"

[workspace.dependencies]
argon2 = "0.5"
base64 = "0.22"
blake3 = "1.8"
chacha20poly1305 = "0.10"
hex = "0.4"
hkdf = "0.12"
proptest = "1.7"
rand_core = { version = "0.6", features = ["getrandom"] }
regex = "1.11"
rusqlite = { version = "0.36", features = ["bundled-sqlcipher-vendored-openssl", "uuid"] }
serde = { version = "1.0", features = ["derive"] }
serde_json = "1.0"
sha2 = "0.10"
tempfile = "3.20"
thiserror = "2.0"
uuid = { version = "1.17", features = ["serde", "v4", "v7"] }
zeroize = { version = "1.8", features = ["derive"] }
```

- [x] **Step 2: 为每个 crate 写完整最小清单**

```toml
# crates/clipboard-domain/Cargo.toml
[package]
name = "clipboard-domain"
version.workspace = true
edition.workspace = true
license.workspace = true
rust-version.workspace = true

[dependencies]
serde.workspace = true
thiserror.workspace = true
uuid.workspace = true

[dev-dependencies]
proptest.workspace = true
```

```toml
# crates/clipboard-crypto/Cargo.toml
[package]
name = "clipboard-crypto"
version.workspace = true
edition.workspace = true
license.workspace = true
rust-version.workspace = true

[dependencies]
chacha20poly1305.workspace = true
hkdf.workspace = true
rand_core.workspace = true
sha2.workspace = true
thiserror.workspace = true
uuid.workspace = true
zeroize.workspace = true
```

```toml
# crates/clipboard-storage/Cargo.toml
[package]
name = "clipboard-storage"
version.workspace = true
edition.workspace = true
license.workspace = true
rust-version.workspace = true

[dependencies]
clipboard-crypto = { path = "../clipboard-crypto" }
clipboard-domain = { path = "../clipboard-domain" }
hex.workspace = true
rusqlite.workspace = true
serde_json.workspace = true
thiserror.workspace = true
uuid.workspace = true

[dev-dependencies]
tempfile.workspace = true
```

```toml
# crates/clipboard-search/Cargo.toml
[package]
name = "clipboard-search"
version.workspace = true
edition.workspace = true
license.workspace = true
rust-version.workspace = true

[dependencies]
clipboard-domain = { path = "../clipboard-domain" }
regex.workspace = true
serde.workspace = true
thiserror.workspace = true
```

```toml
# crates/clipboard-core/Cargo.toml
[package]
name = "clipboard-core"
version.workspace = true
edition.workspace = true
license.workspace = true
rust-version.workspace = true

[dependencies]
clipboard-crypto = { path = "../clipboard-crypto" }
clipboard-domain = { path = "../clipboard-domain" }
clipboard-search = { path = "../clipboard-search" }
clipboard-storage = { path = "../clipboard-storage" }
serde.workspace = true
serde_json.workspace = true
thiserror.workspace = true
uuid.workspace = true

[dev-dependencies]
tempfile.workspace = true
```

```toml
# crates/clipboard-ffi/Cargo.toml
[package]
name = "clipboard-ffi"
version.workspace = true
edition.workspace = true
license.workspace = true
rust-version.workspace = true

[lib]
crate-type = ["cdylib", "rlib"]

[dependencies]
clipboard-core = { path = "../clipboard-core" }
serde_json.workspace = true
thiserror.workspace = true

[dev-dependencies]
tempfile.workspace = true
```

每个 `src/lib.rs` 先包含以下可编译内容，不加入业务逻辑：

```rust
#![forbid(unsafe_code)]

pub const CRATE_READY: bool = true;
```

`clipboard-ffi` 例外，因为后续 C ABI 需要受审计的 `unsafe`：

```rust
#![deny(unsafe_op_in_unsafe_fn)]

pub const CRATE_READY: bool = true;
```

- [x] **Step 3: 格式化并验证 workspace**

Run: `cargo fmt --all --check`

Expected: exit 0。

Run: `cargo test --workspace --all-targets`

Expected: 6 个 crate 编译成功，测试结果均为 `ok`。

- [x] **Step 4: 提交 workspace**

```powershell
git add Cargo.toml Cargo.lock crates
git commit -m "build: scaffold clipboard core workspace"
```

### Task 2: 建立剪贴板领域模型和本机文件同步边界

**Files:**
- Create: `crates/clipboard-domain/src/item.rs`
- Create: `crates/clipboard-domain/src/file_bundle.rs`
- Modify: `crates/clipboard-domain/src/lib.rs`
- Test: `crates/clipboard-domain/tests/item_model.rs`

- [x] **Step 1: 先写领域模型失败测试**

```rust
use clipboard_domain::{ClipboardContent, ClipboardItem, FileBundle, FileEntry, SyncScope};
use uuid::Uuid;

#[test]
fn file_bundle_is_always_local_only() {
    let bundle = FileBundle::new(vec![FileEntry::file(
        r"C:\work\report.docx".into(),
        42,
        1_725_000_000_000,
    )]).unwrap();

    let item = ClipboardItem::new(
        Uuid::now_v7(),
        Uuid::new_v4(),
        ClipboardContent::FileBundle(bundle),
        "explorer.exe".into(),
        1_725_000_000_000,
    );

    assert_eq!(item.sync_scope(), SyncScope::LocalOnly);
    assert!(!item.is_syncable());
}

#[test]
fn text_and_image_are_vault_scoped() {
    let vault = Uuid::new_v4();
    let text = ClipboardItem::new(
        Uuid::now_v7(), vault, ClipboardContent::Text("hello".into()),
        "notepad.exe".into(), 1_725_000_000_000,
    );
    assert_eq!(text.sync_scope(), SyncScope::Vault);
}
```

- [x] **Step 2: 运行测试确认失败**

Run: `cargo test -p clipboard-domain --test item_model`

Expected: FAIL，`ClipboardItem`、`FileBundle` 等类型尚不存在。

- [x] **Step 3: 实现最小领域类型**

```rust
// crates/clipboard-domain/src/file_bundle.rs
use serde::{Deserialize, Serialize};
use thiserror::Error;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum FileEntryKind { File, Directory }

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileEntry {
    pub path: String,
    pub kind: FileEntryKind,
    pub size: u64,
    pub modified_ms: i64,
}

impl FileEntry {
    pub fn file(path: String, size: u64, modified_ms: i64) -> Self {
        Self { path, kind: FileEntryKind::File, size, modified_ms }
    }

    pub fn directory(path: String, modified_ms: i64) -> Self {
        Self { path, kind: FileEntryKind::Directory, size: 0, modified_ms }
    }
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum FileBundleError { #[error("file bundle cannot be empty")] Empty }

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileBundle { pub entries: Vec<FileEntry> }

impl FileBundle {
    pub fn new(entries: Vec<FileEntry>) -> Result<Self, FileBundleError> {
        if entries.is_empty() { return Err(FileBundleError::Empty); }
        Ok(Self { entries })
    }
}
```

```rust
// crates/clipboard-domain/src/item.rs
use crate::FileBundle;
use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SyncScope { Vault, LocalOnly }

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum ClipboardContent {
    Text(String),
    Image { object_id: Uuid, width: u32, height: u32, bytes: u64 },
    FileBundle(FileBundle),
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ClipboardItem {
    pub id: Uuid,
    pub vault_id: Uuid,
    pub content: ClipboardContent,
    pub source_app: String,
    pub created_ms: i64,
    pub last_used_ms: i64,
}

impl ClipboardItem {
    pub fn new(id: Uuid, vault_id: Uuid, content: ClipboardContent, source_app: String, created_ms: i64) -> Self {
        Self { id, vault_id, content, source_app, created_ms, last_used_ms: created_ms }
    }

    pub fn sync_scope(&self) -> SyncScope {
        match &self.content { ClipboardContent::FileBundle(_) => SyncScope::LocalOnly, _ => SyncScope::Vault }
    }

    pub fn is_syncable(&self) -> bool { self.sync_scope() == SyncScope::Vault }
}
```

`lib.rs` 明确声明 `mod file_bundle; mod item;`，并重新导出 `ClipboardContent`、`ClipboardItem`、`FileBundle`、`FileBundleError`、`FileEntry`、`FileEntryKind` 和 `SyncScope`。

- [x] **Step 4: 运行领域测试**

Run: `cargo test -p clipboard-domain --test item_model`

Expected: 2 tests PASS。

- [x] **Step 5: 提交领域模型**

```powershell
git add crates/clipboard-domain
git commit -m "feat(core): add clipboard domain model"
```

### Task 3: 实现混合逻辑时钟和确定性状态合并

**Files:**
- Create: `crates/clipboard-domain/src/hlc.rs`
- Create: `crates/clipboard-domain/src/state.rs`
- Modify: `crates/clipboard-domain/src/lib.rs`
- Test: `crates/clipboard-domain/tests/merge_properties.rs`

- [x] **Step 1: 写失败的性质测试**

```rust
use clipboard_domain::{FavoriteState, Hlc};
use proptest::prelude::*;
use uuid::Uuid;

fn state(value: bool, physical_ms: i64, logical: u32, node: u128) -> FavoriteState {
    FavoriteState { value, updated: Hlc::new(physical_ms, logical, Uuid::from_u128(node)) }
}

proptest! {
    #[test]
    fn favorite_merge_is_commutative(a_value in any::<bool>(), b_value in any::<bool>(), a_time in 0i64..10_000, b_time in 0i64..10_000) {
        let a = state(a_value, a_time, 0, 1);
        let b = state(b_value, b_time, 0, 2);
        prop_assert_eq!(a.merge(&b), b.merge(&a));
    }

    #[test]
    fn favorite_merge_is_idempotent(value in any::<bool>(), time in 0i64..10_000) {
        let a = state(value, time, 0, 1);
        prop_assert_eq!(a.merge(&a), a);
    }
}
```

- [x] **Step 2: 运行测试确认失败**

Run: `cargo test -p clipboard-domain --test merge_properties`

Expected: FAIL，`Hlc::new` 或 `merge` 未定义。

- [x] **Step 3: 实现全序 HLC 和最后写入合并**

```rust
// crates/clipboard-domain/src/hlc.rs
use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub struct Hlc {
    pub physical_ms: i64,
    pub logical: u32,
    pub node_id: Uuid,
}

impl Hlc {
    pub const fn new(physical_ms: i64, logical: u32, node_id: Uuid) -> Self {
        Self { physical_ms, logical, node_id }
    }
}
```

```rust
// crates/clipboard-domain/src/state.rs
use crate::Hlc;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct FavoriteState { pub value: bool, pub updated: Hlc }

impl FavoriteState {
    pub fn merge(&self, other: &Self) -> Self {
        if other.updated > self.updated { *other } else { *self }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct DeleteState { pub deleted: bool, pub updated: Hlc }

impl DeleteState {
    pub fn merge(&self, other: &Self) -> Self {
        if other.updated > self.updated { *other } else { *self }
    }
}
```

- [x] **Step 4: 运行全部领域测试**

Run: `cargo test -p clipboard-domain`

Expected: item model 与性质测试全部 PASS。

- [x] **Step 5: 提交合并规则**

```powershell
git add crates/clipboard-domain
git commit -m "feat(core): add deterministic clipboard state merging"
```

### Task 4: 实现密钥派生和对象认证加密

**Files:**
- Create: `crates/clipboard-crypto/src/keys.rs`
- Create: `crates/clipboard-crypto/src/object_cipher.rs`
- Create: `crates/clipboard-crypto/src/error.rs`
- Modify: `crates/clipboard-crypto/src/lib.rs`
- Test: `crates/clipboard-crypto/tests/object_cipher.rs`

- [x] **Step 1: 写失败的加密测试**

```rust
use clipboard_crypto::{KeyPurpose, ObjectCipher, VaultKey};
use uuid::Uuid;

#[test]
fn ciphertext_round_trips_and_detects_tampering() {
    let vault_id = Uuid::from_u128(7);
    let key = VaultKey::from_bytes([0x42; 32]);
    let cipher = ObjectCipher::new(key.derive(vault_id, KeyPurpose::Image).unwrap());
    let aad = b"vault=7;object=9;type=image;version=1";

    let mut sealed = cipher.seal(b"clipboard image bytes", aad).unwrap();
    assert_eq!(cipher.open(&sealed, aad).unwrap(), b"clipboard image bytes");

    *sealed.last_mut().unwrap() ^= 0x01;
    assert!(cipher.open(&sealed, aad).is_err());
}
```

- [x] **Step 2: 运行测试确认失败**

Run: `cargo test -p clipboard-crypto --test object_cipher`

Expected: FAIL，公开加密类型未定义。

- [x] **Step 3: 实现零化主密钥、HKDF 和 XChaCha20-Poly1305**

`VaultKey` 必须使用 `Zeroize`/`ZeroizeOnDrop`，并且只提供按用途派生方法。`KeyPurpose` 使用固定 ASCII 标签：`database-v1`、`journal-v1`、`image-v1`、`file-cache-v1`、`fingerprint-v1`。`ObjectCipher::seal` 输出格式固定为：1 字节版本、24 字节 nonce、密文和 16 字节 tag。

核心实现：

```rust
let hkdf = hkdf::Hkdf::<sha2::Sha256>::new(Some(vault_id.as_bytes()), self.as_bytes());
hkdf.expand(purpose.label(), &mut output).map_err(|_| CryptoError::KeyDerivation)?;

let nonce = chacha20poly1305::XNonce::from_slice(&nonce_bytes);
let payload = chacha20poly1305::aead::Payload { msg: plaintext, aad };
let ciphertext = cipher.encrypt(nonce, payload).map_err(|_| CryptoError::Authentication)?;
```

随机 nonce 使用 `OsRng`，不得从时间戳、UUID 或对象 ID 派生。

- [x] **Step 4: 运行加密测试和 Clippy**

Run: `cargo test -p clipboard-crypto`

Expected: round-trip PASS，篡改测试返回认证错误。

Run: `cargo clippy -p clipboard-crypto --all-targets -- -D warnings`

Expected: exit 0。

- [x] **Step 5: 提交加密基元**

```powershell
git add crates/clipboard-crypto
git commit -m "feat(core): add vault key derivation and object encryption"
```

### Task 5: 建立 SQLCipher 数据库、迁移和 outbox 拒绝规则

**Files:**
- Create: `crates/clipboard-storage/migrations/001_initial.sql`
- Create: `crates/clipboard-storage/src/database.rs`
- Create: `crates/clipboard-storage/src/item_repository.rs`
- Create: `crates/clipboard-storage/src/outbox.rs`
- Create: `crates/clipboard-storage/src/error.rs`
- Modify: `crates/clipboard-storage/src/lib.rs`
- Test: `crates/clipboard-storage/tests/encrypted_database.rs`
- Test: `crates/clipboard-storage/tests/local_only_outbox.rs`

- [x] **Step 1: 写错误密钥和文件拒绝测试**

```rust
use clipboard_domain::{ClipboardContent, ClipboardItem, FileBundle, FileEntry};
use clipboard_storage::{Database, OutboxError};
use tempfile::tempdir;
use uuid::Uuid;

#[test]
fn wrong_database_key_cannot_read_schema() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("history.db");
    Database::open(&path, &[0x11; 32]).unwrap();
    assert!(Database::open(&path, &[0x22; 32]).is_err());
}

#[test]
fn local_file_bundle_cannot_enter_outbox() {
    let db = Database::open_in_memory(&[0x11; 32]).unwrap();
    let bundle = FileBundle::new(vec![FileEntry::file("C:\\a.txt".into(), 1, 1)]).unwrap();
    let item = ClipboardItem::new(Uuid::now_v7(), Uuid::new_v4(), ClipboardContent::FileBundle(bundle), "explorer.exe".into(), 1);
    db.items().insert(&item).unwrap();
    assert_eq!(db.outbox().enqueue_item(&item), Err(OutboxError::LocalOnly));
}
```

- [x] **Step 2: 运行测试确认失败**

Run: `cargo test -p clipboard-storage --test encrypted_database --test local_only_outbox`

Expected: FAIL，`Database` 和仓储 API 未定义。

- [x] **Step 3: 写数据库迁移**

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_at_ms INTEGER NOT NULL
);

CREATE TABLE clipboard_items (
    id BLOB PRIMARY KEY CHECK(length(id) = 16),
    vault_id BLOB NOT NULL CHECK(length(vault_id) = 16),
    kind TEXT NOT NULL CHECK(kind IN ('text', 'image', 'file_bundle')),
    sync_scope TEXT NOT NULL CHECK(sync_scope IN ('vault', 'local_only')),
    source_app TEXT NOT NULL,
    created_ms INTEGER NOT NULL,
    last_used_ms INTEGER NOT NULL,
    content_json TEXT NOT NULL,
    favorite_json TEXT,
    delete_json TEXT
);

CREATE TABLE sync_outbox (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    item_id BLOB NOT NULL REFERENCES clipboard_items(id) ON DELETE CASCADE,
    event_json TEXT NOT NULL,
    created_ms INTEGER NOT NULL
);

CREATE TRIGGER reject_local_only_outbox
BEFORE INSERT ON sync_outbox
WHEN (SELECT sync_scope FROM clipboard_items WHERE id = NEW.item_id) <> 'vault'
BEGIN
    SELECT RAISE(ABORT, 'local_only item cannot enter sync outbox');
END;
```

- [x] **Step 4: 实现数据库打开顺序和仓储**

`Database::open` 必须在执行任何模式查询前设置 SQLCipher key：

```rust
let connection = rusqlite::Connection::open(path)?;
connection.pragma_update(None, "key", format!("x'{}'", hex_key(key)))?;
let cipher_version: String = connection.query_row("PRAGMA cipher_version", [], |row| row.get(0))?;
if cipher_version.is_empty() { return Err(StorageError::CipherUnavailable); }
connection.execute_batch(include_str!("../migrations/001_initial.sql"))?;
```

`ItemRepository::insert` 使用显式事务写入 `clipboard_items`。`OutboxRepository::enqueue_item` 先检查 `item.is_syncable()` 返回领域错误，再依赖数据库 trigger 做第二道防线。

- [x] **Step 5: 运行存储测试**

Run: `cargo test -p clipboard-storage`

Expected: 错误密钥测试 PASS；文件 outbox 返回 `OutboxError::LocalOnly`；数据库 trigger 直接插入时也拒绝。

- [x] **Step 6: 提交加密存储**

```powershell
git add crates/clipboard-storage
git commit -m "feat(core): add encrypted clipboard storage"
```

### Task 6: 实现即时子串、正则和文件路径搜索

**Files:**
- Create: `crates/clipboard-search/src/query.rs`
- Create: `crates/clipboard-search/src/engine.rs`
- Create: `crates/clipboard-search/src/error.rs`
- Modify: `crates/clipboard-search/src/lib.rs`
- Test: `crates/clipboard-search/tests/search.rs`

- [x] **Step 1: 写文本、文件路径和无效正则测试**

```rust
use clipboard_search::{SearchEngine, SearchMode, SearchQuery};

#[test]
fn substring_search_matches_chinese_text_case_insensitively() {
    let engine = SearchEngine::from_documents([("1", "项目部署地址", ""), ("2", "HELLO Codex", "")]);
    assert_eq!(engine.search(&SearchQuery::new("hello", SearchMode::Substring)).unwrap(), vec!["2"]);
    assert_eq!(engine.search(&SearchQuery::new("部署", SearchMode::Substring)).unwrap(), vec!["1"]);
}

#[test]
fn regex_search_matches_local_file_path() {
    let engine = SearchEngine::from_documents([("f1", "report.docx", r"C:\work\2026\report.docx")]);
    assert_eq!(engine.search(&SearchQuery::new(r"2026\\.*\.docx$", SearchMode::Regex)).unwrap(), vec!["f1"]);
}

#[test]
fn invalid_regex_is_reported_without_replacing_previous_results() {
    let engine = SearchEngine::from_documents([("1", "clipboard", "")]);
    assert!(engine.search(&SearchQuery::new("[", SearchMode::Regex)).is_err());
}
```

- [x] **Step 2: 运行测试确认失败**

Run: `cargo test -p clipboard-search --test search`

Expected: FAIL，搜索 API 未定义。

- [x] **Step 3: 实现搜索引擎**

普通搜索为 `text.to_lowercase().contains(&pattern.to_lowercase())`；正则使用：

```rust
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Deserialize, serde::Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SearchMode {
    Substring,
    Regex,
}

let regex = regex::RegexBuilder::new(&query.pattern)
    .case_insensitive(true)
    .size_limit(2 * 1024 * 1024)
    .dfa_size_limit(4 * 1024 * 1024)
    .build()
    .map_err(|error| SearchError::InvalidRegex(error.to_string()))?;
```

每个文档只组合可搜索正文、文件名和本机路径。图片不得加入 OCR 文本。结果保持输入的最近使用排序，不在搜索层重新打乱。

- [x] **Step 4: 运行搜索测试**

Run: `cargo test -p clipboard-search`

Expected: 3 tests PASS。

- [x] **Step 5: 提交搜索能力**

```powershell
git add crates/clipboard-search
git commit -m "feat(core): add clipboard text and path search"
```

### Task 7: 实现条数、天数和收藏豁免保留计划

**Files:**
- Create: `crates/clipboard-domain/src/retention.rs`
- Modify: `crates/clipboard-domain/src/lib.rs`
- Test: `crates/clipboard-domain/tests/retention.rs`

- [x] **Step 1: 写保留策略失败测试**

```rust
use clipboard_domain::{RetentionCandidate, RetentionPolicy, plan_retention};
use uuid::Uuid;

#[test]
fn favorites_are_never_auto_deleted_and_oldest_regular_items_are_removed() {
    let favorite = RetentionCandidate::new(Uuid::from_u128(1), 1, true, true);
    let oldest = RetentionCandidate::new(Uuid::from_u128(2), 2, false, false);
    let newest = RetentionCandidate::new(Uuid::from_u128(3), 3, false, true);
    let policy = RetentionPolicy { max_regular_items: Some(1), max_age_days: None };

    let plan = plan_retention(&policy, 100, &[favorite, oldest, newest]);
    assert_eq!(plan.delete_local, vec![Uuid::from_u128(2)]);
    assert!(plan.create_tombstones.is_empty());
}
```

参数中的最后一个布尔值表示是否可同步；本机文件删除只能进入 `delete_local`，文本/图片才进入 `create_tombstones`。

- [x] **Step 2: 运行测试确认失败**

Run: `cargo test -p clipboard-domain --test retention`

Expected: FAIL，保留策略类型未定义。

- [x] **Step 3: 实现纯函数保留计划**

实现要求：

```rust
pub struct RetentionPolicy {
    pub max_regular_items: Option<usize>,
    pub max_age_days: Option<u32>,
}

pub struct RetentionPlan {
    pub delete_local: Vec<Uuid>,
    pub create_tombstones: Vec<Uuid>,
}
```

算法先排除收藏项，再按 `last_used_ms` 升序选出过期项，最后对剩余普通项执行最大条数限制。相同记录只出现一次；可同步项进入墓碑列表，本机文件进入本地删除列表。

- [x] **Step 4: 运行领域测试**

Run: `cargo test -p clipboard-domain`

Expected: 保留、合并和模型测试全部 PASS。

- [x] **Step 5: 提交保留计划**

```powershell
git add crates/clipboard-domain
git commit -m "feat(core): add clipboard retention planning"
```

### Task 8: 组合本地 Core 命令服务

**Files:**
- Create: `crates/clipboard-core/src/command.rs`
- Create: `crates/clipboard-core/src/response.rs`
- Create: `crates/clipboard-core/src/service.rs`
- Create: `crates/clipboard-core/src/error.rs`
- Modify: `crates/clipboard-core/src/lib.rs`
- Test: `crates/clipboard-core/tests/local_workflow.rs`

- [x] **Step 1: 写本地纵向流程失败测试**

```rust
use clipboard_core::{CoreCommand, CoreService, IngestText, SearchRequest};
use clipboard_search::SearchMode;
use tempfile::tempdir;
use uuid::Uuid;

#[test]
fn ingest_persist_reopen_and_search_text() {
    let dir = tempdir().unwrap();
    let vault = Uuid::new_v4();
    let key = [0x31; 32];
    {
        let mut core = CoreService::open(dir.path(), vault, &key).unwrap();
        core.execute(CoreCommand::IngestText(IngestText {
            text: "跨设备剪贴板".into(), source_app: "notepad.exe".into(), captured_ms: 10,
        })).unwrap();
    }
    let mut reopened = CoreService::open(dir.path(), vault, &key).unwrap();
    let response = reopened.execute(CoreCommand::Search(SearchRequest {
        pattern: "设备".into(), mode: SearchMode::Substring,
    })).unwrap();
    assert_eq!(response.search_items().len(), 1);
}
```

- [x] **Step 2: 运行测试确认失败**

Run: `cargo test -p clipboard-core --test local_workflow`

Expected: FAIL，Core 命令服务未定义。

- [x] **Step 3: 实现版本化命令和服务**

命令使用内部强类型枚举，FFI 序列化时使用 `api_version: 1` 和 `#[serde(tag = "type", content = "payload", rename_all = "snake_case")]`：

```rust
#[derive(Debug, serde::Deserialize)]
#[serde(tag = "type", content = "payload", rename_all = "snake_case")]
pub enum CoreCommand {
    IngestText(IngestText),
    Search(SearchRequest),
    SetFavorite(SetFavorite),
    Delete(DeleteRequest),
    ApplyRetention,
}
```

响应使用不带额外包装层的对象，确保 C# 可以稳定读取根节点字段：

```rust
#[derive(Debug, serde::Serialize)]
#[serde(untagged)]
pub enum CoreResponse {
    Mutation { item_id: uuid::Uuid },
    Search { items: Vec<SearchItem> },
    Retention { deleted_local: usize, tombstones_created: usize },
    Empty {},
}

impl CoreResponse {
    pub fn search_items(&self) -> &[SearchItem] {
        match self {
            Self::Search { items } => items,
            _ => &[],
        }
    }
}

#[derive(Debug, serde::Serialize)]
pub struct SearchItem {
    pub id: uuid::Uuid,
    pub kind: String,
    pub preview: String,
    pub source_app: String,
    pub last_used_ms: i64,
}

#[derive(Debug, thiserror::Error)]
pub enum CoreError {
    #[error("unsupported API version: {0}")]
    UnsupportedApiVersion(u32),
    #[error(transparent)]
    Storage(#[from] clipboard_storage::StorageError),
    #[error(transparent)]
    Search(#[from] clipboard_search::SearchError),
    #[error("invalid command: {0}")]
    InvalidCommand(String),
}
```

`CoreService::execute` 只做用例编排：校验命令、调用仓储、调用搜索/保留纯函数、在事务中写 outbox。它不得包含 SQL 字符串、正则构建或加密算法。

- [x] **Step 4: 运行 core 纵向测试**

Run: `cargo test -p clipboard-core --test local_workflow`

Expected: 写入、关闭、重开和搜索 PASS。

Run: `cargo test --workspace`

Expected: 全 workspace PASS。

- [x] **Step 5: 提交 Core 服务**

```powershell
git add crates/clipboard-core
git commit -m "feat(core): add local clipboard command service"
```

### Task 9: 暴露稳定 C ABI 并建立 .NET 冒烟程序

**Files:**
- Modify: `crates/clipboard-ffi/Cargo.toml`
- Create: `crates/clipboard-ffi/src/abi.rs`
- Create: `crates/clipboard-ffi/src/buffer.rs`
- Create: `crates/clipboard-ffi/src/status.rs`
- Modify: `crates/clipboard-ffi/src/lib.rs`
- Test: `crates/clipboard-ffi/tests/abi.rs`
- Create: `src/Clipboard.FfiSmoke/Clipboard.FfiSmoke.csproj`
- Create: `src/Clipboard.FfiSmoke/NativeMethods.cs`
- Create: `src/Clipboard.FfiSmoke/Program.cs`

- [x] **Step 1: 写 Rust ABI 失败测试**

```rust
use clipboard_ffi::{clipboard_core_close, clipboard_core_execute, clipboard_core_free_buffer, clipboard_core_open, CoreBuffer, CoreHandle, CoreStatus};
use std::ptr;
use tempfile::tempdir;

#[test]
fn abi_opens_executes_and_frees_response() {
    let dir = tempdir().unwrap();
    let path = dir.path().to_string_lossy();
    let key = [0x55u8; 32];
    let mut handle: *mut CoreHandle = ptr::null_mut();
    assert_eq!(unsafe { clipboard_core_open(path.as_ptr(), path.len(), key.as_ptr(), key.len(), &mut handle) }, CoreStatus::Ok);

    let request = br#"{"api_version":1,"type":"search","payload":{"pattern":"x","mode":"substring"}}"#;
    let mut response = CoreBuffer::default();
    assert_eq!(unsafe { clipboard_core_execute(handle, request.as_ptr(), request.len(), &mut response) }, CoreStatus::Ok);
    unsafe { clipboard_core_free_buffer(response); clipboard_core_close(handle); }
}
```

- [x] **Step 2: 运行 ABI 测试确认失败**

Run: `cargo test -p clipboard-ffi --test abi`

Expected: FAIL，C ABI 符号未定义。

- [x] **Step 3: 实现固定 ABI**

`clipboard-ffi` 设置 `crate-type = ["cdylib", "rlib"]`。只导出以下函数：

```rust
#[repr(i32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CoreStatus {
    Ok = 0,
    InvalidArgument = 1,
    InvalidUtf8 = 2,
    InvalidJson = 3,
    CoreError = 4,
    Panic = 5,
}

#[repr(C)]
#[derive(Debug, Default)]
pub struct CoreBuffer {
    pub ptr: *mut u8,
    pub len: usize,
    pub capacity: usize,
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_open(
    data_dir_ptr: *const u8,
    data_dir_len: usize,
    vault_key_ptr: *const u8,
    vault_key_len: usize,
    out_handle: *mut *mut CoreHandle,
) -> CoreStatus;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_execute(
    handle: *mut CoreHandle,
    request_ptr: *const u8,
    request_len: usize,
    out_response: *mut CoreBuffer,
) -> CoreStatus;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_free_buffer(buffer: CoreBuffer);

#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_close(handle: *mut CoreHandle);
```

所有指针先验证 null，`vault_key_len` 必须恰好为 32。panic 使用 `catch_unwind` 转换为 `CoreStatus::Panic`，不能跨 FFI。`CoreBuffer { ptr, len, capacity }` 只能由 `clipboard_core_free_buffer` 释放。

- [x] **Step 4: 运行 Rust ABI 测试**

Run: `cargo test -p clipboard-ffi --test abi`

Expected: open/execute/free/close PASS；另补 null 指针、错误 key 长度、无效 UTF-8 和无效 JSON 测试并全部 PASS。

- [x] **Step 5: 写 .NET 8 P/Invoke 冒烟程序**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <None Include="..\..\target\debug\clipboard_ffi.dll"
          Link="clipboard_ffi.dll"
          CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

```csharp
// src/Clipboard.FfiSmoke/NativeMethods.cs
using System.Runtime.InteropServices;

internal enum CoreStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidUtf8 = 2,
    InvalidJson = 3,
    CoreError = 4,
    Panic = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct CoreBuffer
{
    public nint Pointer;
    public nuint Length;
    public nuint Capacity;
}

internal static partial class NativeMethods
{
    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_core_open(
        byte* dataDirectory,
        nuint dataDirectoryLength,
        byte* vaultKey,
        nuint vaultKeyLength,
        out nint handle);

    [LibraryImport("clipboard_ffi")]
    internal static unsafe partial CoreStatus clipboard_core_execute(
        nint handle,
        byte* request,
        nuint requestLength,
        out CoreBuffer response);

    [LibraryImport("clipboard_ffi")]
    internal static partial void clipboard_core_free_buffer(CoreBuffer buffer);

    [LibraryImport("clipboard_ffi")]
    internal static partial void clipboard_core_close(nint handle);
}
```

```csharp
// src/Clipboard.FfiSmoke/Program.cs
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

byte[] key = Enumerable.Repeat((byte)0x55, 32).ToArray();
string dataDirectory = Path.Combine(Path.GetTempPath(), $"clipboard-ffi-{Guid.NewGuid():N}");
Directory.CreateDirectory(dataDirectory);
nint handle = 0;

try
{
    byte[] pathBytes = Encoding.UTF8.GetBytes(dataDirectory);
    unsafe
    {
        fixed (byte* pathPointer = pathBytes)
        fixed (byte* keyPointer = key)
        {
            RequireOk(NativeMethods.clipboard_core_open(
                pathPointer, (nuint)pathBytes.Length,
                keyPointer, (nuint)key.Length,
                out handle));
        }
    }

    Execute(handle, """
        {"api_version":1,"type":"ingest_text","payload":{"text":"跨设备剪贴板","source_app":"ffi-smoke","captured_ms":10}}
        """);
    using JsonDocument result = JsonDocument.Parse(Execute(handle, """
        {"api_version":1,"type":"search","payload":{"pattern":"设备","mode":"substring"}}
        """));
    int count = result.RootElement.GetProperty("items").GetArrayLength();
    if (count != 1) throw new InvalidOperationException($"Expected 1 result; found {count}.");
    Console.WriteLine("FFI smoke test passed: 1 item found.");
}
finally
{
    if (handle != 0) NativeMethods.clipboard_core_close(handle);
    CryptographicOperations.ZeroMemory(key);
    if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
}

static unsafe string Execute(nint handle, string requestJson)
{
    byte[] request = Encoding.UTF8.GetBytes(requestJson);
    fixed (byte* requestPointer = request)
    {
        RequireOk(NativeMethods.clipboard_core_execute(
            handle, requestPointer, (nuint)request.Length, out CoreBuffer response));
        try
        {
            return Encoding.UTF8.GetString(
                new ReadOnlySpan<byte>((void*)response.Pointer, checked((int)response.Length)));
        }
        finally
        {
            NativeMethods.clipboard_core_free_buffer(response);
        }
    }
}

static void RequireOk(CoreStatus status)
{
    if (status != CoreStatus.Ok) throw new InvalidOperationException($"Core returned {status}.");
}
```

`clipboard-core` 同时定义以下请求信封，使 `type` 和 `payload` 与 `api_version` 位于同一 JSON 层级；服务入口拒绝缺失或非 `1` 的 API 版本：

```rust
#[derive(Debug, serde::Deserialize)]
pub struct ApiRequest {
    pub api_version: u32,
    #[serde(flatten)]
    pub command: CoreCommand,
}

impl ApiRequest {
    pub fn validate(self) -> Result<CoreCommand, CoreError> {
        if self.api_version != 1 {
            return Err(CoreError::UnsupportedApiVersion(self.api_version));
        }
        Ok(self.command)
    }
}
```

- [x] **Step 6: 构建动态库并运行 .NET 冒烟程序**

Run: `cargo build -p clipboard-ffi`

Expected: `target/debug/clipboard_ffi.dll` 存在。

Run: `dotnet run --project src/Clipboard.FfiSmoke/Clipboard.FfiSmoke.csproj`

Expected: `FFI smoke test passed: 1 item found.`。项目文件必须把 `target/debug/clipboard_ffi.dll` 复制到输出目录，不能依赖手工修改 PATH。

- [x] **Step 7: 提交 FFI 基座**

```powershell
git add crates/clipboard-ffi src/Clipboard.FfiSmoke
git commit -m "feat(core): expose clipboard core C ABI"
```

### Task 10: 建立阶段验证入口和 CI

**Files:**
- Create: `scripts/test-core.ps1`
- Create: `.github/workflows/core.yml`
- Create: `README.md`

- [x] **Step 1: 写单一验证脚本**

```powershell
$ErrorActionPreference = 'Stop'

pwsh -NoProfile -File "$PSScriptRoot/verify-toolchain.ps1"
cargo fmt --all --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace --all-targets
cargo build -p clipboard-ffi
dotnet run --project "$PSScriptRoot/../src/Clipboard.FfiSmoke/Clipboard.FfiSmoke.csproj"

Write-Host 'Clipboard core verification passed.'
```

- [x] **Step 2: 运行完整阶段验证**

Run: `pwsh -NoProfile -File scripts/test-core.ps1`

Expected final line: `Clipboard core verification passed.`，此前没有 warning、失败测试或未捕获异常。

- [x] **Step 3: 写 Windows CI**

`core.yml` 使用 `windows-2022`，安装 Rust 1.88 和 .NET 8，执行 `scripts/test-core.ps1`。CI 不上传数据库、日志或测试临时目录；只缓存 Cargo registry 和 target 编译输出。

- [x] **Step 4: 写 README 阶段说明**

README 明确：当前阶段只有共享内核和 FFI 冒烟程序，不是可用的 Windows 剪贴板应用；列出 `scripts/test-core.ps1` 验证命令，并链接设计规格与总路线图。

- [x] **Step 5: 提交验证入口**

```powershell
git add scripts/test-core.ps1 .github/workflows/core.yml README.md
git commit -m "ci: verify clipboard core foundation"
```

## 阶段 1 完成检查

- [ ] `git status --short` 无未提交文件。
- [x] `pwsh -NoProfile -File scripts/test-core.ps1` 在本机通过。
- [x] `file_bundle` 领域测试和数据库 trigger 双重证明其不能进入 outbox。
- [x] SQLCipher 错误密钥测试通过，且 `PRAGMA cipher_version` 非空。
- [x] 对象密文篡改测试失败关闭，不返回部分明文。
- [x] HLC 状态合并满足幂等和交换性质。
- [x] 中英文子串、文件路径正则和无效正则测试通过。
- [x] .NET FFI 冒烟程序能跨进程边界写入、重开和搜索。
- [x] README 未把阶段 1 描述成完整应用。

完成这些检查后，才编写并执行阶段 2 Windows 客户端计划。
