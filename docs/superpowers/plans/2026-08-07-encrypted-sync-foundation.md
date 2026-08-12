# 加密同步与设备配对基础实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让两个本地 Core 实例通过目录型模拟远端交换端到端加密的文本事件，并提供恢复码、协议拒绝和脱敏诊断。

**Architecture:** 新增 `clipboard-sync` crate，承载版本化事件、每段 scoped AEAD、恢复码、目录 transport 和诊断接口。`clipboard-storage` 为现有 outbox 增加读取与确认 API，`clipboard-core` 通过显式同步命令消费/合并事件；`file_bundle` 和所有 `local_only` 记录在 Core 与 Sync 两层拒绝，绝不触及 transport。

**Tech Stack:** Rust 1.88、serde JSON、XChaCha20-Poly1305、HKDF-SHA256、SQLCipher、临时目录集成测试。

---

## 文件结构

```text
crates/clipboard-sync/src/protocol.rs       版本化事件、段头和 AEAD 序列化
crates/clipboard-sync/src/recovery_code.rs  恢复码编码、校验和和解码
crates/clipboard-sync/src/transport.rs      SyncTransport 与 DirectoryTransport
crates/clipboard-sync/src/diagnostics.rs    脱敏结构化诊断接口
crates/clipboard-sync/src/service.rs        拉取、认证、合并、上传编排
crates/clipboard-storage/src/outbox.rs      待同步事件读取与确认
crates/clipboard-core/src/command.rs        显式目录同步命令 DTO
crates/clipboard-core/src/service.rs        outbox 到 SyncService 的适配和入站合并
crates/clipboard-core/tests/sync_foundation.rs  两设备端到端同步回归
```

### Task 1: 建立同步 crate、恢复码与脱敏诊断

**Files:**
- Modify: `Cargo.toml`
- Create: `crates/clipboard-sync/Cargo.toml`
- Create: `crates/clipboard-sync/src/lib.rs`
- Create: `crates/clipboard-sync/src/error.rs`
- Create: `crates/clipboard-sync/src/recovery_code.rs`
- Create: `crates/clipboard-sync/src/diagnostics.rs`
- Test: `crates/clipboard-sync/tests/recovery_code.rs`
- Test: `crates/clipboard-sync/tests/diagnostics.rs`

- [ ] **Step 1: 写恢复码与诊断失败测试**

```rust
#[test]
fn recovery_code_round_trips_and_rejects_a_changed_character() {
    let source = RecoveryMaterial::new(Uuid::from_u128(1), [0x41; 32]);
    let encoded = encode_recovery_code(&source).unwrap();
    assert_eq!(decode_recovery_code(&encoded).unwrap(), source);
    assert!(decode_recovery_code(&encoded.replacen('A', "B", 1)).is_err());
}

#[test]
fn local_only_diagnostic_does_not_contain_a_file_path() {
    let diagnostics = RecordingDiagnostics::default();
    diagnostics.record(SyncDiagnostic::local_only_rejected(Uuid::from_u128(9)));
    assert!(!diagnostics.serialized().contains("report.txt"));
}
```

- [ ] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-sync --test recovery_code --test diagnostics`

Expected: FAIL，因为 crate、恢复码 API 和诊断类型尚不存在。

- [ ] **Step 3: 添加 crate 与最小实现**

在 workspace members 中加入 `crates/clipboard-sync`，crate 依赖 `clipboard-crypto`、`clipboard-domain`、`base64`、`blake3`、`serde`、`thiserror`、`uuid` 和 `zeroize`。定义如下 API：

```rust
pub const SYNC_PROTOCOL_VERSION: u8 = 1;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RecoveryMaterial { pub vault_id: Uuid, pub master_key: [u8; 32] }

pub fn encode_recovery_code(material: &RecoveryMaterial) -> Result<String, SyncError>;
pub fn decode_recovery_code(value: &str) -> Result<RecoveryMaterial, SyncError>;

pub trait SyncDiagnostics: Send + Sync {
    fn record(&self, diagnostic: SyncDiagnostic);
}

