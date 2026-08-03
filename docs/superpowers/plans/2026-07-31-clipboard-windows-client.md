# Clipboard Windows Client Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付可在 Windows 11 本机捕获、搜索并立即粘贴文本和图片的 WinUI 3 客户端，默认尝试接管 `Win+V`，失败时使用可配置备用快捷键。

**Architecture:** Rust 内核继续拥有记录、加密对象、搜索、筛选、收藏、删除和保留规则；C ABI 增加显式二进制图片入口，避免把 PNG 放入 JSON。C# 客户端按 FFI、保险库启动、剪贴板捕获、立即粘贴、快捷键/窗口定位、ViewModel 和 WinUI 视图拆分，平台 API 全部位于 Windows 项目内。

**Tech Stack:** Rust 1.88、SQLCipher、XChaCha20-Poly1305、.NET 8、C#、WinUI 3、Windows App SDK 1.8.260710003、Windows SDK 26100、Win32、xUnit 2.9.3。

---

## 文件结构

```text
crates/clipboard-domain/                     收藏、删除和筛选所需领域字段
crates/clipboard-storage/                    状态持久化、图片对象索引和保留执行
crates/clipboard-core/                       UI 查询、图片对象和本地命令编排
crates/clipboard-ffi/                        JSON 命令和 PNG 二进制 ABI
src/Clipboard.Windows/                       WinUI 3 x64 非打包客户端
src/Clipboard.Windows/Core/                  FFI DTO、原生句柄和 Core 客户端
src/Clipboard.Windows/Platform/              DPAPI、剪贴板、快捷键、窗口和启动项
src/Clipboard.Windows/ViewModels/            面板和设置状态
src/Clipboard.Windows/Views/                 主面板、记录模板和设置窗口
tests/Clipboard.Windows.Tests/               不启动 WinUI 的平台纯逻辑测试
scripts/verify-windows-client.ps1             阶段 2 单一验证入口
```

`Clipboard.Windows` 不复制 Rust 的搜索、保留或加密规则。所有 Win32 P/Invoke 集中在 `Platform/NativeMethods.cs`；XAML code-behind 只处理窗口生命周期和平台事件，不包含数据库或领域逻辑。

### Task 0: 建立可重复构建的 WinUI 3 项目

**Files:**
- Create: `src/Clipboard.Windows/Clipboard.Windows.csproj`
- Create: `src/Clipboard.Windows/App.xaml`
- Create: `src/Clipboard.Windows/App.xaml.cs`
- Create: `src/Clipboard.Windows/app.manifest`
- Create: `src/Clipboard.Windows/Views/MainWindow.xaml`
- Create: `src/Clipboard.Windows/Views/MainWindow.xaml.cs`
- Create: `tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj`
- Create: `scripts/verify-windows-client-toolchain.ps1`

- [ ] **Step 1: 写 Windows 客户端工具链检查**

`scripts/verify-windows-client-toolchain.ps1` 检查：Windows 11、x64、.NET 8 SDK、MSBuild 17.14、Windows SDK `10.0.26100.0`、`Microsoft.WindowsAppRuntime.1.8` x64 包和阶段 1 的 `target/debug/clipboard_ffi.dll`。缺一项即 `throw`，末行固定输出 `Windows client toolchain verification passed.`。

- [ ] **Step 2: 运行检查确认 DLL 前置满足且 WinUI 包尚未还原**

Run: `pwsh -NoProfile -File scripts/verify-windows-client-toolchain.ps1`

Expected: 工具链检查通过；不得把“已安装 Runtime”误当作项目 NuGet 包已经还原。

- [ ] **Step 3: 创建手工 WinUI 项目清单**

`Clipboard.Windows.csproj` 固定以下关键属性和包，不使用浮动版本：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.22621.0</TargetPlatformMinVersion>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PlatformTarget>x64</PlatformTarget>
    <UseWinUI>true</UseWinUI>
    <UseWindowsForms>true</UseWindowsForms>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.260710003" />
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />
    <None Include="..\..\target\debug\clipboard_ffi.dll" Link="clipboard_ffi.dll" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

