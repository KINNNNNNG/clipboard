# Clipboard

这是跨设备剪贴板项目。当前已完成阶段 3 的 Windows 客户端，以及阶段 4 的加密文本同步基础：两个本地 Core 实例可通过本地目录型模拟远端交换文本事件。

## 当前能力

- Windows 和未来 macOS 客户端可复用的 Rust 领域模型与 C ABI。
- Windows 11 原生风格的单一“剪贴板”面板，显示在光标附近，默认尝试接管 `Win+V`，失败时降级到可配置的备用快捷键。
- 文本与图片本机历史捕获、异步图片缩略图、收藏、删除、全部清除和恢复到原应用的立即粘贴。
- 文本即时子串/正则搜索；文本、图片均可按复制时间、来源应用和类型筛选，图片不执行 OCR。
- 最大普通记录数、保留天数和图片空间上限可配置并在每次捕获后执行；收藏项不会自动清理。
- 单个/多个文件和文件夹历史捕获、Fluent 文件/文件夹卡片、文件筛选、友好来源应用名称、键盘高亮和自动滚动；文件路径保留在本机且不进入同步队列。
- 收藏的文件束按容量上限分块加密缓存；原路径失效时仅恢复到本机受限暂存目录，取消收藏或删除会释放缓存。
- WebDAV、阿里云 OSS 和未来其他同步适配器只同步文本和图片。文件名、路径、元数据、收藏缓存状态和文件内容均不上传。
- SQLCipher 加密数据库、版本化迁移和本地持久化。
- HKDF-SHA-256 用途隔离密钥，以及 XChaCha20-Poly1305 图片/对象加密基元。
- 版本化、端到端加密的文本同步日志；恢复码、篡改/版本拒绝与脱敏同步诊断。当前仅支持显式本地目录模拟远端。
- .NET 8 P/Invoke 冒烟程序和 WinUI 客户端集成测试。

## 尚未提供

- WebDAV、阿里云 OSS 或其他跨设备同步适配器及其凭据配置。
- 文件历史的跨设备同步。
- macOS 客户端及跨平台同步界面。

这些能力将在后续阶段按 [V1 路线图](docs/superpowers/plans/2026-07-31-clipboard-v1-roadmap.md) 继续实现。

## 验证

环境要求为 Rust 1.88、.NET 8 SDK、Visual Studio 2022 Build Tools、Windows SDK 26100 和完整 Perl。运行：

```powershell
pwsh -NoProfile -File scripts/verify-windows-client.ps1
```

该脚本依次验证 Core 工具链、格式、Clippy、全部 Rust 测试、FFI 冒烟、Windows 工具链、WinUI 测试与 x64 构建；`-SkipGuiSmoke` 可跳过 10 秒进程存活检查。

人工验收还应覆盖：记事本、Edge、Office 和 VS Code 的文本/图片捕获和粘贴；资源管理器复制单个/多个文件及文件夹后显示 Fluent 图标和“共 N 项”；来源应用显示正常软件名；上下键高亮并自动滚动；路径已删除时显示“原路径不可用”且不隐藏面板、不发送 `Ctrl+V`；`Win+V` 接管失败时备用快捷键；自身写回不产生重复历史；100%/150%/200% DPI 与多显示器边缘；目标窗口已失效时不自动发送 `Ctrl+V`。

## 文档

- [产品与架构设计](docs/superpowers/specs/2026-07-31-cross-device-clipboard-design.md)
- [阶段 1 实现计划](docs/superpowers/plans/2026-07-31-clipboard-core-foundation.md)
- [阶段 2 Windows 客户端计划](docs/superpowers/plans/2026-07-31-clipboard-windows-client.md)
- [V1 总路线图](docs/superpowers/plans/2026-07-31-clipboard-v1-roadmap.md)
