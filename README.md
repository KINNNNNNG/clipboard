# Clipboard

[简体中文](README.md) | [English](README.en.md)

**Windows 11 上的端到端加密剪贴板历史与跨设备同步。**

[![CI](https://github.com/KINNNNNNG/clipboard/actions/workflows/core.yml/badge.svg)](https://github.com/KINNNNNNG/clipboard/actions/workflows/core.yml)
[![Release](https://github.com/KINNNNNNG/clipboard/actions/workflows/release.yml/badge.svg)](https://github.com/KINNNNNNG/clipboard/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4)
![Rust](https://img.shields.io/badge/rust-1.88-000000)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

Clipboard 用 WinUI 3 在光标附近提供一个原生剪贴板面板，接管 `Win+V`，把文本、图片和文件历史保存在本机 SQLCipher 加密数据库中。跨设备同步基于自研的端到端加密日志：远端只存储密文，文件束与文件路径始终保留在本机。

- 下载安装包：[Releases](https://github.com/KINNNNNNG/clipboard/releases/latest)
- 当前版本：`0.1.0`
- 开发状态与验收边界：[docs/STATUS.md](docs/STATUS.md)

## 功能特性

**原生面板与快捷键**

- WinUI 3 单一面板显示在光标附近，默认尝试接管 `Win+V`，失败时降级到可配置的备用快捷键。
- 键盘上下键高亮并自动滚动，`Esc` 关闭，恢复内容时立即粘贴到原应用。

**历史记录**

- 文本与图片本机捕获、异步图片缩略图、收藏、删除、单条清除与全部清除。
- 单个/多个文件和文件夹捕获，使用 Fluent 文件与文件夹卡片、友好来源应用名称和系统文件类型图标。
- 收藏的文件束按容量上限分块加密缓存；原路径失效时仅恢复到本机受限暂存目录，取消收藏或删除会释放缓存。

**搜索与筛选**

- 文本即时子串与正则搜索；文本、图片和文件可按复制时间、来源应用和类型筛选。
- 图片不执行 OCR，检索只针对文本内容与文件路径。

**保留策略**

- 最大普通记录数、保留天数和图片空间上限可配置，并在每次捕获后执行；收藏项不会被自动清理。

**同步**

- WebDAV、阿里云 OSS 适配器同步端到端加密的文本/图片事件、密文图片对象、收藏状态与删除墓碑。
- 图片对象先上传、后发布日志；接收端通过 AEAD 校验后才写入历史。
- 后台实时同步支持启动排空持久化 outbox 与带 0%–25% 抖动的指数退避；凭据失效时暂停自动重试，重新启用同步后恢复。

## 隐私与安全模型

- **本机加密**：历史存于 SQLCipher 加密数据库，密钥由 HKDF-SHA-256 按用途隔离派生，图片与同步对象使用 XChaCha20-Poly1305。
- **文件不上传**：文件束、文件路径与文件内容严格 local-only，只有加密后的文本/图片事件进入同步队列。
- **设备配对**：恢复码和 Argon2id + XChaCha20-Poly1305 配对文件；配对文件只包含主密钥与非敏感远端配置，账号与 AccessKey Secret 必须单独输入。
- **凭据保护**：同步凭据仅由当前 Windows 用户的 DPAPI 保护，不写入设置 JSON、日志或状态文本；更换设备时需要重新输入。
- **可观测性不泄露内容**：诊断与同步日志经过脱敏，只记录类别与计数。

## 下载与安装

从 [Releases](https://github.com/KINNNNNNG/clipboard/releases/latest) 下载 `Clipboard-Setup-v0.1.0.exe` 与 `SHA256SUMS.txt`，校验后再安装：

```powershell
Get-FileHash .\Clipboard-Setup-v0.1.0.exe -Algorithm SHA256
```

将输出与 `SHA256SUMS.txt` 比对一致后双击安装器：

- 安装到 `%LOCALAPPDATA%\Programs\Clipboard`，按当前用户安装，不需要管理员权限。
- 创建开始菜单入口，并提供可选的桌面快捷方式。
- 卸载只删除程序文件，`%LOCALAPPDATA%\Clipboard` 下的历史、密钥、设置和日志都会保留。
- 应用以 Windows App SDK 自包含模式发布，目标机器无需预装 Windows App Runtime。
- 当前安装包未做代码签名，Windows SmartScreen 可能提示“未知发布者”。

## 从源码构建

### 前置条件

| 依赖 | 版本 | 用途 |
| --- | --- | --- |
| Rust | 1.88（由 `rust-toolchain.toml` 固定） | Rust 工作区与 FFI |
| .NET SDK | 8.0 | WinUI 3 客户端与测试 |
| Visual Studio 2022 Build Tools | 含 MSVC 与 Windows SDK 26100 | 原生构建与资源编译 |
| Perl | 完整安装，路径写入 `OPENSSL_SRC_PERL` | 编译 vendored OpenSSL（SQLCipher） |
| Inno Setup | 6 | 生成 EXE 安装包 |

工具链可用以下命令自检：

```powershell
pwsh -NoProfile -File scripts/verify-toolchain.ps1
```

### 构建与运行

```powershell
cargo build -p clipboard-ffi
dotnet build src\Clipboard.Windows\Clipboard.Windows.csproj -c Debug -p:Platform=x64
```

客户端会自动复制 `clipboard_ffi.dll`，构建产物位于 `src\Clipboard.Windows\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\`，运行 `Clipboard.Windows.exe --show` 可直接打开面板。

### 生成安装包

```powershell
pwsh -NoProfile -File scripts/package-windows.ps1 -Version 0.1.0
```

产物为 `artifacts\windows\installer\Clipboard-Setup-v0.1.0.exe` 与 `artifacts\windows\SHA256SUMS.txt`。详见 [Windows 安装器发布指南](docs/release/windows-installer.md)。

## 测试与验证

```powershell
# Rust 核心：工具链、格式、Clippy、工作区测试与 FFI 冒烟
pwsh -NoProfile -File scripts/test-core.ps1

# 全量 Windows 客户端验证；无桌面环境可加 -SkipGuiSmoke
pwsh -NoProfile -File scripts/verify-windows-client.ps1

# 只跑 Rust 工作区测试
cargo test --workspace --all-targets
```

发布前建议运行完整验证：它会关闭同路径旧客户端，依次检查 Core 工具链、格式、Clippy、WebDAV/OSS 远端同步测试、FFI 冒烟、Windows 工具链、同步设置定向测试、WinUI 全量测试与 x64 构建，确认 Debug x64 产物不含证书或私钥，并连续启动两个客户端验证第二个实例会关闭第一个。

人工验收还应覆盖记事本、Edge、Office 和 VS Code 的文本/图片捕获与粘贴；资源管理器复制单个/多个文件及文件夹后的 Fluent 图标与“共 N 项”；来源应用名称；上下键高亮与自动滚动；原路径不可用时不隐藏面板且不发送 `Ctrl+V`；`Win+V` 接管失败时的备用快捷键；自身写回不产生重复历史；100%/150%/200% DPI 与多显示器边缘。

在受控的 Windows 11 x64 性能环境中，还可以显式运行性能脚本：

```powershell
pwsh -NoProfile -File scripts/measure-history-search.ps1
pwsh -NoProfile -File scripts/measure-windows-history-diagnostics.ps1
```

前者在 10,000 条合成文本上执行 Release Rust 搜索门（子串与组合筛选各采样 30 次，P95 必须不超过 200 ms）；后者输出真实 FFI、ViewModel、固定容量图片 LRU 与进程内存趋势。两者都不参与日常测试。

## 目录结构

```text
crates/                Rust 工作区
  clipboard-domain/    共享领域类型
  clipboard-crypto/    本地密钥派生与对象认证加密
  clipboard-storage/   SQLCipher 加密持久化与同步 outbox
  clipboard-search/    确定性的文本、路径与正则搜索
  clipboard-core/      用例编排与版本化命令协议
  clipboard-ffi/       稳定的 C ABI
  clipboard-sync/      同步协议原语与 WebDAV/OSS 适配器
src/
  Clipboard.Windows/          WinUI 3 客户端
  Clipboard.FfiSmoke/         P/Invoke 冒烟程序
tests/
  Clipboard.Windows.Tests/        客户端单元与集成测试
  Clipboard.Windows.Performance/  性能与诊断入口
installer/             Inno Setup 安装器脚本
scripts/               构建、验证、打包与性能脚本
docs/                  设计、计划与状态文档
```

## 发布流程

推送符合 `vMAJOR.MINOR.PATCH` 的 Tag 即可触发 `Windows Release` 工作流，它会并行执行 Ubuntu 上的 Rust Core 校验与 Windows 上的客户端安装器构建，全部通过后创建 GitHub Release 并上传安装包与校验文件：

```powershell
git tag v0.1.0
git push origin v0.1.0
```

也可以在 Actions 页面手动运行该工作流并填写版本号。

## 路线图与已知限制

- 文件历史的跨设备同步尚未提供，文件束仍严格 local-only。
- macOS 客户端及跨平台同步界面尚未提供。
- 已签名的 MSIX 发布包尚未提供；当前交付自包含 EXE 安装包，安装、升级、卸载的完整验收仍待完成。
- 阶段五收尾项包括真实 WebDAV/OSS 写入、两设备同步与可见 WinUI 焦点/DPI 矩阵，优先级见 [阶段五发布收尾路线](docs/superpowers/plans/2026-08-25-stage-five-closure-roadmap.md)。

## 参与贡献

欢迎提交 Issue 与 Pull Request。

- 提交前请运行 `scripts/test-core.ps1`，涉及客户端时再运行 `scripts/verify-windows-client.ps1`。
- 代码风格遵循 `.editorconfig`：Rust 使用 4 空格缩进，YAML/JSON/TOML 使用 2 空格缩进。
- 提交信息与文档使用中文，提交信息带类型前缀，例如 `修复：`、`测试：`、`文档：`、`构建：`。
- 新增行为请附带测试；仅做根因修复，不做表面补丁。

## 文档

- [开发状态、证据与优先级](docs/STATUS.md)
- [版本更新日志](CHANGELOG.md)
- [Windows 安装器发布指南](docs/release/windows-installer.md)
- [产品与架构设计](docs/superpowers/specs/2026-07-31-cross-device-clipboard-design.md)
- [V1 总路线图](docs/superpowers/plans/2026-07-31-clipboard-v1-roadmap.md)
- [阶段五发布收尾路线](docs/superpowers/plans/2026-08-25-stage-five-closure-roadmap.md)

## 许可证

本项目基于 [MIT 许可证](LICENSE) 发布。