测试项目固定 `Microsoft.NET.Test.Sdk` `17.14.1`、`xunit` `2.9.3` 和 `xunit.runner.visualstudio` `3.1.4`，目标框架与主项目相同，并使用 `<ProjectReference Include="../../src/Clipboard.Windows/Clipboard.Windows.csproj" />`；主项目不引用 FFI smoke 工程，始终从阶段 1 已构建的 DLL 复制 native 文件。

- [ ] **Step 4: 写最小 App 和 Window**

`App.xaml.cs` 只创建 `MainWindow`；主窗口临时显示 `剪贴板` 标题。`app.manifest` 声明 `PerMonitorV2` DPI awareness 和 Windows 11 支持，不申请管理员权限；阶段 2 为非打包开发运行，不创建 MSIX manifest。

- [ ] **Step 5: 还原并构建 x64 客户端**

Run: `dotnet restore src/Clipboard.Windows/Clipboard.Windows.csproj`

Run: `dotnet build src/Clipboard.Windows/Clipboard.Windows.csproj -c Debug -p:Platform=x64`

Expected: `0 Warning(s), 0 Error(s)`，输出目录包含 `Clipboard.Windows.exe`、Windows App SDK bootstrap 文件和 `clipboard_ffi.dll`。

- [ ] **Step 6: 提交 WinUI 基线**

```powershell
git add src/Clipboard.Windows tests/Clipboard.Windows.Tests scripts/verify-windows-client-toolchain.ps1
git commit -m "build(windows): scaffold WinUI clipboard client"
```

### Task 1: 持久化收藏、删除、筛选和执行保留策略

**Files:**
- Modify: `crates/clipboard-domain/src/item.rs`
- Create: `crates/clipboard-storage/migrations/002_ui_state.sql`
- Modify: `crates/clipboard-storage/src/item_repository.rs`
- Modify: `crates/clipboard-storage/src/outbox.rs`
- Modify: `crates/clipboard-core/src/command.rs`
- Modify: `crates/clipboard-core/src/response.rs`
- Modify: `crates/clipboard-core/src/service.rs`
- Test: `crates/clipboard-storage/tests/item_state.rs`
- Test: `crates/clipboard-core/tests/ui_commands.rs`

- [ ] **Step 1: 写状态与 UI 命令失败测试**

测试覆盖：收藏状态关闭数据库后仍保留；搜索按 `created_after_ms`、`created_before_ms`、`source_apps` 和 `kinds` 组合筛选；删除项不再出现在结果中；`clear_unfavorite` 保留收藏项；`apply_retention` 使用最大条数、天数和图片字节上限，并让同步项生成 outbox 墓碑；连续摄取相同文本按带密钥指纹合并为一条并更新最近使用时间。

核心请求 DTO 固定为：

```rust
pub struct SearchRequest {
    pub pattern: String,
    pub mode: SearchMode,
    pub filters: SearchFilters,
}

pub struct SearchFilters {
    pub created_after_ms: Option<i64>,
    pub created_before_ms: Option<i64>,
    pub source_apps: Vec<String>,
    pub kinds: Vec<String>,
}

pub struct SetFavorite { pub item_id: Uuid, pub favorite: bool, pub updated: Hlc }
pub struct DeleteRequest { pub item_id: Uuid, pub updated: Hlc }
pub struct ApplyRetentionRequest { pub now_ms: i64, pub policy: RetentionPolicy }
```

`CoreCommand` 的变体固定为 `IngestText`、`IngestImage`、`Search`、`SetFavorite`、`Delete`、`ClearUnfavorite` 和 `ApplyRetention(ApplyRetentionRequest)`；JSON 使用 `#[serde(tag = "type", content = "payload", rename_all = "snake_case")]`。