pub struct NoopSyncDiagnostics;
```

编码内容为版本、vault ID、主密钥和前 4 字节 BLAKE3 checksum 的 base64url；每 5 字符用 `-` 分组。解码先去除分组符，再验证长度、版本和 checksum。`SyncDiagnostic` 只保留阶段、结果类别、哈希后的 ID、数量、密文长度和耗时；构造 local-only 记录时只接受稳定 item ID，不接受路径字符串。

- [ ] **Step 4: 运行绿灯测试与格式检查**

Run: `cargo fmt --check && cargo test -p clipboard-sync --test recovery_code --test diagnostics`

Expected: PASS。

- [ ] **Step 5: 提交恢复码和诊断边界**

```powershell
git add Cargo.toml crates/clipboard-sync
git commit -m "feat(sync): add recovery codes and diagnostics"
```

### Task 2: 定义加密日志段和目录型 transport

**Files:**
- Create: `crates/clipboard-sync/src/protocol.rs`
- Create: `crates/clipboard-sync/src/transport.rs`
- Modify: `crates/clipboard-sync/src/lib.rs`
- Test: `crates/clipboard-sync/tests/protocol.rs`
- Test: `crates/clipboard-sync/tests/directory_transport.rs`

- [ ] **Step 1: 写段认证、版本和原子上传失败测试**

```rust
#[test]
fn encrypted_segment_rejects_tampering_and_wrong_vault() {
    let segment = seal_segment(&journal_key(), header(1), &[text_event("alpha")]).unwrap();
    assert!(open_segment(&journal_key(), header(1), &flip_last_byte(segment.clone())).is_err());
    assert!(open_segment(&journal_key(), header_for_other_vault(), &segment).is_err());
}

#[test]
fn directory_transport_ignores_pending_uploads_until_rename() {
    let remote = DirectoryTransport::open(tempdir().unwrap().path()).unwrap();
    remote.write_pending_for_test(segment_name(), b"ciphertext").unwrap();
    assert!(remote.list_segments(device_id()).unwrap().is_empty());
}
```

- [ ] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-sync --test protocol --test directory_transport`

Expected: FAIL，因为段格式和 transport 尚未定义。

- [ ] **Step 3: 实现版本化段与 transport**

定义 `SyncEvent` 的 `TextUpsert { item: ClipboardItem }`、`Favorite { item_id, state }`、`Delete { item_id, state }` 变体；`TryFrom<&ClipboardItem>` 仅接受 `ClipboardContent::Text` 且 `SyncScope::Vault`，其他内容返回 `SyncError::LocalOnlyRejected`。段头为：

```rust
pub struct SegmentHeader {
    pub protocol_version: u8,
    pub vault_id: Uuid,
    pub device_id: Uuid,
    pub segment_id: Uuid,
}
```

使用 `journal_key.derive_scoped(segment_aad(&header))` 创建 `ObjectCipher`；AAD 严格由版本、vault、设备、段 ID 和 `b"journal"` 构成。`DirectoryTransport::put_segment` 先将密文写到同目录 `{segment}.pending`，`sync_all` 后 rename 为 `{segment}.enc`；`list_segments` 仅返回 `.enc`，并按文件名排序。所有 transport 错误只保留 `SyncError::Transport`，不把路径拼入 Display 文本。

- [ ] **Step 4: 运行绿灯测试**

Run: `cargo test -p clipboard-sync --test protocol --test directory_transport`

Expected: PASS。

- [ ] **Step 5: 提交协议和本地远端**

```powershell
git add crates/clipboard-sync
git commit -m "feat(sync): add encrypted directory journal transport"
```

### Task 3: 暴露 outbox 消费接口并保持 local-only 拒绝

**Files:**
- Modify: `crates/clipboard-storage/src/outbox.rs`
- Modify: `crates/clipboard-storage/src/lib.rs`
- Test: `crates/clipboard-storage/tests/outbox_consumption.rs`

