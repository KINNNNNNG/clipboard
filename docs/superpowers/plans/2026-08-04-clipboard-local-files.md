# Clipboard Local File History Implementation Plan

> **回填状态：** 截至 2026-08-25，已按 `codex/phase4-image-sync` 的 `2e4c3f6` 回填。
> `[x]` 表示该步骤的最终交付结果可由当前代码、提交或自动测试证明；不重新声称历史红灯命令的原始输出仍可复现。
> `[ ]` 仅保留给尚未完成的资源管理器和跨设备隐私人工验收；当前总状态见 [Clipboard 开发状态](../../STATUS.md)。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Windows 客户端捕获、搜索并以复制语义回放单个或多个真实文件系统文件和文件夹，同时保证其路径、元数据和内容严格只保留在本机。

**Architecture:** Rust Core 继续拥有 `file_bundle` 记录、规范化指纹、搜索和 `local_only` 同步边界；C# 只把 Windows `StorageItems` 转为路径元数据，并通过版本化 JSON FFI 读写文件束。普通记录只引用原路径；收藏后由 Rust 分块加密缓存，路径失效时只允许从这个本地缓存还原到受限暂存目录。任何 file bundle 命令都不得调用 outbox。

**Tech Stack:** Rust 1.88、SQLCipher、XChaCha20-Poly1305、.NET 8、WinUI 3、Windows App SDK、Windows StorageItems/CF_HDROP、xUnit。

---

## 文件结构

```text
crates/clipboard-domain/src/file_bundle.rs             文件条目、路径验证和本地范围
crates/clipboard-core/src/command.rs                   文件束摄取和读取命令 DTO
crates/clipboard-core/src/service.rs                   指纹、判重、读取和 local_only 持久化
crates/clipboard-core/src/file_cache.rs                收藏文件分块缓存和受限暂存
crates/clipboard-ffi/src/abi.rs                        现有 JSON execute 通道
src/Clipboard.Windows/Core/                            C# 文件束 DTO 和 FFI 客户端
src/Clipboard.Windows/Platform/                        StorageItems 捕获、写回和路径状态
src/Clipboard.Windows/ViewModels/                      文件卡摘要与失效状态
src/Clipboard.Windows/Views/                           文件卡、类型筛选和缓存设置
tests/Clipboard.Windows.Tests/                         不启动 WinUI 的平台/VM 测试
```

### Task 1: 为本机文件束建立 Core 命令和不变量

**Files:**
- Modify: `crates/clipboard-domain/src/file_bundle.rs`
- Modify: `crates/clipboard-core/src/command.rs`
- Modify: `crates/clipboard-core/src/response.rs`
- Modify: `crates/clipboard-core/src/service.rs`
- Test: `crates/clipboard-core/tests/file_bundle_workflow.rs`
- Test: `crates/clipboard-storage/tests/local_only_outbox.rs`

- [x] **Step 1: 写摄取、判重和 outbox 隔离失败测试**

```rust
#[test]
fn ingesting_the_same_normalized_file_bundle_reuses_the_local_item_without_outbox() {
    let mut core = open_core();
    let first = ingest(&mut core, vec![file("C:\\Docs\\a.txt", 10, 1)]);
    let second = ingest(&mut core, vec![file("c:\\docs\\A.txt", 10, 1)]);
    assert_eq!(first, second);
    assert_eq!(pending_outbox(&core), 0);
}

#[test]
fn empty_or_relative_file_bundle_is_rejected_before_storage() {
    assert!(ingest(&mut open_core(), vec![]).is_err());
    assert!(ingest(&mut open_core(), vec![file("relative.txt", 1, 1)]).is_err());
}
```

`local_only_outbox.rs` 再对直接绕过服务层的 `file_bundle` 插入断言 `pending_count() == 0`，测试文本/图片同步路径不受影响。

- [x] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-core --test file_bundle_workflow`

Expected: FAIL，因为 `ingest_file_bundle`、`read_file_bundle` 和路径验证尚未定义。

- [x] **Step 3: 实现版本化文件束 DTO 和服务方法**

```rust
pub struct IngestFileBundle {
    pub entries: Vec<FileEntry>,
    pub source_app: String,
    pub captured_ms: i64,
}

pub struct ReadFileBundle { pub item_id: Uuid }

