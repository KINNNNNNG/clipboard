# 历史搜索性能门实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**目标：** 为 10,000 条文本历史提供可重复的 Rust 搜索 P95 硬门，以及独立、非阻断的 Windows FFI、ViewModel、图片 LRU 与内存趋势诊断。

**架构：** Rust 基准夹具二进制在临时目录中直接通过 Database::items().insert() 写入固定的 SQLCipher 历史库，避免 IngestText 的去重扫描。ignored Rust 集成测试在同一夹具上为每个样本新建 CoreService，只将搜索调用计入 P95。Windows 控制台项目复用真实 ClipboardCoreClient 和 ClipboardPanelViewModel，输出脱敏 JSON 指标；它由专用 PowerShell 脚本在 Release x64 下显式调用，不加入默认 xUnit 或发布验证。

**技术栈：** Rust 1.88、SQLCipher、Cargo ignored integration test、PowerShell 7、.NET 8、WinUI 3、xUnit、System.Text.Json。

---

## 文件结构

    crates/clipboard-core/src/bin/prepare_history_performance.rs
        只为性能脚本创建固定的 10,000 条 SQLCipher 基准历史库。
    crates/clipboard-core/tests/history_search_performance.rs
        ignored 的 Rust Core 搜索 P50/P95 门与结果数断言。
    scripts/measure-history-search.ps1
        串行调用 Release ignored Rust 性能测试。
    src/Clipboard.Windows/Clipboard.Windows.csproj
        按 Debug/Release 配置复制相匹配的 clipboard_ffi.dll。
    scripts/verify-windows-client-toolchain.ps1
        接收 Debug/Release 并验证对应 Rust FFI 产物。
    tests/Clipboard.Windows.Tests/Platform/PerformanceEntrypointConfigurationTests.cs
        锁定 Release FFI 映射与性能入口不会进入默认发布验证。
    tests/Clipboard.Windows.Performance/Clipboard.Windows.Performance.csproj
        独立的 Release x64 诊断宿主，引用现有 Windows 客户端代码。
    tests/Clipboard.Windows.Performance/Program.cs
        FFI、ViewModel、空查询、LRU 和工作集诊断，输出一个脱敏 JSON 报告。
    src/Clipboard.Windows/Properties/AssemblyInfo.cs
        只向 Clipboard.Windows.Performance 开放内部诊断所需类型。
    scripts/measure-windows-history-diagnostics.ps1
        创建唯一临时目录、准备 Rust fixture、构建并运行诊断程序。
    README.md
        说明两条显式性能命令、受控运行环境和不覆盖的真实 ListView 首帧。

### Task 1：建立固定 Rust 基准夹具与 ignored 搜索门

**文件：**
- Create: crates/clipboard-core/src/bin/prepare_history_performance.rs
- Create: crates/clipboard-core/tests/history_search_performance.rs

- [ ] **Step 1：先写 ignored 性能测试。**

创建 history_search_performance.rs，使用固定常量：

    const SAMPLE_COUNT: usize = 30;
    const TREND_SAMPLE_COUNT: usize = 3;
    const THRESHOLD: Duration = Duration::from_millis(200);
    const VAULT_ID: Uuid = Uuid::from_u128(0x5a17);
    const KEY: [u8; 32] = [0x6a; 32];

写一个 ignored 测试 history_search_p95_stays_within_budget。它先用 CARGO_BIN_EXE_prepare_history_performance 调用待实现夹具，在唯一临时目录准备数据，然后测量三个场景：

    子串：pattern = "performance-needle"，默认 SearchFilters，30 样本，100 结果。
    组合：相同 pattern；created_after_ms = Some(0)；
          created_before_ms = Some(900)；
          source_apps = ["benchmark-source-00.exe"]；
          kinds = ["text"]；30 样本，10 结果。
    空查询：pattern = ""，默认 SearchFilters，3 样本，10,000 结果，只报告趋势。