- [ ] **Step 2: 运行红灯测试**

Run: `cargo test -p clipboard-storage --test item_state`

Run: `cargo test -p clipboard-core --test ui_commands`

Expected: FAIL，状态仓储和 UI 命令尚未实现。

- [ ] **Step 3: 实现状态持久化和筛选**

`002_ui_state.sql` 增加可空 `content_fingerprint BLOB CHECK(length(content_fingerprint) = 32)` 和索引。`ClipboardItem` 增加 `content_fingerprint: Option<[u8; 32]>`、`favorite_state: Option<FavoriteState>` 与 `delete_state: Option<DeleteState>`，构造器默认 `None`。文本/图片指纹使用 `VaultKey::derive(vault_id, KeyPurpose::Fingerprint)` 作为 BLAKE3 keyed hash；仓储使用现有 `favorite_json`、`delete_json` 列更新和读取状态。所有更新使用显式事务，列表默认排除已删除项，内部保留包含删除项的读取接口供合并和清理使用。

- [ ] **Step 4: 实现命令编排**

`SetFavorite`、`Delete`、`ClearUnfavorite` 和 `ApplyRetention` 必须真实持久化，不再返回未启用错误。文本和图片状态变更写 outbox，文件保持 `local_only`。`Search` 先在 Rust 中筛选，再调用 `SearchEngine`；无效正则仍返回错误且不修改状态。

- [ ] **Step 5: 运行领域、存储和 Core 测试**

Run: `cargo test -p clipboard-domain -p clipboard-storage -p clipboard-core`

Expected: 状态、筛选、清理和既有纵向测试全部 PASS。

- [ ] **Step 6: 提交 UI 命令协议**

```powershell
git add crates/clipboard-domain crates/clipboard-storage crates/clipboard-core
git commit -m "feat(core): add clipboard UI state commands"
```

### Task 2: 加密保存并读取 PNG 图片对象

**Files:**
- Create: `crates/clipboard-core/src/object_store.rs`
- Modify: `crates/clipboard-core/src/service.rs`
- Modify: `crates/clipboard-core/src/response.rs`
- Modify: `crates/clipboard-ffi/src/abi.rs`
- Modify: `crates/clipboard-ffi/src/lib.rs`
- Test: `crates/clipboard-core/tests/image_workflow.rs`
- Test: `crates/clipboard-ffi/tests/image_abi.rs`

- [ ] **Step 1: 写图片往返和篡改失败测试**

使用固定的 1x1 PNG fixture。测试 `CoreService::ingest_image` 后搜索返回 `kind=image`、宽高和字节数；关闭重开后 `read_image` 返回原 PNG；对象文件不包含 PNG 头；修改密文最后一字节后读取返回认证错误且不返回部分图片。

- [ ] **Step 2: 写二进制 ABI 红灯测试**

新增固定入口：

```rust
pub unsafe extern "C" fn clipboard_core_ingest_image(
    handle: *mut CoreHandle,
    metadata_json_ptr: *const u8,
    metadata_json_len: usize,
    png_ptr: *const u8,
    png_len: usize,
    out_response: *mut CoreBuffer,
) -> CoreStatus;

pub unsafe extern "C" fn clipboard_core_read_image(
    handle: *mut CoreHandle,
    item_id_ptr: *const u8,
    item_id_len: usize,
    out_png: *mut CoreBuffer,
) -> CoreStatus;
```

metadata 包含 `api_version`、`width`、`height`、`source_app` 和 `captured_ms`。测试空指针、零长度、超过 50 MiB、无效 UUID 和认证失败状态。

同时新增 `clipboard_core_open_v2`，在旧 `clipboard_core_open` 参数之后增加 16 字节 vault UUID；旧入口继续使用 nil UUID 以保持阶段 1 ABI，Windows 客户端只调用 v2。ABI 测试证明 v2 写入记录使用传入 vault id，错误 UUID 长度返回 `InvalidArgument`。

