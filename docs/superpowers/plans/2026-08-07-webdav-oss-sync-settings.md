# WebDAV、OSS 与同步设置实施计划

> **回填状态：** 截至 2026-08-25，已按 `codex/phase4-image-sync` 的 `2e4c3f6` 回填。
> `[x]` 表示该步骤的最终交付结果可由当前代码、提交或自动测试证明；不重新声称历史红灯命令的原始输出仍可复现。
> 真实 WebDAV/OSS 写入、图片对象与两设备验收转入阶段五；当前总状态见 [Clipboard 开发状态](../../STATUS.md)。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为现有加密文本同步提供 WebDAV、阿里云 OSS 远端适配器和带 DPAPI 凭据保护的 Windows 同步设置页。

**Architecture:** `clipboard-sync` 定义不含明文的远端对象操作和两个 HTTP 适配器；Core 根据版本化远端配置选择适配器并保留现有合并/确认语义。Windows 将非敏感远端配置保存到 settings JSON，将凭据以 DPAPI 单独保护，仅在同步命令调用期间解密。

**Tech Stack:** Rust 1.88、reqwest Rustls、HMAC-SHA256、serde JSON、WinUI 3、Windows DPAPI、.NET 8。

---

## 文件结构

```text
crates/clipboard-sync/src/remote.rs        远端配置、对象名和 RemoteStore 契约
crates/clipboard-sync/src/webdav.rs        WebDAV Basic Auth 实现
crates/clipboard-sync/src/oss.rs           OSS 签名和对象实现
crates/clipboard-sync/tests/remote.rs      通用适配器契约测试
crates/clipboard-core/src/command.rs       SyncRemote DTO
crates/clipboard-core/src/service.rs       远端配置到同步编排适配
crates/clipboard-ffi/tests/abi.rs          远端同步 JSON ABI 回归
src/Clipboard.Windows/Platform/SyncCredentialStore.cs  DPAPI 凭据库
src/Clipboard.Windows/Platform/ClientSettings.cs        非敏感同步配置
src/Clipboard.Windows/ViewModels/SettingsViewModel.cs   同步设置状态与命令
src/Clipboard.Windows/Views/SettingsWindow.xaml         常规/同步双 Tab
```

### Task 1: 定义远端契约和脱敏配置

**Files:**
- Modify: `Cargo.toml`
- Modify: `crates/clipboard-sync/Cargo.toml`
- Create: `crates/clipboard-sync/src/remote.rs`
- Modify: `crates/clipboard-sync/src/lib.rs`
- Modify: `crates/clipboard-sync/src/error.rs`
- Test: `crates/clipboard-sync/tests/remote.rs`

- [x] **Step 1: 写失败测试**

```rust
#[test]
fn remote_config_debug_and_errors_do_not_expose_credentials_or_endpoints() {
    let config = RemoteConfig::webdav("https://sync.example.test/root", "alice", "secret");
    let encoded = format!("{config:?}");
    assert!(!encoded.contains("secret"));
    assert!(!encoded.contains("sync.example.test"));
}
```

- [x] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-sync --test remote`

Expected: FAIL，因为 `RemoteConfig` 尚不存在。

- [x] **Step 3: 实现最小远端 API**

```rust
pub trait RemoteStore: Send + Sync {
    fn list_completed(&self) -> Result<Vec<SegmentHeader>, SyncError>;
    fn get_completed(&self, header: &SegmentHeader) -> Result<Vec<u8>, SyncError>;
    fn put_pending_then_publish(&self, header: &SegmentHeader, ciphertext: &[u8]) -> Result<(), SyncError>;
    fn probe(&self) -> Result<(), SyncError>;
}

