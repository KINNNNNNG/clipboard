# 原生 DWM 面板边框 Implementation Plan

> **回填状态：** 截至 2026-08-25，已按 `codex/phase4-image-sync` 的 `2e4c3f6` 回填。
> `[x]` 表示该步骤的最终交付结果可由当前代码、提交或自动测试证明；不重新声称历史红灯命令的原始输出仍可复现。
> `[ ]` 仅保留给尚未完成的实际窗口与交互人工验收；当前总状态见 [Clipboard 开发状态](../../STATUS.md)。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让剪贴板面板使用 Windows 11 DWM 自动绘制的灰色边框和圆角，删除导致白边/黑边的手工窗口装饰代码。

**Architecture:** `OverlappedPresenter` 保留边框但隐藏标题栏，DWM 使用默认边框颜色和圆角策略；面板窗口不再使用自定义 region、分层窗口或样式过滤。XAML 顶层恢复为不透明主题背景，内部列表卡片样式保持不变。

**Tech Stack:** .NET 8、WinUI 3/Windows App SDK 1.8、Win32 DWM (`DwmSetWindowAttribute`)、x64 Windows 11。

---

### Task 1: 先固定原生边框契约

**Files:**
- Modify: `tests/Clipboard.Windows.Tests/Platform/PanelPlacementTests.cs`
- Modify: `tests/Clipboard.Windows.Tests/Views/XamlResourceConfigurationTests.cs`

- [x] **Step 1: 把 presenter 预期改为系统边框**

在 `Window_presenter_records_source_window_and_applies_monitor_placement` 中，将 `Assert.False(backend.HasBorder);` 改为 `Assert.True(backend.HasBorder);`，并将调用序列改为只包含 `configure`、`show`、`foreground`。删除 fake backend 的 `HideDwmBorder`、`DwmBorderHidden` 和 `hide-dwm-border` 记录。

- [x] **Step 2: 增加 DWM 默认颜色契约**

在 `Popup_chrome_hides_the_dwm_border_and_keeps_rounded_corners` 中保留圆角常量断言，并增加：

```csharp
Assert.Equal(0xFFFFFFFFu, NativeMethods.DwmColorDefault);
```

- [x] **Step 3: 将 XAML fixture 测试改为不透明顶层**

将顶层窗口测试改为断言名为 `Root` 的元素是 `Grid`，其 `Background` 为 `ApplicationPageBackgroundThemeBrush`，且文档不存在 `DesktopAcrylicBackdrop`、`CornerRadius="12"` 的顶层 surface。

- [x] **Step 4: 运行 targeted tests，确认测试先失败**

运行：

```powershell
dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj --no-restore --filter "Window_presenter_records_source_window_and_applies_monitor_placement|Main_window_uses_a_native_opaque_surface|Popup_chrome_hides_the_dwm_border_and_keeps_rounded_corners"
```

预期：因接口仍有 `HideDwmBorder`、默认颜色常量不存在或 XAML 仍是透明宿主而失败。

### Task 2: 删除手工窗口装饰并切换 DWM 默认边框

**Files:**
- Modify: `src/Clipboard.Windows/Platform/NativeMethods.cs`
- Modify: `src/Clipboard.Windows/Platform/WindowPresenter.cs`

- [x] **Step 1: 缩减 NativeMethods 到 DWM 所需 API**

删除样式索引、`BuildBorderlessWindowStyle`、`BuildBorderlessExtendedStyle`、`WM_STYLECHANGING`、分层窗口、region、`SetWindowPos` 和 subclass 相关声明；保留 `DwmSetWindowAttribute`，并定义：

```csharp
internal const uint DwmwaBorderColor = 34;
internal const uint DwmwaWindowCornerPreference = 33;
internal const uint DwmWindowCornerRound = 2;
internal const uint DwmColorDefault = 0xFFFFFFFF;
```

- [x] **Step 2: 移除 subclass 和边框刷新生命周期**