测量辅助函数每一轮在 CoreService::open 后开始 Instant 计时，执行 CoreCommand::Search，断言 response.search_items().len() 等于预期数量。SearchRequest 没有 Clone，实现必须以闭包或场景构造函数在每轮新建请求。nearest_rank 按 ceil(n * p / 100) - 1 取排序样本：30 个样本时 P50 为索引 14，P95 为索引 28。只以 Duration::from_millis(200) 判断两条硬门；每个场景用 serde_json::json! 输出 scenario、samples、results、p50_ms、p95_ms、threshold_ms 与 passed，不能输出文本正文、目录、key 或 vault。

- [ ] **Step 2：确认性能测试为红。**

运行：

    cargo test -p clipboard-core --release --test history_search_performance -- --ignored --test-threads=1

预期：失败，因为 fixture 二进制尚不存在或测试无法准备 fixture，而不是因为阈值、时间或结果数断言。

- [ ] **Step 3：实现只供性能使用的 fixture 二进制。**

二进制只接受一个必填参数 --data-dir <目录>，拒绝未知参数和缺少目录。先调用 std::fs::create_dir_all，然后打开 data_dir/history.db：

    let database = Database::open(&data_dir.join("history.db"), &KEY)?;

循环 0..10_000，直接调用 database.items().insert(&item)，绝不调用 IngestText。每条数据使用：

    let source_app = format!("benchmark-source-{:02}.exe", index % 10);
    let text = if index % 100 == 0 {
        format!("benchmark-item-{index:05}-performance-needle")
    } else {
        format!("benchmark-item-{index:05}")
    };
    let item = ClipboardItem::new(
        Uuid::from_u128(index as u128 + 1),
        VAULT_ID,
        ClipboardContent::Text(text),
        source_app,
        index as i64,
    );

在释放 Database 后只向标准输出写入一行 {"fixture":"history_search","items":10000}。夹具不得读取用户剪贴板、用户数据库、远端配置或环境中的密钥。

- [ ] **Step 4：确认性能门转绿。**

运行：

    cargo test -p clipboard-core --release --test history_search_performance -- --ignored --test-threads=1 --nocapture
    cargo fmt --all --check
    cargo test -p clipboard-core --test history_search_performance

预期：Release ignored 测试输出 100、10、10,000 三个结果数；两个 P95 均在 200 ms 内；普通 cargo test 仅显示 ignored 测试，不运行样本。

- [ ] **Step 5：提交 Rust 性能门。**

    git add crates/clipboard-core/src/bin/prepare_history_performance.rs crates/clipboard-core/tests/history_search_performance.rs
    git commit -m "测试：增加历史搜索性能门"

### Task 2：添加显式 Rust 性能脚本

**文件：**
- Create: scripts/measure-history-search.ps1
- Modify: tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj
- Create: tests/Clipboard.Windows.Tests/Platform/PerformanceEntrypointConfigurationTests.cs

- [ ] **Step 1：先写脚本静态契约测试。**

在 PerformanceEntrypointConfigurationTests.cs 中读取输出目录 Fixtures/measure-history-search.ps1，并断言包含 --release、--ignored、--test-threads=1 和 --nocapture；断言不包含 verify-windows-client.ps1。先在 Windows 测试项目中添加 Content 链接，让脚本被复制为 Fixtures/measure-history-search.ps1。

- [ ] **Step 2：运行红灯测试。**

    dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~PerformanceEntrypointConfigurationTests"

预期：失败，因为性能脚本 Fixture 尚不存在。

- [ ] **Step 3：实现串行 Release 脚本。**

脚本必须采用下面的控制流，保留 Cargo 原始输出和非零退出状态：

    $ErrorActionPreference = 'Stop'
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    Push-Location $repositoryRoot
    try {
        & cargo test -p clipboard-core --release --test history_search_performance -- --ignored --test-threads=1 --nocapture
        if ($LASTEXITCODE -ne 0) {
            throw 'History search performance gate failed.'
        }
    }
    finally {
        Pop-Location
    }

脚本不写结果文件，不调用默认 Windows 发布验证。

- [ ] **Step 4：确认脚本门转绿并执行入口。**

重复 Step 2 的 dotnet test，再运行：

    pwsh -NoProfile -File scripts/measure-history-search.ps1

预期：两个硬门通过时退出 0，任一 P95 超过 200 ms 时退出非零。