- [ ] **Step 3: 运行红灯测试**

Run: `cargo test -p clipboard-core --test image_workflow`

Run: `cargo test -p clipboard-ffi --test image_abi`

Expected: FAIL，图片对象存储和 ABI 符号尚未定义。

- [ ] **Step 4: 实现事务式对象存储**

`CoreService` 持有自动零化的 vault key。图片使用 `VaultKey::derive(vault_id, KeyPurpose::Image)` 和 `ObjectCipher`。先把密文写入 `objects/.pending/{object_id}.tmp`，刷新并原子重命名，再提交数据库；数据库失败时删除对象，Core 打开时清理遗留 `.pending` 文件。AAD 固定包含 vault、object、type=image、version=1。最大原始 PNG 为 50 MiB；规范化 PNG 使用与文本相同的 keyed fingerprint 合并连续重复内容。

- [ ] **Step 5: 实现图片 ABI 并运行测试**

所有指针检查、panic 捕获和 `CoreBuffer` 所有权规则与现有 ABI 相同。PNG 从不进入 JSON，也不写日志。

Run: `cargo test -p clipboard-core --test image_workflow`

Run: `cargo test -p clipboard-ffi --test image_abi`

Run: `cargo clippy --workspace --all-targets -- -D warnings`

Expected: 图片往返、篡改拒绝和 ABI 边界全部 PASS。

- [ ] **Step 6: 提交图片对象能力**

```powershell
git add crates/clipboard-core crates/clipboard-ffi tests/fixtures
git commit -m "feat(core): store encrypted clipboard images"
```

### Task 3: 实现 C# Core 客户端和 DPAPI 保险库启动

**Files:**
- Create: `src/Clipboard.Windows/Core/CoreStatus.cs`
- Create: `src/Clipboard.Windows/Core/CoreBuffer.cs`
- Create: `src/Clipboard.Windows/Core/NativeMethods.cs`
- Create: `src/Clipboard.Windows/Core/CoreDtos.cs`
- Create: `src/Clipboard.Windows/Core/ClipboardCoreClient.cs`
- Create: `src/Clipboard.Windows/Platform/VaultBootstrapper.cs`
- Test: `tests/Clipboard.Windows.Tests/Core/ClipboardCoreClientTests.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/VaultBootstrapperTests.cs`

- [ ] **Step 1: 写 DTO/缓冲区和保险库失败测试**

测试 JSON 使用 snake_case，`SafeHandle` 只关闭一次，响应缓冲在 JSON 失败时仍释放。`VaultBootstrapper` 首次生成 32 字节随机密钥和随机 vault UUID，密钥用 `ProtectedData.Protect(..., CurrentUser)` 保存，二次打开返回相同值；文件权限和 DPAPI 错误不得把密钥写入异常文本。客户端打开时必须调用 `clipboard_core_open_v2`。

- [ ] **Step 2: 运行红灯测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "Core|Vault"`

Expected: FAIL，C# Core 客户端和保险库启动类型未定义。

- [ ] **Step 3: 实现窄 FFI 包装**

`ClipboardCoreClient` 公开 `SearchAsync`、`IngestTextAsync`、`IngestImageAsync`、`ReadImageAsync`、`SetFavoriteAsync`、`DeleteAsync`、`ClearUnfavoriteAsync`、`ApplyRetentionAsync`。所有 native 调用串行化；每次把 key 和临时 UTF-8 缓冲在 `finally` 中清零；非 `Ok` 状态转换为不含请求正文的 `ClipboardCoreException`。

- [ ] **Step 4: 实现 DPAPI 启动并运行测试**

密钥路径为 `%LOCALAPPDATA%\Clipboard\vault.key`，数据库和对象目录为 `%LOCALAPPDATA%\Clipboard\data`。写入使用同目录临时文件和 `File.Move(..., overwrite: true)`；不得使用明文配置回退。

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "Core|Vault"`