pub enum RemoteConfig {
    WebDav(WebDavConfig),
    Oss(OssConfig),
}
```

配置 `Debug` 只输出 provider 枚举和哈希后的配置标识；`SyncError` 新增无参数的 `Authentication`、`Conflict`、`RateLimited`、`RemoteUnavailable`。段对象名沿用 `version-vault-device-segment.enc`，pending 后缀固定 `.pending`。

- [x] **Step 4: 运行绿灯测试与格式检查**

Run: `cargo fmt --check && cargo test -p clipboard-sync --test remote`

Expected: PASS。

- [x] **Step 5: 提交远端契约**

```powershell
git add Cargo.toml crates/clipboard-sync
git commit -m "feat(sync): define remote store contract"
```

### Task 2: 实现 WebDAV 适配器

**Files:**
- Create: `crates/clipboard-sync/src/webdav.rs`
- Modify: `crates/clipboard-sync/src/lib.rs`
- Test: `crates/clipboard-sync/tests/webdav.rs`

- [x] **Step 1: 写 WebDAV 失败测试**

```rust
#[test]
fn webdav_publishes_only_completed_segments_after_move() {
    let server = WebDavFixture::start();
    let remote = WebDavStore::new(server.config("alice", "secret")).unwrap();
    remote.put_pending_then_publish(&header(), b"ciphertext").unwrap();
    assert_eq!(remote.list_completed().unwrap(), vec![header()]);
    assert!(server.requests().iter().any(|request| request.method == "MOVE"));
}
```

- [x] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-sync --test webdav`

Expected: FAIL，因为 `WebDavStore` 尚不存在。

- [x] **Step 3: 实现 WebDAV 操作**

`WebDavStore` 使用 `reqwest::blocking::Client` 和 Basic Auth。`PROPFIND Depth: 1` 只解析 `.enc` 名称；`GET` 读取；`PUT If-None-Match: *` 写入 pending；`MOVE Destination` 发布并保留 overwrite false。401/403 映射 `Authentication`，409/412 映射 `Conflict`，429 映射 `RateLimited`，其余 HTTP/网络失败映射 `RemoteUnavailable`。请求和错误不得格式化 URL、Authorization 或响应正文。

- [x] **Step 4: 运行绿灯测试**

Run: `cargo test -p clipboard-sync --test webdav`

Expected: PASS，且测试覆盖 Basic Auth、pending 不可见、认证失败和冲突不覆盖已有段。

- [x] **Step 5: 提交 WebDAV**

```powershell
git add crates/clipboard-sync Cargo.lock
git commit -m "feat(sync): add WebDAV remote store"
```

### Task 3: 实现阿里云 OSS 适配器

**Files:**
- Create: `crates/clipboard-sync/src/oss.rs`
- Modify: `crates/clipboard-sync/src/lib.rs`
- Test: `crates/clipboard-sync/tests/oss.rs`

- [x] **Step 1: 写 OSS 失败测试**

```rust
#[test]
fn oss_signature_binds_method_content_and_canonical_resource() {
    let request = OssRequest::put("bucket", "prefix/segment.pending", b"ciphertext");
    let authorization = sign_v4(&credentials(), &request, fixed_time()).unwrap();
    assert!(authorization.starts_with("OSS4-HMAC-SHA256 "));
    assert!(!authorization.contains("secret"));
}
```

