# 客户端自动更新设计

**状态：** 待评审
**日期：** 2026-09-15
**首个带更新能力的版本：** 0.2.0

## 背景

应用以 EXE 安装包分发，按当前用户安装在 `%LOCALAPPDATA%\Programs\Clipboard`。用户目前只能自行到 GitHub Releases 下载新版本。本设计让已安装的客户端能够发现并安装新版本。

## 目标

- 客户端能检查本仓库 Releases 是否存在更新版本。
- 用户确认后，客户端下载官方安装器、校验 SHA256、启动安装器并退出自身。
- 历史、密钥、设置与日志在更新前后保持不变。

## 非目标

- 不实现增量或差分更新。
- 不实现静默自动安装：下载完成后必须由用户确认。
- 不支持第三方更新源，更新源固定为本仓库 Releases。
- 不支持私有仓库或凭据配置：仓库已公开，匿名读取即可。

## 已确认的产品决策

| 决策点 | 结论 |
| --- | --- |
| 仓库可见性 | 公开，匿名读取 Releases |
| 触发方式 | 设置页手动检查，加可选的启动时自动检查 |
| 安装确认 | 下载完成后由用户确认安装 |
| 更新源 | 固定本仓库 Releases |
| 检查失败处理 | 提示失败并可重试，保留本地历史 |

## 版本来源

- `src/Clipboard.Windows/Clipboard.Windows.csproj` 增加 `<Version>`，与 Rust 工作区版本保持一致；实现该功能时提升为 `0.2.0`，此后随发版递增。
- 发布脚本继续以 `-p:Version=<Tag 版本>` 覆盖，保证安装包版本与 Tag 一致。
- 客户端从程序集信息读取当前版本，并把版本字符串作为参数传给 core；core 不自行推断当前版本。

## 组件与数据流

1. 客户端读取当前版本，发送 `CheckUpdate { current_version, include_prerelease: false }`。
2. core 请求 `https://api.github.com/repos/KINNNNNNG/clipboard/releases/latest`，解析 `tag_name` 与 `assets[]`。只接受形如 `Clipboard-Setup-v<semver>.exe` 的安装器资产，并与 `current_version` 做语义版本比较。
3. core 返回 `UpdateCheck { available, latest_version, installer_url, checksums_url, release_url, published_at }`；无更新时 `available` 为 false。
4. 用户确认后，客户端发送 `DownloadUpdate { version, installer_url, checksums_url }`。
5. core 把 `SHA256SUMS.txt` 与安装器下载到 `%LOCALAPPDATA%\Clipboard\updates\<version>\`，逐个校验哈希，返回安装器绝对路径与文件大小。
6. 客户端启动安装器并退出自身；安装器关闭残留进程、覆盖程序文件并重启应用。

## 接口

- 新增 `CoreCommand::CheckUpdate` 与 `CoreCommand::DownloadUpdate`，复用现有 `clipboard_core_execute` 入口。不新增 C ABI 函数，`api_version` 保持不变。
- 新增 `CoreResponse::UpdateCheck` 与 `CoreResponse::UpdateDownload`。
- 新增状态码 `UpdateCheckFailed = 12`、`UpdateDownloadFailed = 13`、`UpdateChecksumMismatch = 14`，既有 0–11 保持不变。
- 命令层只接收客户端已经取得的 URL，core 负责域名白名单校验。

## 用户界面

设置页「应用」分组新增「更新」区域：

- 当前版本，只读文本。
- 「检查更新」按钮；检查或下载进行中时禁用并显示进度。
- 状态文本：已是最新、发现 vX.Y.Z、正在下载 N%、校验失败、检查失败。
- 「启动时自动检查更新」开关，默认关闭。
- 发现新版本后的操作：下载并安装、跳过此版本、稍后。

## 设置与存储

`ClientSettings` 增加两个非敏感字段：`UpdateCheckOnStartup`（布尔，默认 false）与 `SkippedUpdateVersion`（可空字符串，默认 null）。更新元数据不写入 vault；下载目录在安装完成或用户取消后清理。

## 安全约束

- 只接受 https 且主机属于 `api.github.com`、`github.com` 或资产重定向域名。
- 安装器资产名必须匹配 `Clipboard-Setup-v<semver>.exe`，文件大小上限 200 MB。
- 哈希必须与发布的 `SHA256SUMS.txt` 完全一致，才允许出现「安装」操作。
- 安装器未签名，因此信任链由 HTTPS 与 SHA256 校验共同构成；文档继续说明 SmartScreen 的提示行为。
- 日志只记录类别、版本号与计数，不记录 URL 查询参数、令牌或本机路径。

## 失败分类

| 状态码 | 场景 | 客户端行为 |
| --- | --- | --- |
| 12 | 网络失败、JSON 解析失败、资产缺失或命名非法 | 提示检查失败，可重试，保留历史 |
| 13 | 下载中断或写入失败 | 提示下载失败，可重试 |
| 14 | 哈希不匹配 | 删除临时文件，提示校验失败，不提供安装操作 |

## 测试策略

- Rust 单元测试：语义版本比较（预发布、补零、大版本）、Release JSON 解析（缺失资产、非法文件名、非 https URL）、哈希校验成功与不匹配、失败分类映射、传输层错误注入。
- 契约测试：`SkippedUpdateVersion` 与 `UpdateCheckOnStartup` 的读写与校验。
- 客户端测试：ViewModel 状态文本、按钮启用条件、跳过版本持久化、启动安装器失败时的提示。
- 自动测试不访问真实 GitHub，通过可注入传输层或本地 fixture 覆盖。
- 新增守卫测试，确保更新状态码 12–14 与既有 0–11 不重叠。

## 未决事项

- 代理环境下是否需要手动配置更新代理；当前依赖系统代理设置。
- 安装器启动后客户端退出的时序，是否需要等待安装器完成接管再退出进程。
- 是否需要为「跳过此版本」提供重置入口。

## 验收标准

- 当前版本低于最新版本时，手动检查报告新版本，并在用户确认后完成下载、校验与安装。
- 当前版本已是最新时，界面提示已是最新。
- 哈希不匹配时不出现安装操作，且不保留临时安装文件。
- 检查或下载失败时，客户端保留本地历史并允许重试。
- 更新前后的历史、密钥与设置保持不变。
- 启动时自动检查默认关闭；开启后仅在后台检查，不自动下载。
