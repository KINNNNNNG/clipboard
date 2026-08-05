# Task 4 文件历史卡片与来源名称实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Windows 剪贴板面板以 Fluent 文件/文件夹卡片显示本机文件历史，支持文件筛选、键盘高亮与自动滚动，并把来源 exe 显示为正常软件名称。

**Architecture:** Rust Core 保留完整路径用于本机搜索和回放，但搜索响应只返回代表名称、类型和条数；数据库保留稳定 `source_app` 标识并新增可选展示名称。Windows 客户端负责版本信息解析、卡片呈现、ListView 选中同步和路径失效状态，现有 Win+V、窗口定位与文本/图片流程不变。

**Tech Stack:** Rust 1.88、SQLite/SQLCipher、Serde JSON、.NET 8、WinUI 3、Win32 `FileVersionInfo`、xUnit。

---

### Task 1: Core 文件束摘要与来源展示字段

**Files:**
- Create: `crates/clipboard-storage/migrations/003_source_app_display_name.sql`
- Modify: `crates/clipboard-domain/src/item.rs`
- Modify: `crates/clipboard-core/src/command.rs`
- Modify: `crates/clipboard-core/src/response.rs`
- Modify: `crates/clipboard-core/src/service.rs`
- Modify: `crates/clipboard-storage/src/item_repository.rs`
- Test: `crates/clipboard-core/tests/file_bundle_workflow.rs`
- Test: `crates/clipboard-core/tests/local_workflow.rs`
- Create: `crates/clipboard-storage/tests/migrations.rs`

- [ ] **Step 1: 写 Core 红灯测试**

在 `file_bundle_workflow.rs` 增加断言：搜索一个包含 `C:\Docs\report.docx` 和 `C:\Photos` 的文件束时，返回 `kind == "file_bundle"`、`preview == "report.docx"`、`file_count == 2`、`representative_name == "report.docx"`，并且 JSON 不包含完整路径；用完整路径搜索仍能命中同一条记录。增加 `source_app_display_name` 摄取和回读断言。

在 `migrations.rs` 用旧版数据库打开后断言新增列为空、历史文本仍可读。先运行：

```powershell
cargo test -p clipboard-core --test file_bundle_workflow
cargo test -p clipboard-storage --test migrations
```

预期：因 SearchItem 缺少摘要字段、数据库列不存在而失败。

- [ ] **Step 2: 添加可空展示字段和迁移**

新增迁移：

```sql
ALTER TABLE clipboard_items ADD COLUMN source_app_display_name TEXT;
```

给 `ClipboardItem` 增加 `source_app_display_name: Option<String>`，构造函数接收可选值；`ItemRepository` 的 `SELECT`、`INSERT`、`UPDATE` 和解码元组同步读写该列，旧行保持 `None`。所有现有构造调用传 `None` 或请求中的展示名称。

在 `IngestText`、`IngestImage`、`IngestFileBundle` 中加入 `#[serde(default)] pub source_app_display_name: Option<String>`。判重更新记录时同时更新展示名称；请求缺失时保留旧值。

- [ ] **Step 3: 生成脱敏文件摘要**

在 `SearchItem` 增加可选 `source_app_display_name`、`file_count`、`representative_name`、`representative_kind` 字段。`search_item` 对文件束使用路径最后非空段作为代表名，按条目类型输出 `file`/`directory`，空名回退为“文件”或“文件夹”；`preview` 只返回代表名。`searchable_fields` 保留完整路径，不能把路径写入 `SearchItem`。

来源筛选同时匹配 `source_app`、展示名称和去掉 `.exe` 的标识，均使用大小写不敏感精确匹配。

- [ ] **Step 4: 运行 Core、存储回归测试并提交**

```powershell
cargo test -p clipboard-core --test file_bundle_workflow
cargo test -p clipboard-core --test local_workflow
cargo test -p clipboard-storage --test migrations
cargo fmt --all -- --check
git diff --check
git add crates/clipboard-domain crates/clipboard-core crates/clipboard-storage
git commit -m "feat(core): expose file card summaries and app names"
```

预期所有测试通过，文件束仍不进入 `sync_outbox`。

### Task 2: C# DTO 与来源应用友好名称解析

**Files:**
- Modify: `src/Clipboard.Windows/Core/CoreDtos.cs`
- Modify: `src/Clipboard.Windows/Core/ClipboardCoreClient.cs`
- Modify: `src/Clipboard.Windows/Platform/ClipboardCaptureCoordinator.cs`
- Modify: `src/Clipboard.Windows/Platform/SourceApplicationResolver.cs`
- Test: `tests/Clipboard.Windows.Tests/Core/ClipboardCoreClientTests.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/ClipboardCaptureCoordinatorTests.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/SourceApplicationResolverTests.cs`

