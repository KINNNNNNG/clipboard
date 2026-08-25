# OSS 连接修复与全局日志 Implementation Plan

> **回填状态：** 截至 2026-08-25，已按 `codex/phase4-image-sync` 的 `2e4c3f6` 回填。
> `[x]` 表示该步骤的最终交付结果可由当前代码、提交或自动测试证明；不重新声称历史红灯命令的原始输出仍可复现。
> `[ ]` 仅保留给尚未完成的真实 OSS 连接、立即同步和日志人工验收；当前总状态见 [Clipboard 开发状态](../../STATUS.md)。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复阿里云 OSS V4 签名与诊断信息，并提供可从系统托盘打开、可持久化配置的全局日志管理能力。

**Architecture:** Rust 同步层将请求规范化、签名和服务端错误码提取限定为无秘密的纯函数，再以 fixture 覆盖真实请求边界。Windows 层提供结构化异步日志服务、文件保留策略和日志查看器；设置模型只保存日志策略，运行时服务以原子方式切换阈值。

**Tech Stack:** Rust、reqwest、quick-xml、C#/.NET 8、WinUI 3、xUnit。

---

### Task 1: OSS V4 签名回归

**Files:**
- Modify: `crates/clipboard-sync/src/oss.rs`
- Modify: `crates/clipboard-sync/tests/oss.rs`

- [x] **Step 1: 写入失败的签名边界测试**

在 `tests/oss.rs` 中新增断言：ListObjectsV2 的空 prefix 为 `prefix=`；PUT 的 Authorization 包含 `AdditionalHeaders=if-none-match`；COPY 的 Authorization 包含 `AdditionalHeaders=if-none-match;x-oss-copy-source`；这些 Header 也实际传输。

- [x] **Step 2: 运行测试并确认失败**

Run: `cargo test -p clipboard-sync --test oss oss_publishes_by_copying_completed_object_then_deleting_pending -- --exact`

Expected: FAIL，因为 Authorization 现为 `AdditionalHeaders=`。

- [x] **Step 3: 实现最小签名修复**

在 `oss.rs` 中将 Header 名称小写并规范化空白；以 `additional_headers.keys()` 生成逗号分隔的 `AdditionalHeaders`，以全部规范 Header 生成分号分隔的 `signed_headers`；对 path/query 使用 URL 已编码的规范表达；`list_url` 对空 prefix 传递 `""`。

- [x] **Step 4: 运行 OSS fixture 测试**

Run: `cargo test -p clipboard-sync --test oss`

Expected: PASS。

- [x] **Step 5: 提交**

Run: `git add crates/clipboard-sync/src/oss.rs crates/clipboard-sync/tests/oss.rs && git commit -m "fix(sync): sign OSS V4 additional headers"`

### Task 2: OSS 脱敏错误诊断

**Files:**
- Modify: `crates/clipboard-sync/src/oss.rs`
- Modify: `crates/clipboard-sync/tests/oss.rs`

- [x] **Step 1: 写入失败的错误码提取测试**

在 `tests/oss.rs` 用包含 `SignatureDoesNotMatch`、`AccessDenied`、`NoSuchBucket` 与敏感 XML message 的 fixture 调用 probe，断言错误仍映射为固定 `SyncError`，并断言诊断帮助函数只返回白名单码、不含 XML message 或密钥。

- [x] **Step 2: 运行测试并确认失败**

Run: `cargo test -p clipboard-sync --test oss oss_extracts_only_allowlisted_error_codes -- --exact`

Expected: FAIL，因为当前响应体被直接丢弃且没有错误码提取函数。

- [x] **Step 3: 实现最小脱敏提取**

在 `oss.rs` 中读取失败响应的有限长度 XML，使用 quick-xml 仅读取 `<Code>`，仅返回固定白名单中的代码；继续以状态码决定 `SyncError`，不暴露响应正文、端点、对象键或凭据。

- [x] **Step 4: 运行同步 crate 测试**

Run: `cargo test -p clipboard-sync`

Expected: PASS。

- [x] **Step 5: 提交**

Run: `git add crates/clipboard-sync/src/oss.rs crates/clipboard-sync/tests/oss.rs && git commit -m "feat(sync): expose sanitized OSS diagnostics"`

### Task 3: 日志数据模型与文件存储

