# 更新日志

本文件记录 Clipboard 的版本变更。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

## [未发布]

### 新增

- 英文 README，供国际用户阅读。
- GitHub Issue 模板与 Pull Request 模板。
- 客户端自动更新：设置页显示当前版本，可手动检查更新，也可开启启动时自动检查。发现新版本后下载官方安装包、校验 SHA-256，再由用户确认安装；确认后客户端自行退出，由安装器完成文件替换并自动重启应用。
- Rust core 新增检查更新与下载安装包命令，并新增 12–14 号状态码，分别表示检查失败、下载失败与校验不匹配。
- 设置新增「启动时自动检查更新」开关与「跳过此版本」记录。

### 测试

- Rust 单元测试覆盖版本比较、Release 解析、URL 白名单、校验文件解析与 SHA-256 校验；命令级测试覆盖版本化命令信封与服务调用。
- 客户端测试覆盖更新状态机、跳过版本持久化、安装器交接与启动时检查的开关条件。
- 打包脚本新增发布产物资源校验，并提供 `-VerifyPublishedApp` 开关，用于启动发布版客户端做冒烟。

### 修复

- 修正发布流水线的产物路径。此前 artifact 保留了 `installer/` 子目录，导致 Release 只上传校验文件而缺少安装包 EXE。
- 修正 Windows 发布任务的 Inno Setup 安装方式。GitHub 的 `windows-2022` runner 没有 `winget`，改用预装的 Chocolatey 安装。
- 移除 Windows 发布任务中不必要的 Windows App Runtime 安装步骤。客户端以 Windows App SDK 自包含模式发布，不依赖预装运行时。
- 移除安装器对 `ChineseSimplified.isl` 的硬依赖。Inno Setup 的 Chocolatey 包不包含该语言文件，会导致编译中止。
- 修正工具链检查中的 Visual Studio 判定条件。原实现把 vswhere 查询限定为 `BuildTools` 产品，在只安装 Enterprise 或 Community 的机器上返回空值并中断验证；现在要求存在带 MSVC 与 Windows SDK 26100 的任意 Visual Studio 安装。
- 修正发布产物缺少 WinUI 编译资源导致安装后无法启动的问题。`dotnet publish` 不会复制构建阶段生成的 `Clipboard.Windows.pri` 与 `*.xbf`，客户端因此在 XAML 初始化阶段崩溃并返回 `0xC000027B`；现在发布后显式补齐这些文件，并在打包脚本中逐个校验，缺失即终止打包。

### 变更

- 发布工作流合并为单一流程：`core` 在 Ubuntu 上执行 Rust 格式、Clippy、工作区测试与 `clipboard-core` 库测试，`windows-client` 在 Windows 上构建 Rust Release FFI、自包含客户端与 EXE 安装器，两者都通过后才由 `release` 任务创建 GitHub Release。
- 客户端版本、安装器名称与 Release 产物统一由版本号驱动。
- 移除 `main` 分支推送与 Pull Request 的自动 CI 工作流。现在只有推送 `v*` Tag 或手动触发才运行 GitHub Actions，日常验证通过 `scripts/test-core.ps1` 与 `scripts/verify-windows-client.ps1` 本地执行。

### 文档

- README 重写为开源项目结构，补充徽章、快速开始、隐私与安全模型、目录结构、发布流程与贡献指南。
- 新增 MIT 许可证文件。

## [0.1.0] - 2026-09-15

首个公开发布，交付 Windows 11 x64 自包含 EXE 安装包。

### 新增

- Rust 工作区与稳定的 C ABI：`clipboard-domain`、`clipboard-crypto`、`clipboard-storage`、`clipboard-search`、`clipboard-core`、`clipboard-ffi` 与 `clipboard-sync`。
- WinUI 3 客户端：光标附近的单一“剪贴板”面板，默认尝试接管 `Win+V`，失败时降级到可配置的备用快捷键。
- 文本与图片本机历史：异步缩略图、收藏、删除、全部清除，以及恢复到原应用的立即粘贴。
- 文本即时子串与正则搜索，文本、图片和文件可按时间、来源应用和类型筛选。
- 保留策略：最大普通记录数、保留天数与图片空间上限在每次捕获后执行，收藏项不被自动清理。
- 本机文件历史：单个/多个文件与文件夹捕获、Fluent 卡片、系统文件类型图标、友好来源应用名称、键盘高亮与自动滚动。
- 收藏文件束按容量上限分块加密缓存；原路径失效时只恢复到本机受限暂存目录。
- 端到端加密同步：WebDAV 与阿里云 OSS 适配器同步密文文本/图片事件、密文图片对象、收藏状态与删除墓碑，图片对象先上传后发布日志。
- 后台实时同步：启动排空持久化 outbox、带 0%–25% 抖动的指数退避，凭据失效时暂停自动重试并在重新启用后恢复。
- 设备配对：恢复码与 Argon2id + XChaCha20-Poly1305 配对文件。
- 设置页分为“常规设置”和“同步设置”，脱敏的同步诊断与全局日志窗口。
- Windows x64 自包含 EXE 安装包，按当前用户安装到 `%LOCALAPPDATA%\Programs\Clipboard`，无需管理员权限。
- 验证与发布脚本：`scripts/test-core.ps1`、`scripts/verify-windows-client.ps1`、`scripts/package-windows.ps1` 与性能基准入口。

### 安全

- 历史存于 SQLCipher 加密数据库，密钥由 HKDF-SHA-256 按用途隔离派生，图片与同步对象使用 XChaCha20-Poly1305。
- 文件束、文件路径与文件内容严格 local-only，不进入同步队列。
- 同步凭据仅由当前 Windows 用户的 DPAPI 保护，不写入设置 JSON、日志或状态文本。
- 历史加载失败按锁、密钥不匹配、无法解密、损坏与迁移失败分类，并映射到唯一状态码。
- 日志与诊断输出经过脱敏，只记录类别与计数。

### 已知限制

- 文件历史的跨设备同步尚未提供。
- macOS 客户端与跨平台同步界面尚未提供。
- 安装包未做代码签名，Windows SmartScreen 可能提示“未知发布者”。
- 已签名的 MSIX 发布包尚未提供。

[未发布]: https://github.com/KINNNNNNG/clipboard/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/KINNNNNNG/clipboard/releases/tag/v0.1.0
