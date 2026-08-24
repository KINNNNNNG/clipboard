# 10,000 条历史搜索性能门设计

**状态：** 已确认，2026-08-24

**目标：** 在 10,000 条文本历史数据下，为首次子串搜索和组合筛选建立可重复的 Release 模式 P95 性能门；同时记录图片缓存和进程峰值内存趋势，但不以它们阻断日常功能验证。

## 背景

当前 `CoreService::search` 会从 SQLCipher 数据库读取全部有效记录、按 vault 和筛选条件过滤、重建 `SearchEngine`，再生成搜索响应。Windows 客户端还会经过 JSON、C ABI、DTO 和 `ObservableCollection` 更新。10,000 条记录时，功能测试只能证明正确性，不能发现搜索路径的明显退化。

本批次的硬门只覆盖稳定、无界面的 Rust Core 搜索主路径。Windows 端保留为显式诊断：现有无界面测试不能创建真实 `ListView`、等待布局或准确衡量第一帧渲染，因此不得把诊断结果伪装成 WinUI 首帧性能，也不得把机器相关的峰值内存设为硬阈值。

## 范围与边界

本批次新增两个独立入口，不加入 `scripts/verify-windows-client.ps1` 的常规验证：

- `scripts/measure-history-search.ps1` 运行 Rust Release 性能门。它在 P95 超过 200 ms 时失败，供受控发布/性能环境显式调用。
- `scripts/measure-windows-history-diagnostics.ps1` 运行 Windows 端诊断工具，输出 FFI/ViewModel 耗时、图片 LRU 容量行为和进程工作集趋势，但没有通过/失败的内存阈值。

常规 `cargo test`、Windows 单元测试和 GUI smoke 不执行性能样本。这样可以保留日常构建的确定性，并要求发布流程在受控机器上显式运行性能门。

本批次不优化搜索算法、不改变数据库 schema、不引入全文索引、不修改同步、剪贴板捕获、图片对象协议或 UI 样式。真实 `ListView` 首帧、DPI/高对比度人工矩阵、MSIX 签名和两台设备验收仍是后续阶段五任务。

## Core 性能门

新增 `crates/clipboard-core/tests/history_search_performance.rs`。测试使用 `Database::items().insert()` 预置 10,000 个合成 `ClipboardItem::Text`，然后关闭数据库并在每个样本中重新打开 `CoreService`。不循环调用 `IngestText`，因为该命令会为去重扫描现有历史，导致测试准备本身成为二次方工作。

数据集固定且不含真实剪贴板内容：

- 一共 10,000 条唯一短文本，使用 10 个确定性的来源应用和单调递增时间戳。
- 其中 100 条含固定子串 `performance-needle`；其余记录不含该子串。
- 组合筛选固定使用来源应用、时间范围和 `text` 类型，且与子串共同命中 10 条记录。
- 空查询返回全部 10,000 条，仅报告趋势，不参与 200 ms 硬门。

每个硬门场景执行 30 个独立样本。每个样本中的计时从首次 `CoreCommand::Search` 之前开始，到搜索响应返回之后结束；建库、预热、打开数据库、写入数据和结果打印均不计入。每轮都断言预期的结果数量，避免将错误路径当作性能结果。

P50 和 P95 使用排序后的 nearest-rank 算法：对于 30 个样本，P50 取第 15 个，P95 取第 29 个。子串搜索和组合筛选的 P95 必须各自不超过 200 ms；任一超限使测试失败。空查询同样输出 P50/P95 和结果数量，但不进行阈值断言。

性能测试标记为 `#[ignore]`，仅由专用脚本以 `--release --ignored --test-threads=1` 运行。测试输出只包含场景名、样本数、结果数、P50/P95 毫秒数和阈值结果，不输出合成文本、数据库路径、vault 密钥或任何用户数据。

## Windows 诊断

新增独立的 `tests/Clipboard.Windows.Performance` 可执行诊断项目，而不是向 `tests/Clipboard.Windows.Tests` 添加性能 `[Fact]`。普通 Windows 测试会被发布验证脚本无过滤执行，不能承载机器敏感的基准。

该工具使用真实 `ClipboardCoreClient` 和 `ClipboardPanelViewModel`，并以固定的 10,000 条合成文本数据运行以下指标：

1. FFI 搜索：计时 `ClipboardCoreClient.SearchAsync`，覆盖请求 JSON、C ABI、Rust/SQLCipher、响应缓冲区复制和响应 JSON 反序列化。
2. ViewModel 搜索：从设置 `QueryText` 或 `SetFilters` 开始，在 `IsLoading` 由 `true` 恢复为 `false` 且结果数正确时结束；该指标包含 50 ms 防抖、FFI、DTO 转 ViewModel 和 `ObservableCollection` 通知。
3. 初始空查询：单独报告 10,000 条返回的耗时与结果数，不与 200 ms 搜索门混合。
4. 图片 LRU 诊断：使用与主窗口相同容量 64 的 `BoundedLruCache<Guid, byte[]>` 写入 65 个确定性 PNG 字节数组，确认最旧项被移除并记录缓存填充前后工作集。它验证缓存容量和内存趋势，不代表可见 WinUI 图像首帧。