**Files:**
- Create: `src/Clipboard.Windows/Platform/GlobalLog.cs`
- Modify: `src/Clipboard.Windows/Platform/ClientSettings.cs`
- Modify: `tests/Clipboard.Windows.Tests/Platform/ClientSettingsTests.cs`
- Create: `tests/Clipboard.Windows.Tests/Platform/GlobalLogTests.cs`

- [x] **Step 1: 写入失败的日志服务测试**

在 `GlobalLogTests.cs` 构造临时日志目录，断言 `Info` 阈值过滤 Trace、写入 UTC/级别/组件/事件名，写入器拒绝包含 `secret`、`authorization`、`password`、`path` 的字段；写入过期和超限文件后断言保留 7 天、总量不超过 200 MB。

- [x] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter FullyQualifiedName~GlobalLogTests`

Expected: FAIL，因为 `GlobalLog` 尚不存在。

- [x] **Step 3: 实现最小日志服务**

在 `GlobalLog.cs` 定义 `LogLevel`、`LogEntry`、`IGlobalLog` 与 `FileGlobalLog`；用有界 `Channel<LogEntry>` 后台写入每天一个文件，支持原子阈值更新、快照读取、清空和按日期/总大小清理；字段使用固定白名单，运行时丢弃敏感键和任意异常文本。

- [x] **Step 4: 实现设置默认值与校验**

在 `ClientSettings.cs` 添加 `LoggingSettings(Level, RetentionDays, MaxSizeBytes)`，默认 `Info/7/209715200`，校验级别和 1-30 天、10 MB-1 GB 范围；在 `ClientSettingsTests.cs` 覆盖默认值、边界和无效值。

- [x] **Step 5: 运行 Windows 单元测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "FullyQualifiedName~GlobalLogTests|FullyQualifiedName~ClientSettingsTests"`

Expected: PASS。

- [x] **Step 6: 提交**

Run: `git add src/Clipboard.Windows/Platform/GlobalLog.cs src/Clipboard.Windows/Platform/ClientSettings.cs tests/Clipboard.Windows.Tests/Platform/GlobalLogTests.cs tests/Clipboard.Windows.Tests/Platform/ClientSettingsTests.cs && git commit -m "feat(windows): add retained global file logging"`

### Task 4: 应用与同步边界记录日志

**Files:**
- Modify: `src/Clipboard.Windows/App.xaml.cs`
- Modify: `src/Clipboard.Windows/ViewModels/SettingsViewModel.cs`
- Modify: `src/Clipboard.Windows/Core/ClipboardCoreClient.cs`
- Modify: `tests/Clipboard.Windows.Tests/ViewModels/SettingsViewModelTests.cs`

- [x] **Step 1: 写入失败的同步日志测试**

在 `SettingsViewModelTests.cs` 注入内存 `IGlobalLog`，对连接测试和立即同步的成功/失败路径断言产生 `sync.probe.start/end`、`sync.remote.*` 和固定状态字段，且记录中不出现账户、密钥、端点、路径或错误正文。

- [x] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter FullyQualifiedName~SettingsViewModelTests`

Expected: FAIL，因为 ViewModel 尚未依赖日志服务。

- [x] **Step 3: 实现最小边界记录**

在 `App.xaml.cs` 创建 `FileGlobalLog`、加载设置后应用策略，并记录 `core.open`、`app.exception`；在 `SettingsViewModel.cs` 的保存、探测和同步命令边界记录固定事件名与无秘密字段；在 `ClipboardCoreClient.cs` 记录固定 Core 命令开始/结束，异常仅映射为类别。

- [x] **Step 4: 运行相关测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter FullyQualifiedName~SettingsViewModelTests`

Expected: PASS。

- [x] **Step 5: 提交**

Run: `git add src/Clipboard.Windows/App.xaml.cs src/Clipboard.Windows/ViewModels/SettingsViewModel.cs src/Clipboard.Windows/Core/ClipboardCoreClient.cs tests/Clipboard.Windows.Tests/ViewModels/SettingsViewModelTests.cs && git commit -m "feat(windows): record sanitized sync diagnostics"`

### Task 5: 托盘日志入口与查看器