pub enum CoreCommand {
    // 保留既有变体
    IngestFileBundle(IngestFileBundle),
    ReadFileBundle(ReadFileBundle),
}
```

`FileBundle::new` 额外拒绝空白、相对、重复路径；Windows 路径比较采用 `to_ascii_lowercase()`、将 `/` 统一为 `\\` 并去除末尾分隔符。`CoreService::ingest_file_bundle` 用 `KeyPurpose::Fingerprint` 对稳定 JSON（条目按规范化路径排序，含 kind/size/modified_ms）做 keyed hash，只在 `ClipboardContent::FileBundle` 中判重，调用 `database.items().insert` 或 `update`，不得调用 `insert_and_enqueue`。`read_file_bundle` 返回结构化 entries，搜索仍复用现有文件路径字段。

- [x] **Step 4: 运行 Core 与存储测试**

Run: `cargo test -p clipboard-core --test file_bundle_workflow`

Run: `cargo test -p clipboard-storage --test local_only_outbox`

Expected: PASS；文件束不生成任何 outbox 事件。

- [x] **Step 5: 提交 Core 文件束协议**

```powershell
git add crates/clipboard-domain crates/clipboard-core crates/clipboard-storage
git commit -m "feat(core): store local-only file bundles"
```

### Task 2: 通过 FFI 和 C# 传输结构化文件束

**Files:**
- Modify: `src/Clipboard.Windows/Core/CoreDtos.cs`
- Modify: `src/Clipboard.Windows/Core/ClipboardCoreClient.cs`
- Modify: `src/Clipboard.Windows/Core/NativeMethods.cs`
- Test: `tests/Clipboard.Windows.Tests/Core/ClipboardCoreClientTests.cs`

- [x] **Step 1: 写 C# JSON 契约失败测试**

```csharp
[Fact]
public async Task File_bundle_round_trip_uses_snake_case_and_never_calls_the_binary_image_abi()
{
    var native = new RecordingNative();
    using var client = OpenClient(native);
    await client.IngestFileBundleAsync(new IngestFileBundleRequestDto(
        [new FileEntryDto("C:\\Docs\\a.txt", FileEntryKindDto.File, 10, 1)],
        "explorer.exe", 100));

    Assert.Equal("ingest_file_bundle", native.LastJsonType);
    Assert.False(native.ImageAbiWasCalled);
}
```

再为 `ReadFileBundleAsync` 写测试，断言返回完整条目而不是以换行分隔的 `Preview`。

- [x] **Step 2: 运行红灯测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter ClipboardCoreClientTests`

Expected: FAIL，因为 C# DTO 和方法不存在。

- [x] **Step 3: 添加 DTO、接口和客户端方法**

```csharp
internal enum FileEntryKindDto { File, Directory }
internal sealed record FileEntryDto(string Path, FileEntryKindDto Kind, ulong Size, long ModifiedMs);
internal sealed record IngestFileBundleRequestDto(
    IReadOnlyList<FileEntryDto> Entries, string SourceApp, long CapturedMs);
internal sealed record FileBundleResponseDto(IReadOnlyList<FileEntryDto> Entries);
```

将 `IngestFileBundleAsync` 和 `ReadFileBundleAsync` 添加到 `IClipboardCaptureSink`/`IClipboardItemContentReader` 的适当窄接口，使用现有 `ExecuteCommandAsync`，不新增二进制 ABI。序列化保持 `snake_case`，`ClipboardCoreException` 不包含路径文本。

- [x] **Step 4: 运行 C# 测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter ClipboardCoreClientTests`

Expected: PASS。

- [x] **Step 5: 提交 FFI 客户端适配**

```powershell
git add src/Clipboard.Windows/Core tests/Clipboard.Windows.Tests/Core
git commit -m "feat(windows): expose local file bundle core commands"
```

### Task 3: 捕获与写回 Windows 文件和文件夹

**Files:**
- Modify: `src/Clipboard.Windows/Platform/IClipboardReader.cs`
- Modify: `src/Clipboard.Windows/Platform/WindowsClipboardReader.cs`
- Modify: `src/Clipboard.Windows/Platform/IClipboardWriter.cs`
- Modify: `src/Clipboard.Windows/Platform/WindowsClipboardWriter.cs`
- Modify: `src/Clipboard.Windows/Platform/ClipboardSuppression.cs`
- Modify: `src/Clipboard.Windows/Platform/ClipboardCaptureCoordinator.cs`
- Modify: `src/Clipboard.Windows/Platform/PasteCoordinator.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/ClipboardCaptureCoordinatorTests.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/PasteCoordinatorTests.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/ClipboardSuppressionTests.cs`

