# 方向键选择视觉状态修复 Implementation Plan

> **回填状态：** 截至 2026-08-25，已按 `codex/phase4-image-sync` 的 `2e4c3f6` 回填。
> `[x]` 表示该步骤的最终交付结果可由当前代码、提交或自动测试证明；不重新声称历史红灯命令的原始输出仍可复现。
> 真实可见焦点框、虚拟化和 DPI 验收转入阶段五；当前总状态见 [Clipboard 开发状态](../../STATUS.md)。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 移除列表重复方向键处理，使每次按键只移动一格且只保留一个选中视觉状态。

**Architecture:** 保留根 `Grid` 的 `KeyDown="Root_KeyDown"` 作为唯一键盘导航入口，删除 `ListView` 的 `PreviewKeyDown` 绑定。ViewModel 的 `SelectedIndex` 和列表的 `SelectionChanged` 不变。

**Tech Stack:** WinUI 3 XAML、C#、xUnit、.NET 8 Windows。

---

### Task 1: 添加失败回归测试

**Files:**
- Modify: `tests/Clipboard.Windows.Tests/Views/XamlResourceConfigurationTests.cs`
- Test fixture: `tests/Clipboard.Windows.Tests/Fixtures/MainWindow.xaml`

- [x] **Step 1: 修改断言，要求历史列表不注册 PreviewKeyDown**

将测试 `History_list_intercepts_direction_keys_before_its_default_navigation` 改为：读取根 `Grid` 和 `ListView`，断言根元素仍为 `KeyDown="Root_KeyDown"`，并断言 `list.Attribute("PreviewKeyDown")` 为 `null`。测试名称改为 `History_list_uses_root_keyboard_navigation_without_duplicate_preview_handler`。

- [x] **Step 2: 运行测试确认当前实现失败**

运行：

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter XamlResourceConfigurationTests
```

预期：失败，原因是当前 `MainWindow.xaml` 仍存在 `PreviewKeyDown="Root_KeyDown"`。

### Task 2: 移除重复事件绑定并验证

**Files:**
- Modify: `src/Clipboard.Windows/Views/MainWindow.xaml:247-259`

- [x] **Step 1: 删除 ListView 的重复 PreviewKeyDown 属性**

从 `HistoryList` 的 XAML 属性中删除以下行，保留根 `Grid` 的 `KeyDown="Root_KeyDown"`：

```xml
PreviewKeyDown="Root_KeyDown"
```

- [x] **Step 2: 运行回归测试确认通过**

运行：

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter XamlResourceConfigurationTests
```

预期：测试通过，且不再报告重复预览键盘处理。

- [x] **Step 3: 运行 Windows 客户端相关测试和构建**

运行：

```powershell
dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --filter "ClipboardPanelViewModelTests|XamlResourceConfigurationTests"
dotnet build src/Clipboard.Windows/Clipboard.Windows.csproj --configuration Debug --framework net8.0-windows10.0.26100.0 --no-restore
```

预期：所有筛选测试通过，Windows 客户端构建成功。

- [x] **Step 4: 检查差异并提交实现**

运行 `git diff --check`，确认仅包含测试断言和 XAML 事件绑定变更后提交：

```powershell
git add tests/Clipboard.Windows.Tests/Views/XamlResourceConfigurationTests.cs src/Clipboard.Windows/Views/MainWindow.xaml
git commit -m "fix(windows): avoid duplicate arrow selection state"
```