- [x] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-sync --test oss`

Expected: FAIL，因为 OSS 签名与适配器尚不存在。

- [x] **Step 3: 实现 OSS 签名与对象操作**

以端点、区域、Bucket、前缀构造规范请求，使用 HMAC-SHA256 派生签名密钥；Header 和对象正文必须由请求签名覆盖。实现带 prefix 的 ListObjectsV2、GET、带 `If-None-Match: *` 的 pending PUT、服务端 COPY 到 `.enc` 和删除 pending。每个操作复用 `RemoteStore` 错误映射，永不记录 canonical request、Authorization、Bucket 名称或对象路径。

- [x] **Step 4: 运行绿灯测试**

Run: `cargo test -p clipboard-sync --test oss`

Expected: PASS，覆盖签名稳定性、对象前缀编码、pending 发布、认证和网络失败。

- [x] **Step 5: 提交 OSS**

```powershell
git add crates/clipboard-sync Cargo.lock
git commit -m "feat(sync): add OSS remote store"
```

### Task 4: 将远端配置接入 Core 和 FFI

**Files:**
- Modify: `crates/clipboard-core/Cargo.toml`
- Modify: `crates/clipboard-core/src/command.rs`
- Modify: `crates/clipboard-core/src/service.rs`
- Modify: `crates/clipboard-core/src/response.rs`
- Test: `crates/clipboard-core/tests/remote_sync.rs`
- Test: `crates/clipboard-ffi/tests/abi.rs`

- [x] **Step 1: 写 Core 失败测试**

```rust
#[test]
fn authentication_failure_keeps_outbox_pending_and_response_is_redacted() {
    let mut core = open_core();
    ingest_text(&mut core, "local only until upload");
    let error = core.execute(CoreCommand::SyncRemote(webdav_request("wrong-password"))).unwrap_err();
    assert!(matches!(error, CoreError::Sync(SyncError::Authentication)));
    assert_eq!(pending_count(&core), 1);
    assert!(!error.to_string().contains("wrong-password"));
}
```

- [x] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-core --test remote_sync`

Expected: FAIL，因为 `SyncRemote` 尚不存在。

- [x] **Step 3: 实现显式远端同步命令**

```rust
pub enum CoreCommand {
    SyncRemote(SyncRemote),
    // existing commands
}

pub struct SyncRemote {
    pub device_id: Uuid,
    pub remote: RemoteConfig,
}
```

将现有同步循环抽取为接受 `&dyn RemoteStore` 的私有函数；`SyncDirectory` 继续构造 `DirectoryTransport`，`SyncRemote` 构造 WebDAV 或 OSS store。成功上传后才 acknowledge；认证、网络、限流、冲突、篡改和版本失败不得确认失败事件。FFI 继续通过现有 `clipboard_core_execute` JSON ABI 传递请求，返回只含计数和错误类别。

- [x] **Step 4: 运行绿灯测试**

Run: `cargo test -p clipboard-core --test remote_sync && cargo test -p clipboard-ffi --test abi`

Expected: PASS。

- [x] **Step 5: 提交 Core/FFI 接入**

```powershell
git add crates/clipboard-core crates/clipboard-ffi Cargo.lock
git commit -m "feat(core): synchronize through remote stores"
```

### Task 5: DPAPI 凭据和双标签同步设置页

**Files:**
- Create: `src/Clipboard.Windows/Platform/SyncCredentialStore.cs`
- Modify: `src/Clipboard.Windows/Platform/ClientSettings.cs`
- Modify: `src/Clipboard.Windows/Platform/ClientSettingsStore.cs`
- Modify: `src/Clipboard.Windows/Core/ClipboardCoreClient.cs`
- Modify: `src/Clipboard.Windows/Core/CoreDtos.cs`
- Modify: `src/Clipboard.Windows/App.xaml.cs`
- Modify: `src/Clipboard.Windows/ViewModels/SettingsViewModel.cs`
- Modify: `src/Clipboard.Windows/Views/SettingsWindow.xaml`
- Modify: `src/Clipboard.Windows/Views/SettingsWindow.xaml.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/SyncCredentialStoreTests.cs`
- Test: `tests/Clipboard.Windows.Tests/ViewModels/SettingsViewModelTests.cs`
- Test: `tests/Clipboard.Windows.Tests/Views/XamlResourceConfigurationTests.cs`