- [x] **Step 1: 写单个、多文件、文件夹和复制语义失败测试**

```csharp
[Fact]
public async Task File_bundle_capture_sends_exact_local_metadata_and_notifies_after_core_commit()
{
    var files = new[] { File("C:\\a.txt", 5), Directory("C:\\folder") };
    var core = new FakeCore();
    await CreateCoordinator(ClipboardPayload.Files(files), core).CaptureAsync();
    Assert.Equal(files, Assert.Single(core.FileBundleRequests).Entries);
    Assert.Single(core.RetentionRequests);
}

[Fact]
public async Task File_bundle_paste_writes_copy_operation_and_never_sends_input_when_the_path_is_missing()
{
    var writer = new FakeWriter { ExistingPaths = ["C:\\a.txt"] };
    PasteResult result = await CreatePasteCoordinator(writer).PasteAsync(FileBundleItem(), new nint(42));
    Assert.Equal(PasteResultKind.ManualPasteRequired, result.Kind);
    Assert.Equal(DataPackageOperation.Copy, writer.RequestedOperation);
}
```

抑制测试使用大小写无关、顺序敏感的规范化绝对路径哈希；应用自身写回的同一 bundle 只被消费一次。

- [x] **Step 2: 运行红灯测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "CaptureCoordinatorTests|PasteCoordinatorTests|SuppressionTests"`

Expected: FAIL，因为 `ClipboardPayloadKind.FileBundle` 和 file writer 方法尚未定义。

- [x] **Step 3: 实现真实路径 `StorageItems` 捕获**

`WindowsClipboardReader` 先检查 `StandardDataFormats.StorageItems`，调用 `GetStorageItemsAsync`；每个 `IStorageItem.Path` 必须是绝对物理路径，`FileAttributes.Directory` 决定类型，文件使用 `FileInfo.Length`，目录 size 固定 0，修改时间统一为 Unix ms。任何缺少真实 `Path` 的项目（Outlook/虚拟下载）使整个 bundle 返回 `Empty`，不保存部分列表。随后才尝试 bitmap 和 text。

```csharp
public static ClipboardPayload Files(IReadOnlyList<ClipboardFileEntry> entries) =>
    new(ClipboardPayloadKind.FileBundle, null, ReadOnlyMemory<byte>.Empty, 0, 0, entries);
```

`WindowsClipboardWriter.WriteFilesAsync` 对每条路径调用 `StorageFile.GetFileFromPathAsync` 或 `StorageFolder.GetFolderFromPathAsync`，建立 `DataPackage { RequestedOperation = DataPackageOperation.Copy }`，使用 `SetStorageItems`、`Clipboard.SetContent` 和 `Flush`。任何路径失效或 WinRT 读取失败时不得写空的 `CF_HDROP`，也不得继续发送 Ctrl+V。

- [x] **Step 4: 接入协调器、抑制和粘贴**

捕获成功后调用 `IngestFileBundleAsync`、执行现有保留服务、再通知 `CaptureNotification(itemId, "file_bundle")`。粘贴前 `ReadFileBundleAsync` 获取完整结构化路径，验证所有路径仍存在，登记文件束抑制令牌，写回系统剪贴板并遵循现有前台窗口恢复流程。`ParseKind` 支持 `file_bundle`。

- [x] **Step 5: 运行平台测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "CaptureCoordinatorTests|PasteCoordinatorTests|SuppressionTests"`

Expected: PASS；文件/文件夹捕获与复制语义均被覆盖。

- [x] **Step 6: 提交 Windows 文件剪贴板适配**

```powershell
git add src/Clipboard.Windows/Platform tests/Clipboard.Windows.Tests/Platform
git commit -m "feat(windows): capture and paste local file bundles"
```

### Task 4: 显示、搜索筛选和失效路径降级

**Files:**
- Modify: `src/Clipboard.Windows/ViewModels/ClipboardItemViewModel.cs`
- Modify: `src/Clipboard.Windows/ViewModels/ClipboardPanelViewModel.cs`
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml`
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml.cs`
- Modify: `src/Clipboard.Windows/Views/Converters.cs`
- Test: `tests/Clipboard.Windows.Tests/ViewModels/ClipboardPanelViewModelTests.cs`
- Test: `tests/Clipboard.Windows.Tests/ViewModels/ClipboardDisplayFormatterTests.cs`

