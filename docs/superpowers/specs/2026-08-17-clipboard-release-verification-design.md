# Windows 发布验证基线设计

**状态：** 已确认，2026-08-17

**目标：** 建立阶段五第一批可重复执行的 Windows 发布验证基线，覆盖旧实例清理、测试、构建产物和 GUI 启动生命周期。

## 背景

当前 Windows 验证脚本已经串联 Rust Core、同步适配器、FFI、WinUI 测试和 Debug 构建，但仍存在几个发布前风险：运行中的旧客户端会锁住 exe 并使构建失败；脚本只启动一个 GUI 进程，不能证明新实例会替换旧实例；新增的旧实例清理和日志脱敏测试没有进入定向验证；GUI smoke 使用强制结束，无法验证应用的正常退出路径。

阶段五本批次只处理验证基线，不引入新的业务能力。

## 设计

`scripts/verify-windows-client.ps1` 作为唯一入口，按以下顺序执行：

1. 解析仓库根目录和正式 Debug x64 客户端 exe 的绝对路径。
2. 仅查找同一路径的 `Clipboard.Windows.exe`，先调用主窗口关闭并等待，超时后才强制结束；路径不匹配的副本不处理。
3. 执行现有 Core、文件工作流、工具链、restore、定向 Windows 测试和全量 Windows 测试。
4. 构建客户端并确认 exe 存在；扫描输出目录时拒绝证书、私钥和常见凭据文件扩展名。
5. GUI smoke 启动第一个客户端，等待其保持运行；再启动第二个同路径客户端，确认第一个已退出、第二个仍运行且同路径进程总数为 1。
6. 使用优雅关闭回收第二个进程，必要时才强制结束；无论测试成功或失败都执行清理。

产品中的 `PreviousInstanceCloser` 仍是运行时最终行为来源，脚本中的清理只负责在构建前释放文件锁和在 smoke 前建立确定状态，两者使用相同的“同路径、优雅关闭、超时强制结束”规则。

## 变更边界

只修改验证入口及其文档，不修改同步协议、数据库、剪贴板捕获或 UI 行为：

- `scripts/verify-windows-client.ps1`：增加同路径旧实例清理、产物检查、双实例 smoke 和优雅回收；定向测试过滤器加入 `GlobalLogTests` 与 `PreviousInstanceCloserTests`。
- `README.md`：补充阶段五验证命令、GUI smoke 行为和本批次质量门。
- `docs/superpowers/plans/2026-08-17-clipboard-release-verification.md`：记录逐步实施、命令和验收证据。

现有 `PreviousInstanceCloserTests`、`GlobalLogTests`、Windows 全量测试和 `scripts/test-core.ps1` 作为执行基础，不复制业务测试逻辑到 PowerShell。

## 失败处理

- 无法定位正式 exe、工具链不满足、任一测试失败或构建失败时立即退出并保留原始错误码。
- 旧实例路径无法读取时不猜测并终止其他进程；脚本报告明确错误。
- GUI smoke 中第一个进程未退出或第二个进程退出，验证失败；`finally` 仍尝试回收已启动的进程。
- 产物目录发现 `.pfx`、`.p12`、`.pem`、`.key`、`.snk` 文件时验证失败。

## 验收标准

- `pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke` 返回 0。
- 同一命令不带 `-SkipGuiSmoke` 返回 0，并证明二次启动只保留一个同路径客户端进程。
- Rust/Core、同步适配器、FFI、Windows 定向测试和全量测试全部通过。
- Debug x64 构建为 0 警告、0 错误，正式 exe 存在且产物目录没有证书或私钥文件。
- 运行中旧客户端不再阻塞验证构建，路径不匹配的其他副本不会被脚本关闭。

## 非目标

本批次不实现 10,000 条记录性能基准、DPI/高对比度人工矩阵、数据库迁移策略、MSIX 签名发布、真实 WebDAV/OSS 凭据验收、文件跨设备同步或 macOS 客户端；这些作为后续阶段五任务单独推进。