- [x] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task SaveAsync_keeps_sync_secret_out_of_settings_json()
{
    await store.SaveAsync(settings.WithSync(new SyncSettings("webdav", "https://example.test", "vault")));
    await credentials.SaveAsync("profile", new SyncCredentials("alice", "secret"));
    Assert.DoesNotContain("secret", await File.ReadAllTextAsync(settingsPath));
    Assert.Equal("secret", (await credentials.LoadAsync("profile")).Password);
}
```

- [x] **Step 2: 运行红灯测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "SyncCredentialStoreTests|SettingsViewModelTests|XamlResourceConfigurationTests"`

Expected: FAIL，因为同步设置和 DPAPI 凭据库尚不存在。

- [x] **Step 3: 实现安全设置和同步操作**

`SyncCredentialStore` 使用 `ProtectedData.Protect(..., CurrentUser)`、随机 profile ID 和原子写入；读取后立即清零临时 UTF-8 数组。`ClientSettings` 只保存 `SyncSettings` 的 provider、endpoint、path/bucket、region、prefix、enabled、device ID、credential profile ID。设置窗口使用 `TabView`，首个 Tab 保留全部常规控件，第二个 Tab 根据 provider 切换 WebDAV/OSS 输入项，提供“测试连接”“立即同步”和“保存”。ViewModel 将凭据解密为一次性 DTO 调用 `ClipboardCoreClient.SyncRemoteAsync`，并把成功计数或脱敏错误类别呈现为状态文本。

- [x] **Step 4: 运行绿灯测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj -c Debug -p:Platform=x64 -p:WindowsAppSDKSelfContained=true --filter "SyncCredentialStoreTests|SettingsViewModelTests|XamlResourceConfigurationTests"`

Expected: PASS，settings JSON、日志快照和异常消息均不含密码或 AccessKey Secret。

- [x] **Step 5: 提交 Windows 设置页**

```powershell
git add src/Clipboard.Windows tests/Clipboard.Windows.Tests
git commit -m "feat(windows): add protected sync settings"
```

### Task 6: 端到端验证与文档

**Files:**
- Modify: `scripts/test-core.ps1`
- Modify: `scripts/verify-windows-client.ps1`
- Modify: `README.md`
- Test: `crates/clipboard-core/tests/remote_sync.rs`

- [x] **Step 1: 写失败的隐私回归**

```rust
#[test]
fn remote_requests_diagnostics_and_errors_never_include_secret_or_clipboard_text() {
    let trace = run_failed_webdav_sync("secret text", "credential-password");
    assert!(!trace.contains("secret text"));
    assert!(!trace.contains("credential-password"));
}
```

- [x] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-core --test remote_sync remote_requests_diagnostics_and_errors_never_include_secret_or_clipboard_text`

Expected: FAIL，直到测试辅助器接入新的远端请求与诊断。

- [x] **Step 3: 添加验证入口和文档**

`test-core.ps1` 显式运行 WebDAV、OSS、远端 Core 测试；`verify-windows-client.ps1` 保持对 Core 验证的调用并执行同步设置定向测试。README 说明 WebDAV/OSS 仅同步加密文本事件，文件束和图片对象不上传；凭据由当前 Windows 用户 DPAPI 保护，首次配置需输入凭据。

- [x] **Step 4: 运行完整验证**

Run: `pwsh -NoProfile -File scripts/test-core.ps1`

Run: `pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke`

Expected: 两条命令均以 0 退出；Windows 构建为 0 warnings / 0 errors。

- [x] **Step 5: 提交验证与文档**

```powershell
git add scripts README.md crates/clipboard-core/tests
git commit -m "ci(sync): verify WebDAV OSS synchronization"
```

## 覆盖检查

- Task 1 固定远端抽象、脱敏配置和错误类别。
- Task 2 覆盖 WebDAV 完成段发布与条件写入。
- Task 3 覆盖 OSS 签名和对象发布。
- Task 4 保证远端失败不确认 outbox，且 FFI 继续使用版本化 JSON。
- Task 5 覆盖 DPAPI、双标签设置和手动同步。
- Task 6 覆盖全量验证、隐私断言和用户文档。