**Files:**
- Modify: `src/Clipboard.Windows/Platform/TrayIconService.cs`
- Create: `src/Clipboard.Windows/Views/LogWindow.xaml`
- Create: `src/Clipboard.Windows/Views/LogWindow.xaml.cs`
- Create: `src/Clipboard.Windows/ViewModels/LogViewModel.cs`
- Modify: `src/Clipboard.Windows/App.xaml.cs`
- Modify: `tests/Clipboard.Windows.Tests/Platform/ShellIntegrationTests.cs`
- Create: `tests/Clipboard.Windows.Tests/ViewModels/LogViewModelTests.cs`

- [x] **Step 1: 写入失败的托盘与查看器测试**

在 `ShellIntegrationTests.cs` 断言 `TrayCommand.Logs` 调用打开回调；在 `LogViewModelTests.cs` 以临时 `IGlobalLog` 验证级别筛选、关键词搜索、自动刷新、复制可见文本、清空确认和级别更新后设置持久化。

- [x] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "FullyQualifiedName~ShellIntegrationTests|FullyQualifiedName~LogViewModelTests"`

Expected: FAIL，因为 `Logs` 命令与日志查看模型尚不存在。

- [x] **Step 3: 实现托盘路由与单实例窗口**

在 `TrayIconService.cs` 添加 `Logs` 枚举、菜单项与回调；在 `App.xaml.cs` 使用 `SingleWindowLifetime<LogWindow>` 打开窗口并在关闭时释放；在 `LogViewModel.cs` 实现过滤、定时刷新、复制和二次确认清空命令，更新级别时保存 `LoggingSettings` 并立即调用 `SetLevel`。

- [x] **Step 4: 实现 XAML 查看器**

在 `LogWindow.xaml` 使用 ComboBox、搜索框、ToggleSwitch、日志列表、复制和清空按钮、文件数量/总大小/最近写入状态；在 code-behind 绑定 ViewModel、处理关闭和剪贴板复制。控件文字使用中文且不展示敏感数据。

- [x] **Step 5: 运行查看器相关测试**

Run: `dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "FullyQualifiedName~ShellIntegrationTests|FullyQualifiedName~LogViewModelTests"`

Expected: PASS。

- [x] **Step 6: 提交**

Run: `git add src/Clipboard.Windows/Platform/TrayIconService.cs src/Clipboard.Windows/App.xaml.cs src/Clipboard.Windows/Views/LogWindow.xaml src/Clipboard.Windows/Views/LogWindow.xaml.cs src/Clipboard.Windows/ViewModels/LogViewModel.cs tests/Clipboard.Windows.Tests/Platform/ShellIntegrationTests.cs tests/Clipboard.Windows.Tests/ViewModels/LogViewModelTests.cs && git commit -m "feat(windows): add tray global log viewer"`

### Task 6: 完整验证与真实 OSS 验收

**Files:**
- Modify: none unless verification exposes a regression

- [x] **Step 1: 运行 Core 回归套件**

Run: `pwsh -NoProfile -File scripts/test-core.ps1`

Expected: exit 0。

- [x] **Step 2: 运行 Windows 编译与单元测试**

Run: `pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke`

Expected: exit 0。

- [ ] **Step 3: 启动客户端并人工验收**

Run: `Start-Process -WindowStyle Hidden -FilePath .\src\Clipboard.Windows\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\Clipboard.Windows.exe`

Expected: 系统托盘菜单可打开“日志”；在真实 OSS 配置中“测试连接”和“立即同步”完成，日志仅出现脱敏阶段、状态和错误码。

- [ ] **Step 4: 提交验证期间产生的修复**

若前述验证发现回归，仅暂存对应的实现与测试文件（`crates/clipboard-sync/src/oss.rs`、`crates/clipboard-sync/tests/oss.rs`、`src/Clipboard.Windows/Platform/GlobalLog.cs`、`src/Clipboard.Windows/Platform/ClientSettings.cs`、`src/Clipboard.Windows/Platform/TrayIconService.cs`、`src/Clipboard.Windows/ViewModels/LogViewModel.cs`、`src/Clipboard.Windows/Views/LogWindow.xaml`、`src/Clipboard.Windows/Views/LogWindow.xaml.cs`、`src/Clipboard.Windows/App.xaml.cs` 及其对应测试），运行 `git diff --cached --check` 后执行 `git commit -m "fix: address OSS and logging verification"`；若无回归则不创建空提交。