- [ ] **Step 1: 写来源解析和 JSON 红灯测试**

测试 `SourceApplicationResolver.FormatDisplayName` 的优先级：FileDescription、ProductName、系统映射、去除 `.exe`；断言结果最多 128 个 UTF-16 字符且不含路径。更新 Core 客户端测试，断言搜索响应能反序列化新摘要字段，摄取文本/图片/文件束请求携带可选展示名称。

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "ClipboardCoreClientTests|SourceApplicationResolverTests|ClipboardCaptureCoordinatorTests"
```

预期：DTO 字段和解析方法尚不存在而失败。

- [ ] **Step 2: 实现来源解析**

新增 `SourceApplicationInfo(string Identifier, string DisplayName)`；`Resolve()` 先得到窗口所属进程名和规范化 exe 标识，再通过 `Process.MainModule?.FileName` 的 `FileVersionInfo.GetVersionInfo` 读取 FileDescription/ProductName。仅保留名称文本，不返回路径；失败时使用 `explorer.exe`、`notepad.exe`、`mspaint.exe` 映射或移除 `.exe` 的标识。

- [ ] **Step 3: 贯通捕获请求和响应 DTO**

`ISourceApplicationResolver.Resolve()` 返回 `SourceApplicationInfo`；捕获协调器把 Identifier 写入 `SourceApp`、DisplayName 写入 `SourceAppDisplayName`。`ClipboardItemDto` 增加可选展示名称和文件摘要字段；`FileEntryKindDto` 的 JSON 现有 snake_case 契约不变。

图片元数据 JSON 也增加可选展示名称，旧 ABI 请求缺失时使用 `null`。运行 Step 1 的定向测试确认旧请求仍可反序列化。

- [ ] **Step 4: 提交 C# 协议适配**

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "ClipboardCoreClientTests|SourceApplicationResolverTests|ClipboardCaptureCoordinatorTests"
git add src/Clipboard.Windows/Core src/Clipboard.Windows/Platform/ClipboardCaptureCoordinator.cs src/Clipboard.Windows/Platform/SourceApplicationResolver.cs tests/Clipboard.Windows.Tests/Core tests/Clipboard.Windows.Tests/Platform
git commit -m "feat(windows): resolve friendly source application names"
```

### Task 3: 文件卡片、文件筛选和键盘高亮滚动

**Files:**
- Modify: `src/Clipboard.Windows/ViewModels/ClipboardItemViewModel.cs`
- Modify: `src/Clipboard.Windows/ViewModels/ClipboardPanelViewModel.cs`
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml`
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml.cs`
- Modify: `src/Clipboard.Windows/Views/Converters.cs`
- Modify: `tests/Clipboard.Windows.Tests/ViewModels/ClipboardDisplayFormatterTests.cs`
- Modify: `tests/Clipboard.Windows.Tests/ViewModels/ClipboardPanelViewModelTests.cs`
- Modify: `tests/Clipboard.Windows.Tests/Views/XamlResourceConfigurationTests.cs`

- [ ] **Step 1: 写 ViewModel/XAML 红灯测试**

增加测试：文件束卡片显示代表名和 `共 N 项`，`IsFileBundle` 为真，来源显示名称优先于 exe；三种筛选同时传递 `file_bundle`；`MainWindow.xaml` 含文件筛选复选框、文件图标和 `SelectionMode="Single"`，不再把文件卡绑定到完整路径预览。

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "ClipboardDisplayFormatterTests|ClipboardPanelViewModelTests|XamlResourceConfigurationTests"
```

预期：新 DTO 字段、卡片属性和 XAML 契约尚不存在而失败。

- [ ] **Step 2: 实现卡片摘要和 Fluent 图标**

在 `ClipboardItemViewModel` 暴露 `IsFileBundle`、`FileIconGlyph`、`FileNameSummary`、`FileCountLabel`、`DisplaySourceApp` 和 `IsSourceUnavailable`。文件图标仅使用 Fluent `FontIcon` glyph；单文件/文件夹按 `representative_kind` 选择，多条目仍使用第一个条目类型。

`MainWindow.xaml` 将通用文本预览限制为文本类型，增加文件卡片区和 `共 N 项` 文本；保留图片预览、卡片圆角、收藏和更多操作。

- [ ] **Step 3: 接入文件筛选**

在筛选 Flyout 增加 `FileKindCheckBox`，默认选中；`ApplyFiltersButton_Click` 把 `text`、`image`、`file_bundle` 按勾选状态传给 `SetFilters`。现有来源和时间筛选不改动。

- [ ] **Step 4: 实现选中高亮和自动滚动**

将 `HistoryList.SelectionMode` 改为 `Single`，在扁平 `ListViewItem` 模板保留内容布局并加入 `Selected` VisualState，使用主题选中背景和边框。`MainWindow.Configure` 订阅 ViewModel 的 `SelectedIndex` 变化，同步 `HistoryList.SelectedIndex`；选中项存在时调用 `HistoryList.ScrollIntoView(selected, ScrollIntoViewAlignment.Default)`。`SelectionChanged` 反向调用 `SelectIndex`，避免点击或方向键状态脱节。边界索引继续钳制在 `0..Items.Count-1`。

- [ ] **Step 5: 运行视图测试并提交**

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "ClipboardDisplayFormatterTests|ClipboardPanelViewModelTests|XamlResourceConfigurationTests"
git add src/Clipboard.Windows/ViewModels src/Clipboard.Windows/Views tests/Clipboard.Windows.Tests/ViewModels tests/Clipboard.Windows.Tests/Views
git commit -m "feat(windows): display file cards and keyboard selection"
```

