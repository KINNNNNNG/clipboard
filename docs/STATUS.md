# Clipboard 开发状态

**基线：** `main` 分支，提交 `430176d`，核对日期 2026-09-11。

`main` 已包含阶段四同步后续、阶段五发布验证、历史搜索性能门和 P0.2 历史加载失败分类提交。

## 状态口径

- **已验证实现**：当前源代码、提交历史和自动测试共同证明功能已落地。
- **已实现，待外部验收**：代码和模拟/单元测试存在，但仍缺真实服务、可见 WinUI 或人工系统环境证据。
- **未完成**：尚未满足第一版要求，或尚未确定问题根因。
- 历史实施计划中的 `[x]` 已于 2026-08-25 和 2026-09-11 按当前最终代码和可追溯提交回填。它表示该步骤的交付结果已存在；不重新声称每次早期红灯运行的原始控制台文本仍可复现。保留 `[ ]` 的步骤是尚未完成的人工、真实远端、跨设备验收，或当前工作区“必须干净”的即时检查；最后一种不表示产品功能缺失。

## 阶段总览

| 阶段 | 当前状态 | 自动证据 | 未关闭验收 |
| --- | --- | --- | --- |
| 1. Rust 内核与 FFI | 已验证实现 | `scripts/test-core.ps1`、workspace 测试、FFI ABI 测试 | 无独立阶段阻塞项 |
| 2. Windows 文本/图片客户端 | 已验证实现，待外部验收 | `scripts/verify-windows-client.ps1`、Windows 单元测试、双实例 smoke | 可见 UI 焦点、DPI、多显示器、高对比度人工矩阵 |
| 3. 本机文件历史 | 已验证实现，待外部验收 | 文件束、缓存、`local_only_outbox`、Windows 平台测试 | 资源管理器真实文件/文件夹矩阵与跨设备隐私核验 |
| 4. 加密同步与配对 | 已验证实现，待外部验收 | 目录/WebDAV/OSS fixture、远端同步、图片对象、配对、实时调度和日志测试 | 真实 WebDAV 403、真实 OSS、两设备离线冲突和图片同步 |
| 5. 发布加固 | 部分完成 | 发布验证基线、双实例 smoke、10,000 条搜索门、无界面 Windows 诊断、历史加载失败分类 | 可见 `ListView` 首帧、兼容性矩阵、真实 vault 迁移/崩溃、MSIX 与最终发布验收 |

## 已验证的阶段五证据

- 发布验证基线：[2026-08-17-clipboard-release-verification.md](superpowers/plans/2026-08-17-clipboard-release-verification.md)。最近记录的无 GUI 验证为 Windows 定向 `126/126`、全量 `231/231`，并验证同路径旧实例清理和双实例替换。
- 搜索性能和诊断：[2026-08-24-history-search-performance.md](superpowers/plans/2026-08-24-history-search-performance.md)。最近记录的 Rust P95 为子串 `29 ms`、组合筛选 `29 ms`；Windows 无界面诊断 FFI P95 为 `30/22 ms`、ViewModel P95 为 `92/91 ms`。这些数据不代表可见 WinUI `ListView` 首帧。
- 脚本安全边界：性能入口保留原始子进程退出码，抑制子进程原始输出，只输出经过严格结构校验的白名单 JSON 或固定安全错误类别。
- 历史加载失败分类：[2026-09-11-history-load-failure-classification.md](superpowers/plans/2026-09-11-history-load-failure-classification.md)。存储层新增 vault 作用域打开和非秘密 `history.vault` 标记，把锁、密钥不匹配、无法解密、损坏和迁移失败分别映射到唯一 `CoreStatus`（7–11），Windows 只对 `CoreError` 与 `StorageLocked` 重试一次。证据为存储层 6 个故障注入、FFI 5 个状态码、Windows 定向 `132/132` 与全量 `240/240`。可见窗口内的人工启动、锁定和恢复流程仍属于外部验收。

## 当前风险与优先级

### P0：必须先关闭

1. **WebDAV“连接成功、立即同步 403”**：探测只证明根路径可列举；同步还需要 PUT、MOVE、对象读取、元数据和图片对象写权限。现有代码会脱敏记录 HTTP 状态和操作，但没有真实服务写入矩阵。必须先复现精确失败操作，再修复协议兼容性或权限诊断。
2. **默认焦点和原生高亮的可见运行时证明**：源码已使用 WinUI 原生焦点视觉，且刷新后选择第一项；但容器尚未生成时的一次性焦点请求可能丢失，缺少可见窗口/布局完成后的验证。

### P1：发布体验和跨设备闭环

1. 100%、150%、200% DPI，多显示器边缘、浅色/深色/高对比度、减少动画和键盘导航矩阵。
2. 两台 Windows 11 x64 的真实 WebDAV/OSS 同步：离线并发复制、收藏、删除、图片对象与最终一致性；同时证明文件路径和内容永不离开本机。
3. 明确“置顶”语义：当前实现是**成功粘贴后**调用 `MarkUsed` 并置顶；如果产品要求仅键盘/鼠标选择就置顶，需要单独改变产品行为和测试。

### P2：交付可靠性

1. 数据库迁移、崩溃恢复、协议版本兼容和远端对象损坏的故障注入。
2. MSIX 打包、受控环境变量签名、安装、升级和卸载验证。
3. 发布说明、恢复码警告、隐私边界、已知限制和最终验收报告。

## 文档入口

- [README 文档索引](../README.md)
- [产品与架构设计](superpowers/specs/2026-07-31-cross-device-clipboard-design.md)
- [V1 路线图](superpowers/plans/2026-07-31-clipboard-v1-roadmap.md)
- [阶段五后续收尾路线](superpowers/plans/2026-08-25-stage-five-closure-roadmap.md)
- [历史加载失败分类设计与计划](superpowers/plans/2026-09-11-history-load-failure-classification.md)
- [发布验证基线](superpowers/plans/2026-08-17-clipboard-release-verification.md)
- [历史搜索性能门](superpowers/plans/2026-08-24-history-search-performance.md)

历史计划保留原始 TDD 任务和决策上下文；它们的逐项回填状态以本页和各计划顶部的回填说明为准。