- [ ] **Step 5：提交显式 Rust 入口。**

    git add scripts/measure-history-search.ps1 tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj tests/Clipboard.Windows.Tests/Platform/PerformanceEntrypointConfigurationTests.cs
    git commit -m "脚本：增加历史搜索性能入口"

### Task 3：按构建配置提供匹配的 FFI DLL

**文件：**
- Modify: src/Clipboard.Windows/Clipboard.Windows.csproj
- Modify: scripts/verify-windows-client-toolchain.ps1
- Modify: tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj
- Modify: tests/Clipboard.Windows.Tests/Platform/PerformanceEntrypointConfigurationTests.cs

- [ ] **Step 1：写 Debug/Release 映射的失败测试。**

测试读取 Fixtures/Clipboard.Windows.csproj 与 Fixtures/verify-windows-client-toolchain.ps1，断言项目包含以下两个条件项，工具链脚本包含 ValidateSet('Debug', 'Release') 和 $Configuration.ToLowerInvariant()：

    <None Include="..\..\target\debug\clipboard_ffi.dll"
          Link="clipboard_ffi.dll"
          CopyToOutputDirectory="PreserveNewest"
          Condition="'$(Configuration)' == 'Debug'" />
    <None Include="..\..\target\release\clipboard_ffi.dll"
          Link="clipboard_ffi.dll"
          CopyToOutputDirectory="PreserveNewest"
          Condition="'$(Configuration)' == 'Release'" />

- [ ] **Step 2：运行红灯测试。**

运行 Task 2 的定向 dotnet test。预期：失败，因为当前项目和脚本只指向 Debug FFI。

- [ ] **Step 3：实现双配置 DLL 与工具链参数。**

将现有无条件 Debug DLL 项替换为 Step 1 的两个 XML 项。向工具链脚本顶部添加：

    param(
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration = 'Debug'
    )

并按配置找到 Rust DLL：

    $rustConfiguration = $Configuration.ToLowerInvariant()
    $ffiLibrary = Join-Path $PSScriptRoot "..\target\$rustConfiguration\clipboard_ffi.dll"
    if (-not (Test-Path -LiteralPath $ffiLibrary)) {
        throw "target/$rustConfiguration/clipboard_ffi.dll is required."
    }

默认 Debug 保持现有 verify-windows-client.ps1 行为。

- [ ] **Step 4：确认映射转绿。**

    cargo build -p clipboard-ffi
    cargo build -p clipboard-ffi --release
    dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~PerformanceEntrypointConfigurationTests"
    pwsh -NoProfile -File scripts/verify-windows-client-toolchain.ps1 -Configuration Debug
    pwsh -NoProfile -File scripts/verify-windows-client-toolchain.ps1 -Configuration Release

预期：静态测试与两种工具链检查均通过。

- [ ] **Step 5：提交 Release FFI 映射。**

    git add src/Clipboard.Windows/Clipboard.Windows.csproj scripts/verify-windows-client-toolchain.ps1 tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj tests/Clipboard.Windows.Tests/Platform/PerformanceEntrypointConfigurationTests.cs
    git commit -m "构建：支持 Release FFI 性能诊断"

### Task 4：实现 Windows 独立诊断宿主与脚本

**文件：**
- Create: tests/Clipboard.Windows.Performance/Clipboard.Windows.Performance.csproj
- Create: tests/Clipboard.Windows.Performance/Program.cs
- Modify: src/Clipboard.Windows/Properties/AssemblyInfo.cs
- Create: scripts/measure-windows-history-diagnostics.ps1
- Modify: tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj
- Modify: tests/Clipboard.Windows.Tests/Platform/PerformanceEntrypointConfigurationTests.cs

- [ ] **Step 1：先锁定 Windows 性能入口边界。**

向 PerformanceEntrypointConfigurationTests 添加一个测试，读取 Fixtures/measure-windows-history-diagnostics.ps1、Fixtures/Clipboard.Windows.Performance.csproj 和 Fixtures/verify-windows-client.ps1。它断言前两者分别包含 prepare_history_performance、-Configuration Release、Clipboard.Windows.Performance.csproj、<OutputType>Exe</OutputType>；并断言默认发布脚本不包含 Clipboard.Windows.Performance。把三份源文件作为测试 Fixture 复制到输出目录。