### Task 4: 路径失效反馈和文件复制菜单

**Files:**
- Modify: `src/Clipboard.Windows/Platform/PasteCoordinator.cs`
- Modify: `src/Clipboard.Windows/Platform/WindowsClipboardWriter.cs`
- Modify: `src/Clipboard.Windows/ViewModels/ClipboardPanelViewModel.cs`
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml.cs`
- Test: `tests/Clipboard.Windows.Tests/Platform/PasteCoordinatorTests.cs`
- Test: `tests/Clipboard.Windows.Tests/ViewModels/ClipboardPanelViewModelTests.cs`

- [ ] **Step 1: 写路径失效红灯测试**

让 FakeWriter 对文件束返回 `FileNotFoundException`，断言 `PasteResultKind.SourceUnavailable`，且 foreground restore、send input、hide panel 均为 0 次；ViewModel 卡片标记不可用并保留删除/收藏能力。更多菜单的“复制”对文件束必须调用 `WriteFilesAsync`，不能把代表名称写成文本。

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "PasteCoordinatorTests|ClipboardPanelViewModelTests"
```

- [ ] **Step 2: 实现安全失败路径**

`WindowsClipboardWriter.WriteFilesAsync` 将文件不存在、目录不存在、权限失败和 WinRT COM 读取失败统一转换为 `FileNotFoundException`，在创建 `DataPackage` 前完成全部验证。`PasteCoordinator` 捕获该异常、撤销文件束抑制并返回 `SourceUnavailable`，不执行隐藏窗口、焦点恢复或 `SendPasteInput`。ViewModel 更新当前卡片状态并报告“原路径不可用”。

- [ ] **Step 3: 修复更多菜单的文件复制**

`MainWindow.CopyItemAsync` 按 `file_bundle` 调用 `ReadFileBundleAsync` 和 `WriteFilesAsync`；只对文本调用 `WriteTextAsync`，只对图片调用 `WriteImageAsync`。异常继续由现有 UI 操作包装器呈现，不泄露路径。

- [ ] **Step 4: 运行平台测试并提交**

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "PasteCoordinatorTests|ClipboardPanelViewModelTests"
git add src/Clipboard.Windows/Platform src/Clipboard.Windows/ViewModels src/Clipboard.Windows/Views/MainWindow.xaml.cs tests/Clipboard.Windows.Tests/Platform tests/Clipboard.Windows.Tests/ViewModels
git commit -m "fix(windows): report unavailable file history safely"
```

### Task 5: 阶段验证和提交前检查

**Files:**
- Modify: `README.md`
- Modify: `scripts/verify-windows-client.ps1`
- Test: `crates/clipboard-storage/tests/local_only_outbox.rs`

- [ ] **Step 1: 增加 local_only 隐私回归**

断言搜索摘要、代表名称和来源展示名称不会让文件束进入 outbox；测试事件 JSON 不含 Windows 文件路径或文件名。

- [ ] **Step 2: 更新文档和验证脚本**

README 标记文件捕获、卡片展示和安全复制已交付，明确收藏缓存仍属于下一任务；验证脚本加入 Core 文件束和 Windows 定向测试。

- [ ] **Step 3: 完整验证**

```powershell
cargo test --workspace
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore
dotnet build src/Clipboard.Windows/Clipboard.Windows.csproj -c Debug -p:Platform=x64 -p:WindowsAppSDKSelfContained=true --no-restore
pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke
git diff --check
```

手工验收：资源管理器复制单个文件、多个文件和文件夹；按上下键观察高亮和自动滚动；检查列表显示 Fluent 图标、`共 N 项` 和友好来源名；对已删除文件确认不写剪贴板、不发送 Ctrl+V。

- [ ] **Step 4: 提交验证文档**

```powershell
git add README.md scripts/verify-windows-client.ps1 crates/clipboard-storage/tests/local_only_outbox.rs
git commit -m "test(windows): verify file history task 4"
```