从 `WinUiWindowPlacementBackend` 删除 `_subclassProc`、`SetWindowSubclass`、`WindowSubclassProc`、`Window_Closed` 和 `Window_Activated`。从 `IWindowPlacementBackend`、`WindowPresenter.Show`、fake backend 中删除 `HideDwmBorder`。

- [x] **Step 3: 使用系统边框和 DWM 默认颜色**

`ConfigureToolWindow` 保留不可调整大小、不可最大化、不可最小化设置，并使用：

```csharp
presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
uint color = NativeMethods.DwmColorDefault;
NativeMethods.DwmSetWindowAttribute(
    PanelWindowHandle,
    NativeMethods.DwmwaBorderColor,
    ref color,
    sizeof(uint));
uint corner = NativeMethods.DwmWindowCornerRound;
NativeMethods.DwmSetWindowAttribute(
    PanelWindowHandle,
    NativeMethods.DwmwaWindowCornerPreference,
    ref corner,
    sizeof(uint));
```

不再调用 `SetWindowLongPtr`、`SetLayeredWindowAttributes`、`SetWindowRgn` 或 `SetWindowPos(SWP_FRAMECHANGED)`。

- [x] **Step 4: 运行 targeted tests，确认转绿**

运行同 Task 1 的 targeted 命令，预期全部通过。

### Task 3: 恢复不透明顶层 XAML

**Files:**
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml`
- Modify: `tests/Clipboard.Windows.Tests/Views/XamlResourceConfigurationTests.cs`

- [x] **Step 1: 删除透明宿主和顶层手工圆角**

将 `Root` 恢复为顶层 `Grid`，使用：

```xml
<Grid
    x:Name="Root"
    Padding="12"
    Background="{ThemeResource ApplicationPageBackgroundThemeBrush}"
    KeyDown="Root_KeyDown">
```

把资源、原有布局、列表和状态栏直接放回该 Grid；保留列表 `ListViewItem` 的扁平模板和内部项目卡片圆角，不修改搜索、筛选和图片预览控件。

- [x] **Step 2: 同步 XML 测试**

测试项目已把运行时 `MainWindow.xaml` 链接为输出目录中的 `Fixtures/MainWindow.xaml`；让 XML 测试只验证顶层 Grid 的主题背景，不再验证透明 Border 或 `CornerRadius="12"`。

- [x] **Step 3: 运行视图测试**

运行：

```powershell
dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj --no-restore --filter FullyQualifiedName~XamlResourceConfigurationTests
```

预期：全部通过。

### Task 4: 全量验证和实际窗口检查

**Files:**
- No additional source files.

- [x] **Step 1: 运行完整单元测试**

```powershell
dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj --no-restore
```

预期：全部测试通过，失败数为 0。

- [x] **Step 2: 构建 x64 可运行版本**

```powershell
dotnet build src\Clipboard.Windows\Clipboard.Windows.csproj -c Debug -p:Platform=x64 -p:WindowsAppSDKSelfContained=true --no-restore
```

预期：0 警告、0 错误，产物为 `src\Clipboard.Windows\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\Clipboard.Windows.exe`。

- [ ] **Step 3: 运行时检查窗口不再使用自定义 region**

启动 x64 程序后，用 Win32 读取窗口样式和 `GetWindowRgnBox`：region 类型应为 0，窗口尺寸仍为 `386x500`；使用 `PrintWindow` 截图确认边缘为系统灰色、圆角存在且没有白边/黑边。

- [ ] **Step 4: 手工回归交互**

确认 Win+V 面板能弹出，点击外部会隐藏，面板内搜索和 Enter 粘贴行为不变，Win 键不会卡住。

- [x] **Step 5: 提交实现**

```powershell
git add src/Clipboard.Windows/Platform/NativeMethods.cs src/Clipboard.Windows/Platform/WindowPresenter.cs src/Clipboard.Windows/Views/MainWindow.xaml tests/Clipboard.Windows.Tests/Platform/PanelPlacementTests.cs tests/Clipboard.Windows.Tests/Views/XamlResourceConfigurationTests.cs
git commit -m "fix(windows): use native DWM panel border"
```