Windows 诊断的数据只能由 Rust 侧准备：新增仅供性能脚本调用的 `clipboard-core` 基准 fixture 二进制，使用已有 `Database` API 写入同一套 10,000 条合成记录。PowerShell 在系统临时目录创建唯一目录，调用该二进制后把目录和固定基准 vault ID 交给 Windows 诊断项目；双方使用同一个仅测试用途的固定 32 字节 key，但该 key 不写入命令行、标准输出、报告或仓库外文件。这样 C# 不会复制 SQLCipher schema、加密或迁移逻辑，且 10,000 条写入成本不进入诊断计时。

每个 Windows 搜索场景同样执行 30 个样本并报告 P50/P95。Rust 测试为每个场景输出一行不含内容的 JSON 指标；Windows 工具输出一个不含敏感字段的 JSON 报告，包含 Git 修订、Windows/.NET/Rust 版本、CPU 逻辑核数、物理内存、构建配置、样本数、结果数、P50/P95、`PeakWorkingSet64` 和托管堆大小。调用方可重定向或由 CI 归档输出；仓库不自动写入基准结果文件。

Windows 诊断必须在 Release x64、无并发捕获/同步/图片读取的空闲环境运行。它会先执行不计时的预热样本；每个正式样本使用新的 Client 和 ViewModel，但不声称能清除操作系统文件缓存。报告将该口径标记为“预热 OS 文件缓存、冷搜索对象”。

真实 UI 首帧另设人工/实验室门：在可见 STA WinUI 窗口中，从触发查询到 `HistoryList` 的目标容器生成并完成下一次布局或渲染回调。该测量需要独立的可见窗口 harness，不会在本批无界面诊断中实现或声明已覆盖。

## 架构与文件边界

```text
measure-history-search.ps1
  -> ignored Rust integration test (Release, hard P95 gate)
      -> seeded SQLCipher database -> fresh CoreService -> Search

measure-windows-history-diagnostics.ps1
  -> Clipboard.Windows.Performance executable (Release x64, report only)
      -> ClipboardCoreClient -> C ABI -> CoreService -> SQLCipher
      -> ClipboardPanelViewModel -> ObservableCollection
      -> BoundedLruCache image-capacity diagnostic -> process memory report
```

计划中的文件职责如下：

- `crates/clipboard-core/tests/history_search_performance.rs`：合成数据、统计函数、Core 硬门和脱敏指标输出。
- `crates/clipboard-core/src/bin/prepare_history_performance.rs`：只为 Windows 诊断准备临时合成数据库；不读取用户数据、不打印测试 key。
- `scripts/measure-history-search.ps1`：验证工具链并以串行 Release 模式执行该 ignored 测试。
- `tests/Clipboard.Windows.Performance/Clipboard.Windows.Performance.csproj`：独立的 Release x64 Windows 诊断宿主，不进入普通 xUnit 测试发现。
- `tests/Clipboard.Windows.Performance/Program.cs`：固定数据集、真实 FFI/ViewModel 样本、图片缓存容量检查和结构化报告。
- `src/Clipboard.Windows/Properties/AssemblyInfo.cs`：仅增加对诊断程序集的 `InternalsVisibleTo`，不扩大公共业务 API。
- `scripts/measure-windows-history-diagnostics.ps1`：构建并执行 Windows 诊断项目，保留原始退出码。
- `README.md` 和阶段五实施计划：说明两条显式命令、硬门适用环境和非阻断诊断口径。

## 失败处理与可重复性

- 任何合成数据数、命中数、P95 样本数或统计位置不符合设计时，测试立即失败，避免无效基准给出通过结论。
- Core 的子串或组合筛选 P95 大于 200 ms 时，专用脚本返回非零；空查询和 Windows 内存趋势只报告，不能因阈值猜测而失败。
- 工具链、Release 构建、原生 FFI 加载或诊断数据准备失败时，脚本保留原始错误并返回非零。
- 性能运行使用临时、唯一的数据目录并在进程结束后删除；不访问用户剪贴板历史、远端配置、恢复码、文件路径或凭据。
- 报告不得记录文本正文、图片字节、数据库路径、密钥、令牌、远端地址或任何操作系统用户名。

## 验收标准

- `pwsh -NoProfile -File scripts/measure-history-search.ps1` 在受控 Windows 11 x64 Release 环境退出 0，且两个硬门场景各有 30 个有效样本，P95 均不超过 200 ms。
- 同一命令的输出包含空查询趋势，但空查询不影响 200 ms 门。
- `pwsh -NoProfile -File scripts/measure-windows-history-diagnostics.ps1` 输出 FFI、ViewModel、空查询、图片 LRU 和内存趋势字段；它不因没有硬性内存基线而失败。
- 默认 `scripts/verify-windows-client.ps1 -SkipGuiSmoke` 不运行任何性能样本，原有功能验证范围不扩大。
- 基准代码、脚本输出、临时目录和文档均不含真实用户内容或敏感配置。
