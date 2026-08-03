# Clipboard

这是跨设备剪贴板项目的阶段 1 共享内核，不是可直接使用的 Windows 剪贴板应用，也暂时不会接管 `Win+V`。

## 当前能力

- Windows 和未来 macOS 客户端可复用的 Rust 领域模型与 C ABI。
- 文本、图片及单个/多个文件和文件夹的历史模型；文件束固定为仅本机范围，不能进入同步 outbox。
- SQLCipher 加密数据库、版本化迁移和本地持久化。
- 文本即时子串搜索、正则搜索及本机文件路径搜索；图片不执行 OCR。
- 最大普通记录数、保留天数和非收藏图片空间上限的纯函数清理计划；收藏项始终豁免。
- HKDF-SHA-256 用途隔离密钥，以及 XChaCha20-Poly1305 图片/对象加密基元。
- .NET 8 P/Invoke 冒烟程序，可跨 ABI 写入、关闭、重开并搜索文本。

## 尚未提供

- WinUI 3 窗口、系统剪贴板监听、光标处弹窗、快捷键接管和粘贴回目标应用。
- WebDAV、阿里云 OSS 或其他跨设备同步适配器。
- Windows 端图片/文件捕获与收藏文件加密缓存工作流。
- 收藏、删除和执行保留的持久化 Core 命令；当前协议入口会明确返回未启用错误。

这些能力将在 Windows 客户端阶段按 [V1 路线图](docs/superpowers/plans/2026-07-31-clipboard-v1-roadmap.md) 继续实现。

## 验证

环境要求为 Rust 1.88、.NET 8 SDK、Visual Studio 2022 Build Tools、Windows SDK 26100 和完整 Perl。运行：

```powershell
pwsh -NoProfile -File scripts/test-core.ps1
```

脚本依次验证工具链、格式、Clippy、全部 Rust 测试、FFI 动态库构建和 .NET P/Invoke 冒烟程序。

## 文档

- [产品与架构设计](docs/superpowers/specs/2026-07-31-cross-device-clipboard-design.md)
- [阶段 1 实现计划](docs/superpowers/plans/2026-07-31-clipboard-core-foundation.md)
- [V1 总路线图](docs/superpowers/plans/2026-07-31-clipboard-v1-roadmap.md)
