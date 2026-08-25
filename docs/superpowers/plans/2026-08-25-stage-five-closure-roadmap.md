# 阶段五发布收尾路线

> **定位：** 本文是发布收尾的路线图，不把跨设备、真实服务和可见 UI 验收伪装成单一可执行的代码计划。每个 P0/P1 项在开始实现前都必须另建独立的设计和实施计划。

**基线：** `codex/phase4-image-sync` 分支的 `2e4c3f6`，核对日期 2026-08-25。`main` 仍位于 `725e502`；以下状态在分支合并前不能作为主分支发布声明。

**目标：** 为 Clipboard V1 建立可追溯的真实环境、可见界面、可靠性和安装交付证据，关闭已知 P0 问题后才进入发布工程。

**现有基础：** [发布验证基线](2026-08-17-clipboard-release-verification.md) 已覆盖同路径旧实例清理、敏感产物扫描和双实例 smoke；[历史搜索性能门](2026-08-24-history-search-performance.md) 已覆盖 10,000 条 Rust 搜索门和无界面 Windows 诊断。二者均不替代真实远端、可见 `ListView` 或多设备验收。

---

## 执行顺序

| 优先级 | 收尾项 | 前置条件 | 关闭证据 |
| --- | --- | --- | --- |
| P0 | WebDAV 403 真实复现与诊断 | 受控 WebDAV 凭据和可写测试目录 | 精确失败操作、脱敏诊断、修复或明确服务权限配置、回归测试 |
| P0 | 历史加载失败根因与恢复 | 可隔离的测试 vault | 锁定、损坏、迁移失败的故障注入与恢复结果 |
| P0 | 原生焦点和默认首项可见验收 | 可见 Windows 11 桌面 | 首项焦点、单一焦点框、方向键、虚拟化和主题/DPI 记录 |
| P1 | 两台设备的真实同步验收 | P0 WebDAV 或 OSS 通路关闭 | 文本、图片、离线冲突、收藏、删除和 file local-only 证据 |
| P1 | 可靠性与兼容性 | 可生成旧版本 vault 和中断状态 | 升级、崩溃恢复、协议拒绝和损坏对象恢复记录 |
| P2 | MSIX 与最终交付文档 | P0/P1 均关闭 | 签名、安装、升级、卸载、隐私/恢复码说明和发布清单 |

## P0.1：WebDAV“连接成功、立即同步 403”

**问题边界：** 当前“测试连接”只证明根目录探测可用。实际同步还会进行目录列举、读取、条件 `PUT` pending、`MOVE` 发布、设备状态、快照和图片对象读写；任一操作缺少权限都可能返回 403。

**预计触及文件：** `crates/clipboard-sync/src/webdav.rs`、`crates/clipboard-sync/tests/webdav.rs`、`crates/clipboard-core/src/service.rs`、`src/Clipboard.Windows/ViewModels/SettingsViewModel.cs`、对应 Rust/C# 测试，以及新的真实服务验收记录。

- [ ] 为 `PROPFIND`、`GET`、条件 `PUT`、`MOVE`、元数据、设备状态、快照和图片对象建立固定操作名称与脱敏状态记录；禁止记录端点、对象名、账号、凭据或响应正文。
- [ ] 在本地 WebDAV fixture 为每个写入阶段加入直接 403 回归，验证错误类别、`http_403` 和操作名称均可区分。
- [ ] 使用受控真实 WebDAV 凭据在空的专用目录运行操作矩阵，记录每一步 HTTP 状态和服务端所需权限，不把凭据或远端路径写入仓库、日志或文档。
- [ ] 根据复现结果修复路径、方法、条件写入或错误分类；若是服务端权限配置，记录最小权限要求和可操作提示。
- [ ] 重新运行 `cargo test --workspace --all-targets`、`pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke`，并把真实验收的脱敏结论补入新的 P0 实施计划和 [Clipboard 开发状态](../../STATUS.md)。

## P0.2：无法加载剪贴板历史的根因与恢复

**问题边界：** `ClipboardPanelViewModel` 已在首次 `CoreError` 后延迟 75 ms 重试，并在再次失败时显示“无法加载剪贴板历史。”；这只是用户可见降级，尚未证明 vault 打开、锁定、损坏或迁移失败时能够诊断和恢复。

