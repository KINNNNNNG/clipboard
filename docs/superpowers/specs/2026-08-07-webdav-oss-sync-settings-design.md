# WebDAV、OSS 与同步设置设计

## 目标

在现有加密文本同步基础上，增加可互换的 WebDAV 和阿里云 OSS 远端适配器，以及 Windows 的同步设置页。用户可以选择任一服务、经 DPAPI 安全保存凭据、测试连接并显式执行同步。

## 范围

本任务支持文本事件、收藏状态和删除墓碑。文件束、文件缓存、文件路径、图片对象、恢复码和主密钥仍不离开本机。首版不实现后台同步、重试调度、DPAPI 导入导出或图片同步。

## 架构

Rust 在 `clipboard-sync` 中定义远端对象契约，适配器只处理公开段名和密文。Core 保持既有的拉取、认证解密、无回声合并、加密上传和 outbox 确认编排。

```text
WinUI Sync Settings
  -> DPAPI credential store
  -> explicit Core sync request
  -> WebDavTransport | OssTransport
  -> encrypted journal segments
```

适配器必须实现列出完成段、读取段、写入唯一 `.pending` 对象和原子发布为 `.enc` 对象。所有传输错误映射为不包含端点、对象名、用户名或凭据的同步错误。

## WebDAV

WebDAV 使用 HTTPS Basic Auth。适配器通过 `PROPFIND` 列出段、`GET` 读取、条件 `PUT` 写入 pending 对象、`MOVE` 发布完成对象。连接测试只探测远端根路径，不上传正文或创建历史事件。

## OSS

OSS 使用 AccessKey ID、AccessKey Secret 和标准阿里云 OSS 请求签名。配置包含端点、区域、Bucket 和可选前缀。适配器用对象列举、`GET`、条件上传和服务端对象复制或原子发布语义实现相同的完成段协议。

## Windows 设置

设置窗口分为两个 Tab：

- 常规设置：保留历史、图片、文件缓存、快捷键、启动和主题设置。
- 同步设置：同步启用状态、服务类型、服务端点、WebDAV 根路径或 OSS Bucket/区域/前缀、设备 ID、测试连接和手动同步。

账号、密码、AccessKey Secret 不进入 `ClientSettings` JSON、日志、错误消息或 `Debug` 输出。Windows 平台层用 DPAPI 对凭据加密，设置模型只保存非敏感配置和凭据引用。每次同步仅在内存中解密凭据并传给 Core。

## 失败语义

- 认证、网络、限流和远端冲突不影响本机历史，未成功上传的 outbox 条目保持待同步。
- 篡改段、错误 vault 和不支持版本不会进入本地历史。
- 连接测试和同步响应只返回脱敏状态、计数和错误类别。
- 文件束和所有 `local_only` 记录在 Core 和 Sync 两层拒绝，绝不调用任一远端适配器。

## 验收

1. WebDAV 与 OSS 适配器通过同一组列出、读取、pending 发布和冲突契约测试。
2. 两个 Core 经任一适配器同步后收敛；重复同步不重复写入。
3. 网络或认证失败保留 outbox；篡改段和错误 vault 不合并。
4. DPAPI 往返可恢复凭据，持久化设置、日志、错误和测试快照不含明文凭据。
5. 设置页显示“常规设置 / 同步设置”Tab，能保存、测试连接并手动触发同步。
6. 远端对象、诊断和同步响应均不含剪贴板明文、文件路径、恢复码或密钥。