Expected: 所有测试 PASS。

- [ ] **Step 5: 提交 Windows Core 客户端**

```powershell
git add src/Clipboard.Windows/Core src/Clipboard.Windows/Platform/VaultBootstrapper.cs tests/Clipboard.Windows.Tests
git commit -m "feat(windows): connect WinUI client to clipboard core"
```

### Task 4: 捕获文本和图片并抑制自身写回

**Files:**
- Create: `src/Clipboard.Windows/Platform/IClipboardReader.cs`
- Create: `src/Clipboard.Windows/Platform/WindowsClipboardReader.cs`
- Create: `src/Clipboard.Windows/Platform/PngNormalizer.cs`
- Create: `src/Clipboard.Windows/Platform/SourceApplicationResolver.cs`
- Create: `src/Clipboard.Windows/Platform/ClipboardSuppression.cs`
- Create: `src/Clipboard.Windows/Platform/ClipboardCaptureCoordinator.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/ClipboardCaptureCoordinatorTests.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/ClipboardSuppressionTests.cs`

- [ ] **Step 1: 写捕获协调器失败测试**

使用 fake reader/core 测试：Unicode 文本写入一次；图片规范化后写入一次；连续相同事件由内核合并；剪贴板锁定按 20/50/100/200 ms 最多重试四次；来源识别失败使用 `unknown`；数据库失败触发通知但不清空历史。

- [ ] **Step 2: 写抑制令牌失败测试**

令牌包含 `kind + SHA-256(content) + expires_at`，只消费一次，5 秒后过期。不同文本或图片不能误抑制。

- [ ] **Step 3: 运行红灯测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "Capture|Suppression"`

Expected: FAIL，捕获和抑制类型未定义。

- [ ] **Step 4: 实现 Windows 剪贴板读取**

订阅 `Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged`。文本使用 `StandardDataFormats.Text`；图片使用 `StandardDataFormats.Bitmap` 和 `BitmapDecoder`/`BitmapEncoder` 统一输出 PNG。阶段 2 忽略 `StorageItems`，由阶段 3 接入 `CF_HDROP`。所有 WinRT 调用回到 DispatcherQueue，不在事件回调中阻塞 UI。

- [ ] **Step 5: 实现来源应用与协调器**

来源通过 `GetClipboardOwner`、`GetWindowThreadProcessId` 和 `Process.ProcessName` 尽力识别，任何访问拒绝都返回 `unknown`。捕获成功后触发只含 item id/kind 的刷新事件，不携带正文日志。

- [ ] **Step 6: 运行测试并提交**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "Capture|Suppression"`

```powershell
git add src/Clipboard.Windows/Platform tests/Clipboard.Windows.Tests/Platform
git commit -m "feat(windows): capture text and image clipboard history"
```

### Task 5: 安全恢复原窗口并立即粘贴

**Files:**
- Create: `src/Clipboard.Windows/Platform/NativeMethods.cs`
- Create: `src/Clipboard.Windows/Platform/IForegroundWindowService.cs`
- Create: `src/Clipboard.Windows/Platform/ForegroundWindowService.cs`
- Create: `src/Clipboard.Windows/Platform/IClipboardWriter.cs`
- Create: `src/Clipboard.Windows/Platform/WindowsClipboardWriter.cs`
- Create: `src/Clipboard.Windows/Platform/PasteCoordinator.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/PasteCoordinatorTests.cs`

- [ ] **Step 1: 写立即粘贴失败测试**

测试顺序固定为：读取完整内容、登记抑制令牌、写系统剪贴板、隐藏面板、恢复原 HWND、确认 `GetForegroundWindow` 等于原 HWND、发送 `Ctrl+V`。原窗口为零、已销毁或恢复失败时不得调用 `SendInput`，但成功写入的剪贴板内容保留并返回 `ManualPasteRequired`。

