# Windows Installer Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 为 Clipboard 0.1.0 生成可双击安装的 Windows x64 EXE，并让 GitHub 在发布 Tag 后自动构建和创建 Release。

**Architecture:** 使用现有 Rust FFI Release 构建和 Windows 客户端发布目录作为安装器输入，启用 Windows App SDK 自包含部署。使用 Inno Setup 将发布目录封装为当前用户安装的 EXE，GitHub Actions 负责版本解析、验证、打包、校验和上传。

**Tech Stack:** Rust 1.88、.NET 8、Windows App SDK 1.8、PowerShell、Inno Setup、GitHub Actions。

**Spec:** 用户确认的“EXE 安装包 + GitHub 自动发布”方案，当前版本 `0.1.0`。

## Global Constraints

- 安装包目标平台为 Windows x64。
- Windows App SDK 运行时随应用自包含发布，不要求用户预装运行时。
- 安装目录使用当前用户的 `%LOCALAPPDATA%\Programs\Clipboard`，不需要管理员权限。
- 用户数据不放入安装目录，卸载不删除剪贴板历史和设置。
- GitHub Release 由 `v*` Tag 触发，产物包含 EXE 和 SHA256 校验文件。
- 所有提交信息使用中文。

### Task 1: 添加可复用的 Windows 发布脚本

**Files:**
- Create: `scripts/package-windows.ps1`

- [ ] 接收并校验 `Version`、`OutputRoot` 和 `SkipInstaller` 参数。
- [ ] 构建 `clipboard-ffi` Release DLL，并使用 `WindowsAppSDKSelfContained=true` 发布 WinUI 客户端。
- [ ] 清理并重建指定输出目录，避免旧文件进入安装包。
- [ ] 在找到 `iscc.exe` 时编译 `installer/Clipboard.iss`，否则在 `SkipInstaller` 为真时只保留发布目录。
- [ ] 输出安装包路径和 `SHA256SUMS.txt`，校验文件只包含安装包文件名和 SHA256。

### Task 2: 添加 Inno Setup 安装器

**Files:**
- Create: `installer/Clipboard.iss`

- [ ] 固定 AppId、版本、x64 架构和当前用户安装目录。
- [ ] 复制整个自包含发布目录，创建开始菜单快捷方式和可选桌面快捷方式。
- [ ] 提供卸载入口，不删除 `%LOCALAPPDATA%\Clipboard` 用户数据。
- [ ] 使用应用图标和版本化输出文件名 `Clipboard-Setup-v0.1.0.exe`。

### Task 3: 添加 GitHub 自动发布工作流

**Files:**
- Create: `.github/workflows/release.yml`

- [ ] 仅响应 `v*` Tag 或手动触发。
- [ ] 安装 Rust 1.88、.NET 8 和 Inno Setup，运行发布脚本。
- [ ] 手动触发和 Tag 触发都校验语义版本，并将 Tag 与产物版本保持一致。
- [ ] 使用最小的 `contents: write` 权限创建 GitHub Release 并上传 EXE 与校验文件。

### Task 4: 更新发布文档

**Files:**
- Modify: `README.md`
- Create: `docs/release/windows-installer.md`

- [ ] 说明本地打包前置条件、命令和产物位置。
- [ ] 说明推送 `v0.1.0` Tag 的发布流程、GitHub 权限和下载方式。
- [ ] 说明未配置代码签名时 SmartScreen 的行为，以及后续签名配置位置。

### Task 5: 验证并提交

- [ ] 运行 PowerShell 语法检查、发布目录构建和安装器编译。
- [ ] 检查 EXE、校验文件、安装器架构和版本名称。
- [ ] 启动打包后的应用并确认进程存活。
- [ ] 使用中文提交信息提交发布、测试和文档变更。