**预计触及文件：** `src/Clipboard.Windows/ViewModels/ClipboardPanelViewModel.cs`、`tests/Clipboard.Windows.Tests/ViewModels/ClipboardPanelViewModelTests.cs`、Rust vault/storage 测试和新的故障注入验收记录。

- [ ] 定义不会泄露路径、内容或密钥的错误分类，区分短暂 Core 调用失败、数据库锁、错误密钥、损坏数据库和迁移失败。
- [ ] 为每个分类建立可重复的故障注入测试，断言保留已有历史、重试次数、用户提示和恢复后的重新加载行为。
- [ ] 在隔离 vault 上执行启动、关闭、锁定、损坏和恢复流程，确认任何失败都不会覆盖原始数据或生成明文诊断。
- [ ] 仅在根因可区分后调整 UI 提示、日志或恢复操作，并补充对应的 Windows 自动测试。

## P0.3：默认焦点与原生焦点框的可见 WinUI 验收

**问题边界：** 源码已将第一条历史设为选择项，并在显示面板后调用 `FocusSelectedHistoryItem`；XAML 使用 `ListViewItemPresenter` 和系统焦点视觉。容器尚未生成时的焦点请求是否丢失，不能由当前静态 XAML 与 ViewModel 测试证明。

**预计触及文件：** `src/Clipboard.Windows/Views/MainWindow.xaml`、`src/Clipboard.Windows/Views/MainWindow.xaml.cs`、`src/Clipboard.Windows/ViewModels/ClipboardPanelViewModel.cs`、相关 Windows 测试，以及可见 STA harness 或人工验收记录。

- [ ] 在真实 Windows 11 桌面建立可见的 STA 验收入口，记录 ListView 容器生成后首项获得键盘焦点的结果。
- [ ] 验证上/下方向键每次只移动一项，任一时刻只出现一个系统原生焦点框，鼠标选择、Enter 粘贴和 Escape 关闭不回归。
- [ ] 在 100%、150%、200% DPI，浅色、深色、高对比度、减少动画和多显示器边缘完成矩阵；检查虚拟化回收后焦点框和文件图标不串位。
- [ ] 若焦点因容器生成时序丢失，先增加失败的可见或调度时序测试，再以最小生命周期修复；不得恢复手绘选中边框。

## P1：跨设备和可靠性

- [ ] 使用两台 Windows 11 x64 设备分别验证 WebDAV 和 OSS：文本/图片上传下载、离线并发复制、收藏、删除、重启后最终一致性和图片 AEAD 损坏隔离。
- [ ] 在两台设备及远端对象中证明文件束、文件名、路径、缓存状态和文件内容不会离开产生设备。
- [ ] 对旧版本数据库执行升级；注入中断迁移、崩溃重启、远端对象截断、错误密钥和未知协议版本，记录失败关闭与恢复行为。
- [ ] 将通过的人工矩阵、版本号、环境、脱敏日志摘要和未解决限制写入发布验收报告，而不是仅保留在终端输出。

## P2：安装包与发布资料

- [ ] 建立 MSIX/Appx 打包、签名和产物扫描流程；证书只能由受控环境变量或 CI secret 提供，产物和日志不得包含私钥、测试凭据、恢复码或剪贴板数据。
- [ ] 在干净 Windows 11 环境验证安装、升级、卸载、数据保留和恢复码提示；明确卸载时本地 vault 的保留/删除语义。
- [ ] 编写最终用户文档：支持范围、file local-only 隐私边界、恢复码安全提示、真实服务最小权限、已知限制和故障恢复路径。
- [ ] 由发布负责人对照产品规格第 11 至 14 节逐项签字；任一 P0/P1 未关闭时不得宣称 V1 可发布。

## 完成条件

阶段五完成的唯一标准是：上表 P0、P1、P2 全部具有可追溯的自动或人工证据，且分支已完成评审并合入发布基线。仅通过本地 fixture、无界面诊断或单实例 smoke 不足以关闭真实服务和可见 UI 的验收项。
