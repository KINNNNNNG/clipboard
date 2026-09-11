# 历史加载失败分类与恢复设计

**状态：** 设计中，2026-09-11。属于[阶段五发布收尾路线](2026-08-25-stage-five-closure-roadmap.md)的 P0.2，关闭前不得进入 P1 跨设备验收。

**目标：** 为剪贴板历史打开和加载失败建立不含路径、内容或密钥的错误分类，区分短暂 Core 调用失败、数据库锁、密钥不匹配、数据库损坏和迁移失败，并让用户提示、重试策略和脱敏日志与该分类一一对应。

## 背景

当前实现只有两个笼统出口：

- `ClipboardPanelViewModel.ExecuteSearchAsync` 把除 `InvalidRegex` 以外的全部失败折叠为 `无法加载剪贴板历史。`，且只对 `CoreStatus.CoreError` 重试一次。
- `App.InitializeAsync` 的兜底 `catch` 把 vault 打开失败统一记为 `error_category=initialization` 并显示 `无法初始化剪贴板服务`。

Rust 侧同样没有分类依据：`clipboard_storage::Database::open` 只接收路径和密钥，`StorageError` 把 `rusqlite::Error` 原样透传，C ABI 只返回 `CoreStatus::CoreError`。因此数据库被其他实例锁住、密钥对应到别的 vault、文件损坏和迁移失败在用户看来完全一样，无法诊断也无法恢复。

## 范围与边界

- 只做分类、脱敏诊断和用户提示。不实现自动修复、不自动重建数据库、不删除或覆盖任何已有历史。
- 不改变数据库 schema、同步协议、剪贴板捕获、图片对象或 UI 样式。
- 不引入字符串错误通道：错误分类继续走稳定的整数 `CoreStatus`，`CoreStatus` 只追加新值，既有数值不变，保持 C ABI 兼容。
- 日志和状态文本只写固定枚举值，不写路径、SQL、SQLCipher 消息、密钥或剪贴板内容。

## 分类定义

| 类别 | 判定依据 | FFI 状态码 | 用户提示 |
| --- | --- | --- | --- |
| 短暂 Core 调用失败 | 其他 `CoreError` | `CoreError = 4` | 无法加载剪贴板历史。 |
| 数据库锁 | rusqlite `DatabaseBusy` / `DatabaseLocked` | `StorageLocked = 7` | 剪贴板历史数据库正被占用，请稍后重试。 |
| 密钥不匹配 | 磁盘 vault 标记与请求 vault 不一致 | `VaultKeyMismatch = 8` | 剪贴板数据库与当前密钥不匹配。 |
| 数据库无法解密 | 无 vault 标记且 `NotADatabase` | `VaultUnreadable = 9` | 无法解密剪贴板数据库，可能已损坏或密钥不匹配。 |
| 数据库损坏 | 有匹配 vault 标记且 `NotADatabase`，或 `DatabaseCorrupt` | `VaultCorrupt = 10` | 剪贴板历史数据库已损坏。 |
| 迁移失败 | `UnsupportedSchemaVersion` / `InvalidMigrationHistory` | `StorageMigration = 11` | 剪贴板历史数据库版本不受支持。 |

`NotADatabase` 在 SQLCipher 下既可能是密钥错误也可能是首页损坏，单凭 rusqlite 错误码无法区分。因此需要一个非秘密的旁路标记来确定数据库归属，见下一节。

## Vault 标记

在数据目录写入 `history.vault`，内容为该 vault 的 UUID 文本。UUID 是随机标识，不来自用户内容，也不参与派生密钥，因此不是秘密；数据库每个条目本来就记录同一个 vault UUID。

`clipboard_storage::Database::open_vault(data_dir, vault_id, key)` 的判定顺序：

1. 读取标记：标记存在且与请求 vault 不一致时立即返回 `VaultMismatch`，不打开数据库、不写任何文件，已有历史保持不变。
2. 打开数据库：`NotADatabase` 按表中规则解析为 `VaultUnreadable` 或 `VaultCorrupt`；`Locked`、`Corrupt` 原样返回。
3. 打开成功且此前没有标记时补写标记；补写失败只降低后续判定精度，不影响打开结果，因此按尽力而为处理。

标记使用“临时文件 + 重命名”原子写入，内容固定为小写 UUID 加换行。标记缺失只影响 `NotADatabase` 的细分，不影响数据库本身的正确性。

## 重试与恢复

- 仅 `CoreError` 和 `StorageLocked` 各自重试一次，因为二者可能由瞬时竞争导致。
- `VaultKeyMismatch`、`VaultUnreadable`、`VaultCorrupt`、`StorageMigration` 不重试：重复尝试不会改变结果，只会延迟提示。
- 任何失败都保留当前 `Items`，只有成功响应才替换列表，避免一次失败清空可见历史。
- 分类只改变提示和日志，不触发删除、重建或迁移。

## 架构与文件边界

```text
clipboard-ffi (C ABI)
  -> CoreStatus 追加 7..11，open_v2 与 execute 共用 status_for_core_error
      -> clipboard-core CoreService::open 调用 Database::open_vault
          -> clipboard-storage StorageError 分类 + vault 标记
              -> SQLCipher / rusqlite 错误码
Windows 客户端
  -> CoreStatus 枚举同值扩展
      -> ClipboardPanelViewModel 提示与重试策略
      -> App.InitializeAsync 打开失败提示与脱敏日志类别
```

计划中的文件职责：

- `crates/clipboard-storage/src/vault_marker.rs`：读写 `history.vault`，原子写入。
- `crates/clipboard-storage/src/error.rs`：新增 `Locked`、`Unreadable`、`Corrupt`、`VaultMismatch`、`VaultMarkerInvalid`、`Io`，并用手写 `From<rusqlite::Error>` 完成错误码分类。
- `crates/clipboard-storage/src/database.rs`：新增 `Database::open_vault`，保留 `Database::open` 供测试和内存库使用。
- `crates/clipboard-core/src/service.rs`：`CoreService::open` 改用 vault 作用域打开。
- `crates/clipboard-ffi/src/status.rs`、`crates/clipboard-ffi/src/abi.rs`：追加状态码并在 open/execute 两处映射。
- `src/Clipboard.Windows/Core/CoreStatus.cs`：C# 枚举同值扩展。
- `src/Clipboard.Windows/ViewModels/ClipboardPanelViewModel.cs`、`src/Clipboard.Windows/App.xaml.cs`：提示、重试和脱敏日志类别。

## 失败处理与可重复性

- 标记文件损坏时返回 `VaultMarkerInvalid`，不猜测 vault，也不改写标记。
- 故障注入测试使用临时目录，不接触真实用户 vault、密钥、远端配置或剪贴板内容。
- 断言必须覆盖：分类状态码、重试次数、用户提示文本、失败后 `Items` 保留，以及分类修复后重新加载成功。
- 用户的 vault 密钥只在测试内以固定常量出现，不写入日志、标准输出或仓库文件。

## 验收标准

- 锁、密钥不匹配、无法解密、损坏、迁移失败各自返回唯一 `CoreStatus`，同一场景重复运行结果稳定。
- `VaultMismatch` 判定发生在打开数据库之前，原始历史文件字节不变。
- 只有 `CoreError` 和 `StorageLocked` 触发一次重试；其余分类只调用一次 Core。
- 失败后保留已有历史，恢复后重新加载能显示正确结果。
- 日志类别为固定枚举值，不含路径、密钥、SQL 或内容。
