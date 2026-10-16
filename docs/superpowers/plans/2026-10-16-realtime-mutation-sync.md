# 实时同步变更通知补全实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (recommended) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标：** 确保收藏状态和批量清理产生的同步 outbox 变更立即唤醒实时同步调度器。

**架构：** 保持 Rust 核心和现有 `IRealtimeSyncNotifier` 接口不变。Windows ViewModel 在核心变更成功后发出与已有捕获/删除流程一致的通知：单项收藏使用条目类型通知，批量清理使用 `startup` 作为全量 vault 变更通知。通过 ViewModel 单元测试验证通知时序和内容。

**技术栈：** .NET 8、WinUI 3、C#、xUnit。

**规格：** `docs/superpowers/specs/2026-08-07-realtime-sync-design.md`

## 全局约束

- 只修复根因，保持实时同步调度器的单一入口和现有文件历史 local-only 边界。
- 核心调用成功后才发出通知，失败操作不产生虚假的实时同步请求。
- 文件束不会触发远程同步；收藏文件束仍只维护本地缓存。
- 所有新增行为必须有自动化回归测试。

---

### 任务 1：为收藏和批量清理补充失败测试

**文件：**
- 修改：`tests/Clipboard.Windows.Tests/ViewModels/ClipboardPanelViewModelTests.cs`

- [x] 增加测试替身，记录 `IRealtimeSyncNotifier.NotifyChanged` 收到的通知类型。
- [x] 增加测试：文本条目收藏成功后收到一次 `text` 通知。
- [x] 增加测试：图片条目取消收藏成功后收到一次 `image` 通知。
- [x] 增加测试：批量清理成功后收到一次 `startup` 通知。
- [x] 运行定向测试，确认新增断言在当前实现下失败，证明缺口真实存在。

运行：

```powershell
 dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj --configuration Debug --no-restore --filter FullyQualifiedName~ClipboardPanelViewModelTests
```

### 任务 2：实现变更通知

**文件：**
- 修改：`src/Clipboard.Windows/ViewModels/ClipboardPanelViewModel.cs`

- [x] 在 `ToggleFavoriteAsync` 的 `SetFavoriteAsync` 成功后调用 `_realtimeSync?.NotifyChanged(item.Kind)`。
- [x] 保持文件束通知被 `RealtimeSyncCoordinator` 忽略，避免破坏 local-only 约束。
- [x] 在 `ClearUnfavoriteAsync` 的核心操作成功后调用 `_realtimeSync?.NotifyChanged("startup")`，让调度器拉取全部待同步状态。
- [x] 不在核心调用失败时发送通知。

### 任务 3：验证完整行为

**文件：**
- 无新增文件。

- [x] 运行 ViewModel 定向测试。
- [x] 运行完整 Windows 测试。
- [x] 运行 Rust 工作区测试和 Clippy，确认核心同步协议没有回归。
- [x] 检查 `git diff`，确认修改仅涉及计划文件及本计划文档。

验证命令：

```powershell
 dotnet test tests\Clipboard.Windows.Tests\Clipboard.Windows.Tests.csproj --configuration Debug --no-restore
 cargo test --workspace
 cargo clippy --workspace --all-targets -- -D warnings
```