- [ ] **Step 1: 写读取、确认和文件束隔离失败测试**

```rust
#[test]
fn acknowledging_one_outbox_entry_leaves_later_entries_pending() {
    let database = open_database();
    enqueue_text(&database, "first");
    enqueue_text(&database, "second");
    let entries = database.outbox().pending().unwrap();
    database.outbox().acknowledge(entries[0].id).unwrap();
    assert_eq!(database.outbox().pending().unwrap().len(), 1);
}

#[test]
fn local_file_bundle_has_no_consumable_outbox_entry_or_serialized_path() {
    let database = open_database();
    insert_local_file_bundle(&database, "C:\\private\\taxes.pdf");
    assert!(database.outbox().pending().unwrap().is_empty());
}
```

- [ ] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-storage --test outbox_consumption`

Expected: FAIL，因为 `OutboxEntry`、`pending` 和 `acknowledge` 不存在。

- [ ] **Step 3: 实现最小 outbox 查询 API**

```rust
pub struct OutboxEntry { pub id: i64, pub item_id: Uuid, pub event_json: String, pub created_ms: i64 }

pub fn pending(&self) -> Result<Vec<OutboxEntry>, OutboxError>;
pub fn acknowledge(&self, id: i64) -> Result<bool, OutboxError>;
```

`pending` 按 `(created_ms, id)` 升序读取，`acknowledge` 只删除指定 ID。不得修改 `enqueue_item` 与数据库 trigger 的 local-only 拒绝；测试同时确认文本 outbox 仍可消费。

- [ ] **Step 4: 运行绿灯测试**

Run: `cargo test -p clipboard-storage --test outbox_consumption --test local_only_outbox`

Expected: PASS。

- [ ] **Step 5: 提交 outbox 消费接口**

```powershell
git add crates/clipboard-storage
git commit -m "feat(storage): consume pending sync outbox entries"
```

### Task 4: 将同步服务接入 Core 的显式目录命令

**Files:**
- Modify: `crates/clipboard-core/Cargo.toml`
- Modify: `crates/clipboard-core/src/command.rs`
- Modify: `crates/clipboard-core/src/error.rs`
- Modify: `crates/clipboard-core/src/response.rs`
- Modify: `crates/clipboard-core/src/service.rs`
- Create: `crates/clipboard-core/tests/sync_foundation.rs`

- [ ] **Step 1: 写两设备合并、幂等和 local-only 拒绝失败测试**

```rust
#[test]
fn two_cores_converge_after_offline_text_events_are_synced_in_reverse_order() {
    let remote = tempdir().unwrap();
    let mut first = open_core("first-device");
    let mut second = open_core("second-device");
    ingest(&mut first, "from first");
    ingest(&mut second, "from second");
    sync(&mut second, remote.path()).unwrap();
    sync(&mut first, remote.path()).unwrap();
    sync(&mut second, remote.path()).unwrap();
    assert_eq!(previews(&mut first), previews(&mut second));
}

#[test]
fn malformed_file_bundle_outbox_event_is_rejected_before_transport_and_diagnostics_hide_its_path() {
    let mut core = open_core("device");
    inject_file_bundle_event(&mut core, "C:\\private\\payroll.xlsx");
    let result = sync(&mut core, tempdir().unwrap().path()).unwrap();
    assert_eq!(result.rejected_local_only, 1);
    assert!(!diagnostics_json().contains("payroll.xlsx"));
}
```

- [ ] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-core --test sync_foundation`

Expected: FAIL，因为同步命令和入站合并不存在。

- [ ] **Step 3: 添加命令、服务编排和入站合并**

在 `CoreCommand` 增加：

```rust
SyncDirectory(SyncDirectory { remote_path: String, device_id: Uuid })
```