- [ ] **Step 2：确认入口边界测试失败。**

    dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~PerformanceEntrypointConfigurationTests"

预期：失败，提示缺少独立性能项目和脚本，而不是默认发布验证发生变化。

- [ ] **Step 3：建立内部可见的独立诊断项目。**

在 AssemblyInfo.cs 保留已有 friend assembly，并添加：

    [assembly: InternalsVisibleTo("Clipboard.Windows.Performance")]

诊断项目必须是 net8.0-windows10.0.26100.0、win-x64、x64 的 Exe，引用 src/Clipboard.Windows/Clipboard.Windows.csproj，并禁用 Windows App SDK 自动初始化。项目中的两个条件 None 项复用 Task 3 的 Debug/Release DLL 映射，确保 -c Release 复制 target/release/clipboard_ffi.dll。

- [ ] **Step 4：实现不含 UI 首帧声明的 JSON 诊断程序。**

Program.cs 解析三个参数 --data-dir、--vault-id、--configuration，拒绝未知或缺失参数。它使用固定的仅测试 32 字节 key 打开真实 ClipboardCoreClient，创建 ClipboardPanelViewModel 和仅用于未调用粘贴的空 IClipboardItemPasteService 实现。

实现以下固定帮助函数：

    static long Percentile(IReadOnlyList<long> samples, int percentile)
    {
        long[] ordered = samples.Order().ToArray();
        int rank = (int)Math.Ceiling(percentile * ordered.Length / 100d);
        return ordered[rank - 1];
    }

    static async Task<Metric> MeasureAsync(
        int samples,
        int expectedResults,
        Func<Task<int>> execute)
    {
        var elapsed = new List<long>(samples);
        for (int index = 0; index < samples; index++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int results = await execute();
            watch.Stop();
            if (results != expectedResults)
            {
                throw new InvalidOperationException("Unexpected diagnostic result count.");
            }
            elapsed.Add(watch.ElapsedMilliseconds);
        }
        return new Metric(samples, expectedResults, Percentile(elapsed, 50), Percentile(elapsed, 95));
    }

先运行一轮不计时预热。FFI 子串和组合筛选各运行 30 个新 client 样本；ViewModel 样本从设置 QueryText 或 SetFilters 开始，到 IsLoading 为 false 且 Items.Count 正确为止，必须包含 50 ms debounce、DTO 转换和 ObservableCollection 更新。空查询只运行 3 次并单独报告。每轮 client 与 ViewModel 都新建并释放，不宣称清除操作系统文件缓存。

图片诊断创建 new BoundedLruCache<Guid, byte[]>(64)，写入 65 个确定性的短 PNG 字节数组，断言 Count == 64、第一个键不存在、最后一个键仍存在。记录该操作前后 Process.GetCurrentProcess().WorkingSet64。输出 JsonSerializer.Serialize 的对象，字段至少包含 git_revision、windows_version、dotnet_version、rust_version、logical_processor_count、physical_memory_bytes、configuration、warm_os_file_cache、ffi_substring、ffi_combined、view_model_substring、view_model_combined、empty_query、image_lru、peak_working_set_bytes、managed_heap_bytes。所有指标只记录数值，不记录测试文本、数据库目录、key、vault、用户名或远端信息。

- [ ] **Step 5：实现临时目录 PowerShell 入口。**

脚本使用 [IO.Path]::GetTempPath() 和 [Guid]::NewGuid().ToString('N') 建立唯一目录，并按照下面顺序执行：

    cargo build -p clipboard-ffi --release
    pwsh -NoProfile -File scripts/verify-windows-client-toolchain.ps1 -Configuration Release
    cargo run -p clipboard-core --release --bin prepare_history_performance -- --data-dir $dataDirectory
    dotnet run --project tests/Clipboard.Windows.Performance/Clipboard.Windows.Performance.csproj -c Release -p:Platform=x64 -p:WindowsAppSDKSelfContained=true --no-restore -- --data-dir $dataDirectory --vault-id 00000000-0000-0000-0000-000000005a17 --configuration Release