- [ ] **Step 2: 运行红灯测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter PasteCoordinator`

Expected: FAIL，粘贴协调器未定义。

- [ ] **Step 3: 实现文本/图片写回**

文本使用 `DataPackage.SetText`；图片把 Core 返回的 PNG 放入 `InMemoryRandomAccessStream` 后调用 `DataPackage.SetBitmap`；随后 `Clipboard.SetContent` 和 `Clipboard.Flush`。空内容和解码失败不得改写剪贴板。

- [ ] **Step 4: 实现窗口恢复和 SendInput**

只在 `IsWindow(originalHwnd)` 且 `SetForegroundWindow` 后轮询 250 ms 确认前台窗口匹配时发送四个 INPUT：Ctrl down、V down、V up、Ctrl up。发送数量不为 4 返回手动粘贴降级。

- [ ] **Step 5: 运行测试并提交**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter PasteCoordinator`

```powershell
git add src/Clipboard.Windows/Platform tests/Clipboard.Windows.Tests/Platform/PasteCoordinatorTests.cs
git commit -m "feat(windows): paste clipboard items into the source window"
```

### Task 6: 接管快捷键并把面板锚定到光标

**Files:**
- Create: `src/Clipboard.Windows/Platform/PanelPlacement.cs`
- Create: `src/Clipboard.Windows/Platform/GlobalShortcutService.cs`
- Create: `src/Clipboard.Windows/Platform/HotkeyChord.cs`
- Create: `src/Clipboard.Windows/Platform/WindowPresenter.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/PanelPlacementTests.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/HotkeyChordTests.cs`

- [ ] **Step 1: 写定位和快捷键解析失败测试**

定位测试覆盖右下、右上、左下、左上翻转，100%/150%/200% DPI，任务栏占用后的工作区和 70% 最大高度。快捷键测试覆盖 `Alt+V`、`Ctrl+Shift+V`、无修饰键拒绝和系统保留组合拒绝。

- [ ] **Step 2: 运行红灯测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "PanelPlacement|HotkeyChord"`

Expected: FAIL，定位和快捷键类型未定义。

- [ ] **Step 3: 实现 Win+V 钩子和备用热键**

专用 STA 消息线程安装 `WH_KEYBOARD_LL`，仅在 Win 键按下时拦截 V down/up，并通过 DispatcherQueue 请求打开面板。安装失败、策略阻止或用户禁用接管时，用 `RegisterHotKey` 注册设置中的备用组合，默认 `Alt+V`。状态以 `Intercepted | Fallback | Unavailable` 暴露给 UI。

- [ ] **Step 4: 实现窗口样式与定位**

打开前记录前台 HWND、`GetCursorPos`、`MonitorFromPoint`、`GetMonitorInfo` 和 `GetDpiForMonitor`。窗口逻辑尺寸 386x500，高度不超过工作区 70%；`AppWindow` 无标题栏、工具窗口、不进任务栏，按纯函数结果移动。`Esc` 隐藏，失焦隐藏，但设置窗口打开时不隐藏。

- [ ] **Step 5: 运行测试并提交**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "PanelPlacement|HotkeyChord"`

```powershell
git add src/Clipboard.Windows/Platform tests/Clipboard.Windows.Tests/Platform
git commit -m "feat(windows): open clipboard panel from global shortcuts"
```

### Task 7: 实现面板 ViewModel、即时搜索和设置

**Files:**
- Create: `src/Clipboard.Windows/ViewModels/ObservableObject.cs`
- Create: `src/Clipboard.Windows/ViewModels/ClipboardItemViewModel.cs`
- Create: `src/Clipboard.Windows/ViewModels/ClipboardPanelViewModel.cs`
- Create: `src/Clipboard.Windows/ViewModels/SettingsViewModel.cs`
- Create: `src/Clipboard.Windows/Platform/ClientSettings.cs`
- Create: `src/Clipboard.Windows/Platform/ClientSettingsStore.cs`
- Test: `tests/Clipboard.Windows.Tests/ViewModels/ClipboardPanelViewModelTests.cs`
- Test: `tests/Clipboard.Windows.Tests/ViewModels/SettingsViewModelTests.cs`