`CoreService::sync_directory` 派生 `KeyPurpose::Journal`，创建 `DirectoryTransport` 与 `SyncService`，按“拉取所有完成段、认证解码、忽略自己已处理段、合并、上传本机 pending outbox、acknowledge 成功上传条目”执行。入站 `TextUpsert` 按 item ID 插入或更新；`Favorite`/`Delete` 使用现有 `merge` 方法，仅在状态变更时更新本地数据库，绝不重新 enqueue 入站事件。`SyncDirectory` 响应返回 pulled、merged、uploaded、rejected_local_only 数量，不返回远端路径、明文或密钥。

`CoreError` 增加透明 `Sync` 变体；所有同步命令保持显式调用，不启动线程。对 outbox JSON 反序列化出 `FileBundle` 或 `local_only` 时，记录 `reject_local_only` 诊断、确认并丢弃该错误条目，且不调用 transport。正常数据库约束已阻止真实本地文件记录进入 outbox；测试通过向一个同步文本 item 关联的 outbox 行直接注入 file bundle JSON，验证同步层的第二道拒绝边界。

- [ ] **Step 4: 运行绿灯测试和 Core 回归**

Run: `cargo test -p clipboard-core --test sync_foundation && cargo test -p clipboard-core`

Expected: PASS；同步顺序、重复拉取、篡改拒绝和文件束隔离均通过。

- [ ] **Step 5: 提交 Core 同步命令**

```powershell
git add crates/clipboard-core crates/clipboard-sync
git commit -m "feat(core): synchronize encrypted local journals"
```

### Task 5: 阶段验证与文档边界

**Files:**
- Modify: `scripts/test-core.ps1`
- Modify: `scripts/verify-windows-client.ps1`
- Modify: `README.md`
- Test: `crates/clipboard-sync/tests/protocol.rs`
- Test: `crates/clipboard-core/tests/sync_foundation.rs`

- [ ] **Step 1: 写诊断和远端隐私断言失败测试**

```rust
#[test]
fn remote_files_and_diagnostics_never_contain_clipboard_text_or_file_paths() {
    let remote = tempdir().unwrap();
    let diagnostics = RecordingDiagnostics::default();
    sync_text_and_attempt_file_bundle(remote.path(), &diagnostics);
    assert!(remote_file_bytes(remote.path()).iter().all(|bytes| !contains_utf8(bytes, "secret text")));
    assert!(!diagnostics.serialized().contains("C:\\private\\file.txt"));
}
```

- [ ] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-core --test sync_foundation remote_files_and_diagnostics_never_contain_clipboard_text_or_file_paths`

Expected: FAIL，直到端到端诊断与远端密文断言接入测试助手。

- [ ] **Step 3: 添加验证入口与文档说明**

`test-core.ps1` 在 workspace 测试后显式运行 `clipboard-sync` 协议、transport、恢复码和诊断测试，以及 `clipboard-core --test sync_foundation`。`verify-windows-client.ps1` 继续调用 `test-core.ps1`，不新增 Windows UI 同步行为。README 标注阶段 4 基础仅支持本地目录模拟远端，不接受 WebDAV/OSS 凭据；文本同步日志为端到端密文，文件束仍严格本地-only。

- [ ] **Step 4: 运行完整验证**

Run: `pwsh -NoProfile -File scripts/test-core.ps1`

Run: `pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke`

Expected: 两条命令均以 0 退出；Windows 构建为 0 warnings / 0 errors。

- [ ] **Step 5: 提交验证与文档**

```powershell
git add scripts README.md crates/clipboard-core/tests/sync_foundation.rs crates/clipboard-sync/tests
git commit -m "ci(sync): verify encrypted directory synchronization"
```

## 范围检查

- Task 1 覆盖恢复码和脱敏日志。
- Task 2 覆盖目录远端、每段独立 AEAD、AAD、版本和原子上传。
- Task 3 覆盖同步队列消费与 local-only 数据库边界。
- Task 4 覆盖显式 Core 编排、两设备合并、幂等和文件束双层拒绝。
- Task 5 覆盖端到端隐私断言、验证脚本和文档。

本计划明确不实现 WebDAV、OSS、图片对象同步、Windows 配对 UI、DPAPI 凭据或后台重试。