使用 try/finally，以 Remove-Item -LiteralPath $dataDirectory -Recurse -Force 清理这个已验证的唯一临时子目录；保留 Cargo、fixture、build 或程序的原始非零退出码。脚本不接受数据目录或 key 参数，不进入默认发布验证。

- [ ] **Step 6：运行绿灯边界测试和完整诊断。**

    dotnet test tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~PerformanceEntrypointConfigurationTests"
    pwsh -NoProfile -File scripts/measure-windows-history-diagnostics.ps1

预期：xUnit 只验证入口配置；诊断程序输出一个 JSON 报告，包含四个搜索指标、空查询、LRU 和内存字段，并且不会因峰值内存没有硬阈值而失败。

- [ ] **Step 7：提交 Windows 诊断。**

    git add src/Clipboard.Windows/Properties/AssemblyInfo.cs tests/Clipboard.Windows.Performance scripts/measure-windows-history-diagnostics.ps1 tests/Clipboard.Windows.Tests/Clipboard.Windows.Tests.csproj tests/Clipboard.Windows.Tests/Platform/PerformanceEntrypointConfigurationTests.cs
    git commit -m "测试：增加 Windows 历史性能诊断"

### Task 5：文档、计划记录与完整回归

**文件：**
- Modify: README.md
- Modify: docs/superpowers/plans/2026-08-24-history-search-performance.md

- [ ] **Step 1：补充 README 的显式性能验证说明。**

在现有发布验证段落之后增加两条显式命令：

    pwsh -NoProfile -File scripts/measure-history-search.ps1
    pwsh -NoProfile -File scripts/measure-windows-history-diagnostics.ps1

说明第一条命令在 10,000 条合成文本历史上执行 Release Rust 搜索门，子串和组合筛选各 30 个样本的 P95 均必须不超过 200 ms；10,000 条空查询仅报告趋势。说明第二条命令输出真实 FFI、ViewModel、固定容量图片 LRU 和进程内存趋势，不设置机器相关内存阈值，也不代表 WinUI ListView 的可见首帧。日常 cargo test 与 scripts/verify-windows-client.ps1 不执行性能样本。

- [ ] **Step 2：执行新鲜的全量验证。**

按顺序运行：

    git diff --check
    cargo fmt --all --check
    cargo clippy --workspace --all-targets -- -D warnings
    cargo test --workspace --all-targets
    pwsh -NoProfile -File scripts/measure-history-search.ps1
    pwsh -NoProfile -File scripts/measure-windows-history-diagnostics.ps1
    pwsh -NoProfile -File scripts/verify-windows-client.ps1 -SkipGuiSmoke

预期：默认 Rust 与 Windows 发布验证不执行性能样本；两个显式入口成功，Windows JSON 不含敏感字段。若任一命令失败，先修复根因并重新执行同一命令，不把失败结果写成通过。

- [ ] **Step 3：记录实际证据并提交文档。**

只有在对应命令真实通过后，将本计划中的步骤标记为 [x]，并追加日期、命令、退出码、Rust 两个 P95、Windows 报告字段存在性及默认验证未运行性能项目的证据。记录不得含合成文本、临时目录、密钥、vault ID、用户名或远端配置。

    git add README.md docs/superpowers/plans/2026-08-24-history-search-performance.md
    git commit -m "文档：记录历史搜索性能验收"

## 计划自审

- 设计要求的固定 10,000 条数据、100/10 命中、30/3 样本、nearest-rank、200 ms Rust 硬门和空查询趋势分别由 Task 1 覆盖。
- Release FFI 映射、独立 Windows 控制台、真实 Client/ViewModel/LRU 诊断和无内存硬门由 Task 3 与 Task 4 覆盖。
- 默认测试与 verify-windows-client.ps1 不运行性能样本由 Task 2 与 Task 4 的静态契约测试锁定。
- 每项生产行为先有失败测试或失败入口，再实施最小代码；所有提交消息均为中文。
- 范围不包含搜索算法优化、schema、同步逻辑、可见 ListView 首帧、DPI/高对比度、MSIX 签名或跨设备人工验收。