- [ ] **Step 1: 写 ViewModel 红灯测试**

测试：输入后 50 ms debounce 只执行最新搜索；空查询列出最近记录；无效正则设置输入错误且保留上一次有效结果；方向键改变选中项，Enter 调用粘贴，Esc 请求关闭；来源、时间和内容类型筛选组合传给 Rust；刷新取消旧请求。

- [ ] **Step 2: 写设置失败测试**

默认值为 1000 条、30 天、1 GiB 图片、备用 `Alt+V`、Win+V 尝试开启。三个上限都支持关闭；数字必须大于零；保存后调用 `ApplyRetention` 并重新注册快捷键。开机启动开关失败时回滚 UI 值并显示脱敏错误。

- [ ] **Step 3: 运行红灯测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "ViewModel|Settings"`

Expected: FAIL，ViewModel 和设置存储未定义。

- [ ] **Step 4: 实现 ViewModel 和原子设置文件**

设置保存到 `%LOCALAPPDATA%\Clipboard\settings.json`，使用临时文件原子替换。设置不包含密钥、剪贴板正文或云凭据。ViewModel 通过接口依赖 Core、粘贴、快捷键和通知服务，测试不启动 WinUI。

- [ ] **Step 5: 运行测试并提交**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "ViewModel|Settings"`

```powershell
git add src/Clipboard.Windows/ViewModels src/Clipboard.Windows/Platform/ClientSettings* tests/Clipboard.Windows.Tests/ViewModels
git commit -m "feat(windows): add clipboard panel and settings state"
```

### Task 8: 构建 Windows 11 原生剪贴板界面

**Files:**
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml`
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml.cs`
- Create: `src/Clipboard.Windows/Views/SettingsWindow.xaml`
- Create: `src/Clipboard.Windows/Views/SettingsWindow.xaml.cs`
- Create: `src/Clipboard.Windows/Views/Converters.cs`
- Create: `src/Clipboard.Windows/Platform/TrayIconService.cs`
- Create: `src/Clipboard.Windows/Platform/StartupService.cs`
- Modify: `src/Clipboard.Windows/App.xaml`
- Modify: `src/Clipboard.Windows/App.xaml.cs`

- [ ] **Step 1: 实现单一剪贴板顶栏**

主面板只显示剪贴板 SymbolIcon、`剪贴板`、本机/同步健康状态和 `全部清除`。不得创建表情、GIF、颜文字或符号 Tab，也不得保留空 Tab 间距。标题字号 20，正文 14，卡片圆角不超过 8，窗口逻辑宽 386。

- [ ] **Step 2: 实现搜索、筛选和虚拟化列表**

搜索框右侧是 `.*` ToggleButton 和漏斗图标 MenuFlyout；筛选项使用时间、来源应用、内容类型复选菜单。列表使用 `ItemsRepeater`/虚拟化 `ListView`，固定可用高度，不因条目数量改变窗口。加载、空结果、无效正则和数据库错误有独立状态。

- [ ] **Step 3: 实现原生记录卡片**

文本卡显示最多 6 行摘要；图片卡异步读取 PNG 并使用统一缩略图区域显示，下面展示尺寸；底栏显示来源应用和复制时间。每卡右上为更多菜单，右下为 Pin 图标按钮。点击主区域立即粘贴，更多菜单提供复制、收藏/取消收藏和删除。

- [ ] **Step 4: 实现设置窗口、托盘和启动项**