- [x] **Step 1: 写文件卡摘要和筛选失败测试**

```csharp
[Fact]
public void File_bundle_card_shows_representative_name_and_count_without_exposing_full_paths_in_metadata()
{
    var card = new ClipboardItemViewModel(FileBundleItem("C:\\Docs\\report.docx", "C:\\Photos"));
    Assert.Equal("report.docx 等 2 项", card.FileSummary);
    Assert.True(card.IsFileBundle);
}

[Fact]
public async Task File_kind_filter_is_forwarded_as_file_bundle()
{
    viewModel.SetFilters(null, null, [], ["file_bundle"]);
    await viewModel.RefreshAsync();
    Assert.Equal(["file_bundle"], Assert.Single(core.Requests).Filters.Kinds);
}
```

- [x] **Step 2: 运行红灯测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "ClipboardPanelViewModelTests|ClipboardDisplayFormatterTests"`

Expected: FAIL，因为 file bundle 卡片属性和筛选控件不存在。

- [x] **Step 3: 实现文件卡与路径不可用状态**

在 `ClipboardItemDto` 中添加可选 `FileCount`、`RepresentativeName`、`FileBundleAvailable`；Core 搜索响应只包含这些卡片字段，实际路径只在点击粘贴时读取。XAML 给 `file_bundle` 添加紧凑文件图标、名称、条数、来源和“原路径不可用”状态；筛选菜单添加文件复选框。不可用卡片仍可删除或取消收藏，但点击主区域显示脱敏错误，不能写剪贴板。

- [x] **Step 4: 运行 ViewModel 测试和 x64 构建**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "ClipboardPanelViewModelTests|ClipboardDisplayFormatterTests"`

Run: `dotnet build src/Clipboard.Windows/Clipboard.Windows.csproj -c Debug -p:Platform=x64 -p:WindowsAppSDKSelfContained=true`

Expected: PASS，构建为 0 warnings / 0 errors。

- [x] **Step 5: 提交文件历史界面**

```powershell
git add src/Clipboard.Windows/ViewModels src/Clipboard.Windows/Views tests/Clipboard.Windows.Tests/ViewModels
git commit -m "feat(windows): display and filter local file history"
```

### Task 5: 收藏文件加密缓存与受限暂存

**Files:**
- Create: `crates/clipboard-core/src/file_cache.rs`
- Modify: `crates/clipboard-core/src/object_store.rs`
- Modify: `crates/clipboard-core/src/service.rs`
- Modify: `crates/clipboard-core/src/command.rs`
- Modify: `crates/clipboard-core/src/response.rs`
- Modify: `src/Clipboard.Windows/Core/CoreDtos.cs`
- Modify: `src/Clipboard.Windows/Core/ClipboardCoreClient.cs`
- Modify: `src/Clipboard.Windows/Platform/ClientSettings.cs`
- Modify: `src/Clipboard.Windows/ViewModels/SettingsViewModel.cs`
- Modify: `src/Clipboard.Windows/Views/SettingsWindow.xaml`
- Test: `crates/clipboard-core/tests/file_cache_workflow.rs`
- Test: `tests/Clipboard.Windows.Tests/ViewModels/SettingsViewModelTests.cs`

- [x] **Step 1: 写缓存准入、失败回滚和不同步失败测试**

```rust
#[test]
fn favorite_cache_rejects_reparse_points_and_rolls_back_partial_objects() {
    let root = tempdir().unwrap();
    let result = cache_bundle_with_reparse_point(&mut open_core(), root.path());
    assert!(result.is_err());
    assert!(pending_cache_objects(root.path()).is_empty());
}

#[test]
fn cached_file_bundle_never_adds_names_paths_or_bytes_to_outbox() {
    favorite_and_cache(&mut open_core(), bundle());
    assert_eq!(pending_outbox(&core), 0);
}
```

再测试：缓存空间不足在复制前拒绝；收藏后删除原路径能在当前用户专用暂存目录解密恢复；取消收藏释放缓存；60 分钟后清理暂存，启动时清理超过 24 小时的残留。

