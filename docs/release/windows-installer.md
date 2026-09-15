# Windows 安装器发布指南

当前版本为 `0.1.0`。项目使用 Inno Setup 生成单个 `Clipboard-Setup-v0.1.0.exe`，应用以 Windows App SDK 自包含模式发布，因此目标机器不需要单独安装 Windows App Runtime。

## 本地打包

安装以下工具：

- Rust `1.88`
- .NET SDK `8.0`
- Inno Setup 6，并确保 `ISCC.exe` 位于 PATH 或默认安装目录

在仓库根目录运行：

```powershell
pwsh -NoProfile -File scripts/package-windows.ps1 -Version 0.1.0
```

输出文件：

- `artifacts/windows/installer/Clipboard-Setup-v0.1.0.exe`：安装器
- `artifacts/windows/SHA256SUMS.txt`：安装器 SHA256 校验值
- `artifacts/windows/publish/`：自包含应用目录，供调试和安装器编译使用

只构建自包含应用目录、不编译安装器时运行：

```powershell
pwsh -NoProfile -File scripts/package-windows.ps1 -Version 0.1.0 -SkipInstaller
```

安装器默认安装到 `%LOCALAPPDATA%\Programs\Clipboard`，创建开始菜单入口，并提供可选的桌面快捷方式。卸载只删除程序文件，不删除 `%LOCALAPPDATA%\Clipboard` 下的历史、密钥、设置和日志。

## GitHub 自动发布

工作流文件为 `.github/workflows/release.yml`。它支持两种入口：

1. 推荐在 `main` 分支提交完成后创建并推送版本 Tag：

   ```powershell
   git tag v0.1.0
   git push origin v0.1.0
   ```

   Tag 必须符合 `vMAJOR.MINOR.PATCH`，例如 `v0.1.0`。GitHub Actions 会自动安装工具链和 Inno Setup，执行发布构建，生成安装器和 SHA256 文件，并创建名为 `Clipboard v0.1.0` 的 GitHub Release。

2. 在 GitHub 的 Actions 页面手动运行 `Windows Release`，输入 `0.1.0`。工作流会使用当前分支提交创建对应的 `v0.1.0` Release Tag。

仓库 Actions 权限需要允许工作流写入内容。仓库设置中打开 `Settings -> Actions -> General -> Workflow permissions -> Read and write permissions`；工作流自身也声明了 `contents: write`。

## 用户安装

从 GitHub Release 下载 `Clipboard-Setup-v0.1.0.exe` 和 `SHA256SUMS.txt`，在 PowerShell 中校验：

```powershell
Get-FileHash .\Clipboard-Setup-v0.1.0.exe -Algorithm SHA256
```

将输出的哈希与 `SHA256SUMS.txt` 比较后双击安装器即可。当前未配置代码签名证书，Windows SmartScreen 可能显示“未知发布者”；用户确认来源为项目 GitHub Release 后选择继续运行。配置签名证书后，可在 `.github/workflows/release.yml` 的安装器步骤后增加签名和证书校验。