设置窗口使用 NumberBox/ToggleSwitch/ComboBox：最大条数、天数、图片 GiB、Win+V 尝试、备用快捷键、开机启动和主题。托盘菜单为“打开剪贴板”“设置”“退出”；开机启动使用当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值只包含带引号的 exe 路径。

- [ ] **Step 5: 适配系统主题和可访问性**

颜色全部来自 WinUI ThemeResource；支持浅色、深色、高对比度。动画尊重系统 `UISettings.AnimationsEnabled`。所有图标按钮有 AutomationProperties.Name 和 ToolTip；Tab/方向键焦点顺序可预测。

- [ ] **Step 6: 构建并启动人工视觉检查**

Run: `dotnet build src/Clipboard.Windows/Clipboard.Windows.csproj -c Debug -p:Platform=x64`

Run: `src\Clipboard.Windows\bin\Debug\net8.0-windows10.0.26100.0\win-x64\Clipboard.Windows.exe`

Expected: 面板在光标附近打开，外观与 Windows 11 原生剪贴板一致，顶栏只有剪贴板；在 100% 和 150% DPI 下无重叠、裁切或文本溢出。

- [ ] **Step 7: 提交 WinUI 体验**

```powershell
git add src/Clipboard.Windows
git commit -m "feat(windows): build native clipboard history panel"
```

### Task 9: 建立阶段 2 验证入口和 Windows CI

**Files:**
- Create: `scripts/verify-windows-client.ps1`
- Modify: `.github/workflows/core.yml`
- Modify: `README.md`

- [ ] **Step 1: 写单一验证脚本**

脚本接受 `[switch]$SkipGuiSmoke`；默认依次执行阶段 1 `scripts/test-core.ps1`、Windows 工具链检查、`dotnet test`、WinUI x64 build，以及启动后 10 秒进程存活/单实例握手 smoke。GUI smoke 不操作真实剪贴板，不采集屏幕或日志正文。

- [ ] **Step 2: 运行完整自动验证**

Run: `pwsh -NoProfile -File scripts/verify-windows-client.ps1`

Expected final line: `Clipboard Windows client verification passed.`，此前无 warning、失败测试或未处理异常。

- [ ] **Step 3: 更新 Windows CI**

新增 `windows-client` job，使用 `windows-2022`、Rust 1.88、.NET 8 和 Windows App SDK NuGet 缓存；运行 `verify-windows-client.ps1 -SkipGuiSmoke`。CI 不上传数据库、截图、日志或本机剪贴板样本。

- [ ] **Step 4: 写人工系统验收清单**

README 记录必须人工验证：记事本、Edge、Office、VS Code 的文本/图片捕获与粘贴；Win+V 成功/失败降级；自身写回无重复；100/150/200% DPI 和多显示器边缘；原窗口失效时不发送 Ctrl+V。

- [ ] **Step 5: 提交阶段验证**

```powershell
git add scripts .github/workflows/core.yml README.md
git commit -m "ci(windows): verify local clipboard client"
```

## 阶段 2 完成检查

- [ ] `git status --short` 无未提交文件。
- [ ] `pwsh -NoProfile -File scripts/verify-windows-client.ps1` 本机通过。
- [ ] 文本和图片捕获在数据库提交后才刷新 UI，自身写回不会重复记录。
- [ ] 图片对象以认证密文保存，篡改后失败关闭。
- [ ] 搜索、正则、时间/来源/类型筛选和无效正则保留旧结果均通过。
- [ ] 收藏、删除、全部清除和三个保留上限真实持久化，收藏项不自动清理。
- [ ] Win+V 接管失败时备用快捷键保持应用可用。
- [ ] 面板在光标附近并约束于工作区，100/150/200% DPI 无重叠。
- [ ] 只有确认原 HWND 恢复后才发送 Ctrl+V。
- [ ] README 明确阶段 2 不包含文件历史和跨设备同步。

完成这些检查后，才编写阶段 3 本机文件与文件夹历史计划。
