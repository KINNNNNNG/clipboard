# OSS 连接修复与全局日志设计

> **状态说明：** OSS V4 签名、脱敏诊断、全局日志和日志查看器已有代码与 fixture/自动测试；真实 OSS 的“测试连接”和“立即同步”人工验收仍未关闭。当前证据见 [Clipboard 开发状态](../../STATUS.md)。

## 目标

修复真实阿里云 OSS 连接失败问题，并为 Windows 客户端提供统一、可检索、可动态调节级别的全局日志能力，便于诊断远端同步和其他运行问题。

## 现状根因

现有 OSS V4 签名把 `host`、`x-oss-date`、`x-oss-content-sha256` 以及条件/COPY 请求头纳入规范请求，但 Authorization 的 `AdditionalHeaders` 始终为空。阿里云会因此拒绝签名。当前 HTTP 响应正文也被丢弃，Windows 只能显示固定的“连接失败/同步失败”，无法定位阶段或服务端错误码。

## 范围与不变约束

- 支持 OSS List、GET、条件 PUT、COPY 发布和 DELETE pending 的现有远端契约。
- WebDAV、Core 同步合并/outbox 确认语义保持不变。
- 日志不得包含剪贴板正文、图片数据、文件路径、恢复码、主密钥、账号密码、AccessKey Secret、Authorization、规范请求、对象名或远端响应正文。
- 文件束、图片对象和 `local_only` 项仍不上传。
- 日志级别、保留天数和大小上限属于非敏感客户端设置；默认级别为 `Info`、保留 7 天、最大 200 MB。

## OSS 修正设计

`OssStore` 的签名流程拆为可单测的规范请求构造和签名派生：

1. 对 URI path 和 query 进行 OSS V4 规范编码与排序，确保 Bucket、前缀、对象名和 ListObjectsV2 参数一致；空前缀以空字符串传递，不伪造 `/`。
2. 规范 Header 使用小写、压缩空白、排序后的名称和值。
3. `signed_headers` 与 Authorization 的 `AdditionalHeaders` 使用同一份额外 Header 列表；`host`、`x-oss-content-sha256`、`x-oss-date` 始终进入规范请求，条件/COPY 请求的 `if-none-match`、`x-oss-copy-source` 同时进入规范请求和 `AdditionalHeaders`。
4. COPY 的 canonical resource、source header、条件 header 与请求实际发送值保持一致。
5. 保留现有错误类别映射，并从 OSS XML 错误响应中只提取固定白名单错误码（如 `SignatureDoesNotMatch`、`AccessDenied`、`NoSuchBucket`），不保留响应正文、消息、端点或对象名。
6. `prefix` 为空时不发送伪造的 `/` 前缀；探测请求使用最小权限的 ListObjectsV2 查询。

为避免密钥泄露，签名帮助函数不实现 `Debug`，日志只记录 provider、操作阶段、HTTP 状态、白名单错误码和计数。

## 日志架构

新增 `GlobalLog`/`LogSink` 抽象，分为平台无关记录器和 Windows 文件存储：

- `LogLevel`: `Trace`, `Debug`, `Info`, `Warn`, `Error`，动态原子更新当前阈值。
- `LogEntry`: UTC 时间、级别、组件、事件名、脱敏字段；正文采用结构化键值，不接受任意异常字符串直接输出。
- `FileLogSink`: 写入 `%LOCALAPPDATA%\\Clipboard\\logs\\clipboard-YYYY-MM-DD.log`，按天滚动；启动和级别变更时清理超过 7 天或超过 200 MB 的最旧文件。
- 文件写入异步、有界队列；队列满时丢弃低级别记录并保留计数，不能阻塞剪贴板捕获、UI 或同步线程。
- 日志读取器只返回已落盘的文本快照，查看器分页/限制单次载入量，避免 200 MB 文件一次性进入内存。

所有跨层边界使用固定事件名：`core.open`、`core.command.start/end`、`sync.probe.start/end`、`sync.remote.list/get/upload/publish`、`oss.request.start/end`、`oss.error`、`settings.save`、`app.exception`。字段只包含 provider、阶段、耗时、状态、计数、脱敏错误类别/白名单码。

## Windows 托盘与日志查看器

托盘右键菜单新增“日志”，打开单实例 `LogWindow`：

- 顶部级别 ComboBox 修改全局阈值并立即持久化。
- 级别筛选、关键词搜索、自动刷新 Toggle、复制当前筛选结果、清空按钮。
- 默认显示最新日志，状态栏显示文件数量、总大小和最近写入时间，不显示秘密字段。
- 查看器关闭不停止记录；日志文件清空需要二次确认。

`ClientSettings` 增加非敏感 `LoggingSettings`，保存 `Level`、`RetentionDays`、`MaxSizeBytes`。数值范围固定校验：保留天数 1-30，最大大小 10 MB-1 GB；UI 默认值为 7 天和 200 MB，避免异常配置造成无限增长。

## OSS 失败诊断流程

连接测试和手动同步在每个边界记录开始/结束事件。失败时：

1. 记录操作阶段和耗时；
2. 将 HTTP 状态映射为固定同步错误；
3. 只提取白名单 OSS 错误码；
4. Windows 状态文本显示固定类别，日志查看器显示同样的脱敏字段；
5. 不记录请求 URL、对象路径、Authorization、XML 正文或凭据。

## 测试验收

- OSS 签名测试覆盖 canonical path/query/header、AdditionalHeaders、PUT/COPY 条件头、空前缀和稳定签名。
- OSS fixture 返回 `SignatureDoesNotMatch`、`AccessDenied`、`NoSuchBucket` 时，错误类别和白名单错误码正确且不含正文/密钥。
- 日志测试覆盖级别动态过滤、异步队列满载、滚动、7 天清理、200 MB 总量淘汰、清空和敏感字段脱敏。
- Windows 测试覆盖日志级别持久化、托盘“日志”命令路由、查看器筛选/复制、设置默认值和 XAML 资源配置。
- 完整验证继续运行 `scripts/test-core.ps1` 与 `scripts/verify-windows-client.ps1 -SkipGuiSmoke`，并进行真实 OSS 配置的手动连接/同步验收。