- [x] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-core --test file_cache_workflow`

Expected: FAIL，因为收藏缓存命令和分块对象不存在。

- [x] **Step 3: 实现分块缓存与安全限制**

按 `KeyPurpose::FileCache` 为每个对象派生 AEAD 密钥；固定分块大小 1 MiB，每块独立 nonce 和包含 vault、bundle、relative path、chunk index、version 的 AAD。预扫描所有目录，不跟随 `FileAttributes.ReparsePoint`，在开始复制前计算总大小并与 `MaxFavoriteFileCacheBytes` 比较。写到 `.pending`，所有块认证成功后原子提交索引；任何失败删除 pending 目录且不把记录标记 cached。缓存状态和文件路径只写 SQLCipher `content_json`，不得入 outbox。

- [x] **Step 4: 接入收藏、失效路径回放和设置**

文件收藏调用 `cache_file_bundle`；普通收藏状态仅在缓存成功后设为 true。`read_file_bundle` 原路径无效时解密到 `%LOCALAPPDATA%\\Clipboard\\staging\\{bundle_id}`，使用当前用户 ACL，返回暂存路径给 writer；暂存清理不跟随重解析点。`ClientSettings` 增加默认 5 GiB 的 `MaxFavoriteFileCacheBytes` 及可关闭开关，设置页显示独立上限；收藏项永不自动逐出缓存。

- [x] **Step 5: 运行 Core、Windows 测试和完整验证**

Run: `cargo test -p clipboard-core --test file_cache_workflow`

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj -c Debug -p:Platform=x64 -p:WindowsAppSDKSelfContained=true`

Run: `pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke`

Expected: 所有测试 PASS；缓存失败不留下部分文件；验证输出 `Clipboard Windows client verification passed.`。

- [x] **Step 6: 提交收藏文件缓存**

```powershell
git add crates/clipboard-core src/Clipboard.Windows tests/Clipboard.Windows.Tests
git commit -m "feat(files): cache favorite file bundles locally"
```

### Task 6: 阶段 3 验收、隐私证明与文档

**Files:**
- Modify: `README.md`
- Modify: `scripts/verify-windows-client.ps1`
- Test: `crates/clipboard-storage/tests/local_only_outbox.rs`
- Test: `tests/Clipboard.Windows.Tests/Platform/PasteCoordinatorTests.cs`

- [x] **Step 1: 添加跨层本地-only 回归测试**

测试摄取、搜索、收藏缓存、粘贴和删除流程后 `sync_outbox` 始终没有 file bundle 事件；断言序列化事件文本不含测试路径与文件名。测试原路径失效且未缓存时 writer/foreground service 都未被调用。

- [x] **Step 2: 更新验证入口和 README**

`verify-windows-client.ps1` 在 Core 测试后显式运行 `file_bundle_workflow` 和 `file_cache_workflow`；README 把本机文件/文件夹历史列为已交付，同时明确 WebDAV/OSS 同步只同步文本和图片，文件名、路径、元数据、缓存状态和内容都不上传。

- [ ] **Step 3: 执行完整自动验证与人工矩阵**

Run: `pwsh -NoProfile -File scripts/verify-windows-client.ps1`

人工验证：资源管理器复制单个文件、多个混合条目和文件夹；搜索文件名/路径；原路径有效的自动粘贴；收藏后删除原文件的暂存回放；失效且未缓存时禁用粘贴；检查另一台设备和远端对象均没有文件记录。

- [x] **Step 4: 提交阶段验证**

```powershell
git add README.md scripts crates/clipboard-storage/tests tests/Clipboard.Windows.Tests/Platform
git commit -m "ci(files): verify local-only file history"
```

## 阶段 3 完成检查

- [ ] 单个、多文件和文件夹仅在产生记录的本机可见、可搜索、可复制回放。
- [x] `StorageItems` 中无真实路径的虚拟文件不保存部分记录。
- [x] 所有回放使用 `DataPackageOperation.Copy`；路径失效且无缓存时绝不发送 `Ctrl+V`。
- [x] 文件记录、路径、名称、元数据、缓存状态和内容永不进入 `sync_outbox`。
- [x] 收藏缓存只在预扫描与容量准入成功后提交；不跟随重解析点，失败没有部分对象。
- [x] 收藏项不自动清理；暂存目录仅当前用户可读并按 60 分钟/24 小时规则清理。
- [x] `pwsh -NoProfile -File scripts/verify-windows-client.ps1` 通过且 Windows 构建无警告。
