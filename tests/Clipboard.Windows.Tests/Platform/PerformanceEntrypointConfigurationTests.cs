using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class PerformanceEntrypointConfigurationTests
{
    private const string WindowsDiagnosticsFailureStageEnvironmentVariable =
        "CLIPBOARD_TEST_WINDOWS_DIAGNOSTICS_FAILURE_STAGE";
    private const string WindowsDiagnosticsCommandLogEnvironmentVariable =
        "CLIPBOARD_TEST_WINDOWS_DIAGNOSTICS_COMMAND_LOG";

    [Fact]
    public void Release_ffi_configuration_uses_matching_native_library()
    {
        string project = ReadFixture("Clipboard.Windows.csproj");
        string toolchain = ReadFixture("verify-windows-client-toolchain.ps1");

        AssertFfiDllItem(
            project,
            @"..\..\target\debug\clipboard_ffi.dll",
            "Debug");
        AssertFfiDllItem(
            project,
            @"..\..\target\release\clipboard_ffi.dll",
            "Release");
        Assert.Contains("[ValidateSet('Debug', 'Release')]", toolchain);
        Assert.Contains("$Configuration.ToLowerInvariant()", toolchain);
    }

    [Fact]
    public void Performance_entrypoints_are_explicit_and_excluded_from_default_verification()
    {
        string historySearch = ReadFixture("measure-history-search.ps1");
        string windowsDiagnostics = ReadFixture("measure-windows-history-diagnostics.ps1");
        string verification = ReadFixture("verify-windows-client.ps1");

        Assert.Contains("history_search_performance", historySearch);
        Assert.Contains("--release", historySearch);
        Assert.Contains("--ignored", historySearch);
        Assert.Contains("--test-threads=1", historySearch);
        Assert.Contains("--nocapture", historySearch);
        Assert.DoesNotContain("verify-windows-client.ps1", historySearch);
        Assert.Contains("prepare_history_performance", windowsDiagnostics);
        Assert.Contains(
            "cargo build -p clipboard-core --release --bin prepare_history_performance",
            windowsDiagnostics);
        Assert.Contains(@"target\release\prepare_history_performance.exe", windowsDiagnostics);
        Assert.DoesNotContain("cargo run -p clipboard-core", windowsDiagnostics);
        Assert.Contains("Clipboard.Windows.Performance.csproj", windowsDiagnostics);
        Assert.Contains("-Configuration Release", windowsDiagnostics);
        Assert.DoesNotContain("--property Platform=x64", windowsDiagnostics);
        Assert.DoesNotContain("--property WindowsAppSDKSelfContained=true", windowsDiagnostics);
        Assert.DoesNotContain("-p:Platform=x64", windowsDiagnostics);
        Assert.DoesNotContain("measure-history-search.ps1", verification);
        Assert.DoesNotContain("measure-windows-history-diagnostics.ps1", verification);
        Assert.DoesNotContain("Clipboard.Windows.Performance", verification);
    }

    [Fact]
    public void Performance_scripts_preserve_child_exit_code_after_cleanup()
    {
        string historySearch = ReadFixture("measure-history-search.ps1");
        string windowsDiagnostics = ReadFixture("measure-windows-history-diagnostics.ps1");

        AssertPreservesChildExitCodeAfterCleanup(historySearch, "Pop-Location");
        AssertPreservesChildExitCodeAfterCleanup(windowsDiagnostics, "Remove-Item");
    }

    [Fact]
    public void Performance_scripts_capture_each_native_exit_code_immediately_without_resetting_it()
    {
        string historySearch = ReadFixture("measure-history-search.ps1");
        string windowsDiagnostics = ReadFixture("measure-windows-history-diagnostics.ps1");

        AssertNativeCommandImmediatelyCapturesExitCode(historySearch, "historyOutput");
        AssertNativeCommandImmediatelyCapturesExitCode(windowsDiagnostics, "ffiBuildOutput");
        AssertNativeCommandImmediatelyCapturesExitCode(windowsDiagnostics, "toolchainOutput");
        AssertNativeCommandImmediatelyCapturesExitCode(windowsDiagnostics, "fixtureBuildOutput");
        AssertNativeCommandImmediatelyCapturesExitCode(windowsDiagnostics, "fixtureOutput");
        AssertNativeCommandImmediatelyCapturesExitCode(windowsDiagnostics, "restoreOutput");
        AssertNativeCommandImmediatelyCapturesExitCode(windowsDiagnostics, "diagnosticOutput");
        AssertSingleExitCodeInitialization(historySearch);
        AssertSingleExitCodeInitialization(windowsDiagnostics);
    }

    [Fact]
    public void Performance_scripts_contain_cleanup_failures_without_overriding_child_exit_codes()
    {
        string historySearch = ReadFixture("measure-history-search.ps1");
        string windowsDiagnostics = ReadFixture("measure-windows-history-diagnostics.ps1");

        AssertContainsProtectedCleanup(historySearch, "Pop-Location");
        AssertContainsProtectedCleanup(windowsDiagnostics, "Remove-Item");
    }

    [Fact]
    public void Windows_diagnostics_suppresses_fixture_output()
    {
        string windowsDiagnostics = ReadFixture("measure-windows-history-diagnostics.ps1");

        Assert.Contains("$fixtureOutput = & $fixtureBinary --data-dir $dataDirectory *> $null", windowsDiagnostics);
    }

    [Fact]
    public void Performance_scripts_capture_child_output_before_reporting_safe_results()
    {
        string historySearch = ReadFixture("measure-history-search.ps1");
        string windowsDiagnostics = ReadFixture("measure-windows-history-diagnostics.ps1");

        Assert.Contains("$historyOutput = & cargo test", historySearch);
        Assert.Matches(@"\$historyOutput\s*=\s*&\s*cargo test[\s\S]*?2>&1", historySearch);
        Assert.Contains("Get-JsonObjectCandidates", historySearch);
        Assert.Contains("Write-HistorySearchMetrics", historySearch);
        Assert.DoesNotMatch(@"(?m)^\s*&\s+", historySearch);
        Assert.Contains("$diagnosticOutput = & dotnet run", windowsDiagnostics);
        Assert.Matches(@"\$diagnosticOutput\s*=\s*&\s*dotnet run[\s\S]*?2>&1", windowsDiagnostics);
        Assert.Contains("Get-JsonObjectCandidates", windowsDiagnostics);
        Assert.Contains("Write-DiagnosticReport", windowsDiagnostics);
        Assert.Contains("*> $null", windowsDiagnostics);
        Assert.DoesNotMatch(@"(?m)^\s*&\s+", windowsDiagnostics);
    }

    [Fact]
    public void Performance_scripts_strictly_validate_json_schema_before_reporting()
    {
        string historySearch = ReadFixture("measure-history-search.ps1");
        string windowsDiagnostics = ReadFixture("measure-windows-history-diagnostics.ps1");

        AssertHasStrictJsonPropertyValidation(historySearch, requireStrings: false);
        AssertHasStrictJsonPropertyValidation(windowsDiagnostics, requireStrings: true);
        Assert.Contains("$scenarioOrder -ccontains $scenario", historySearch);
        Assert.Matches(@"\$p95\s*-gt\s*\$threshold", historySearch);
        Assert.Contains("[0-9a-f]{8,40}", windowsDiagnostics);
        Assert.Contains("^\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?$", windowsDiagnostics);
        Assert.DoesNotContain("(?:[-+][A-Za-z0-9.-]+)?", windowsDiagnostics);
    }

    [Fact]
    public void History_search_metrics_reject_duplicate_json_properties()
    {
        int exitCode = InvokeFixtureFunction(
            "measure-history-search.ps1",
            "Write-HistorySearchMetrics",
            """
            {"scenario":"substring","samples":30,"samples":30,"results":100,"p50_ms":10,"p95_ms":20,"threshold_ms":200,"passed":true}
            """,
            CreateHistoryMetric("combined_filters"),
            CreateHistoryMetric("empty_query"));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void History_search_metrics_reject_duplicate_properties_nested_in_unknown_array()
    {
        string substringMetric = CreateHistoryMetric("substring");
        string metricWithDuplicateArrayProperty = substringMetric.Insert(
            substringMetric.Length - 1,
            ",\"diagnostic_extensions\":[{\"name\":\"first\",\"name\":\"second\"}]");

        int exitCode = InvokeFixtureFunction(
            "measure-history-search.ps1",
            "Write-HistorySearchMetrics",
            metricWithDuplicateArrayProperty,
            CreateHistoryMetric("combined_filters"),
            CreateHistoryMetric("empty_query"));

        Assert.NotEqual(0, exitCode);
    }

    [Theory]
    [InlineData("\"scenario\":\"substring\"", "\"scenario\":30")]
    [InlineData("\"samples\":30", "\"samples\":\"30\"")]
    [InlineData("\"passed\":true", "\"passed\":\"true\"")]
    public void History_search_metrics_reject_incorrect_json_value_types(
        string expectedValue,
        string incorrectValue)
    {
        string invalidMetric = CreateHistoryMetric("substring").Replace(
            expectedValue,
            incorrectValue,
            StringComparison.Ordinal);

        int exitCode = InvokeFixtureFunction(
            "measure-history-search.ps1",
            "Write-HistorySearchMetrics",
            invalidMetric,
            CreateHistoryMetric("combined_filters"),
            CreateHistoryMetric("empty_query"));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void History_search_metrics_reject_duplicate_valid_scenarios()
    {
        int exitCode = InvokeFixtureFunction(
            "measure-history-search.ps1",
            "Write-HistorySearchMetrics",
            CreateHistoryMetric("substring"),
            CreateHistoryMetric("substring"),
            CreateHistoryMetric("combined_filters"),
            CreateHistoryMetric("empty_query"));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void History_search_metrics_ignore_invalid_duplicate_scenarios()
    {
        string invalidDuplicate = CreateHistoryMetric("substring").Replace(
            "\"p95_ms\":20",
            "\"p95_ms\":201",
            StringComparison.Ordinal);

        int exitCode = InvokeFixtureFunction(
            "measure-history-search.ps1",
            "Write-HistorySearchMetrics",
            CreateHistoryMetric("substring"),
            invalidDuplicate,
            CreateHistoryMetric("combined_filters"),
            CreateHistoryMetric("empty_query"));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Windows_diagnostics_reject_duplicate_json_properties()
    {
        int exitCode = InvokeFixtureFunction(
            "measure-windows-history-diagnostics.ps1",
            "Write-DiagnosticReport",
            CreateDiagnosticReport(duplicateFfiSamples: true));

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Windows_diagnostics_reject_duplicate_properties_nested_in_unknown_array()
    {
        string report = CreateDiagnosticReport();
        string reportWithDuplicateArrayProperty = report.Insert(
            report.Length - 1,
            ",\"diagnostic_extensions\":[{\"name\":\"first\",\"name\":\"second\"}]");

        int exitCode = InvokeFixtureFunction(
            "measure-windows-history-diagnostics.ps1",
            "Write-DiagnosticReport",
            reportWithDuplicateArrayProperty);

        Assert.NotEqual(0, exitCode);
    }

    [Theory]
    [InlineData("\"git_revision\":\"1234567\"", "\"git_revision\":1234567")]
    [InlineData("\"logical_processor_count\":8", "\"logical_processor_count\":\"8\"")]
    [InlineData("\"warm_os_file_cache\":true", "\"warm_os_file_cache\":\"true\"")]
    public void Windows_diagnostics_reject_incorrect_json_value_types(
        string expectedValue,
        string incorrectValue)
    {
        string invalidReport = CreateDiagnosticReport().Replace(
            expectedValue,
            incorrectValue,
            StringComparison.Ordinal);

        int exitCode = InvokeFixtureFunction(
            "measure-windows-history-diagnostics.ps1",
            "Write-DiagnosticReport",
            invalidReport);

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Windows_diagnostics_reject_multiple_complete_reports()
    {
        int exitCode = InvokeFixtureFunction(
            "measure-windows-history-diagnostics.ps1",
            "Write-DiagnosticReport",
            CreateDiagnosticReport(),
            CreateDiagnosticReport());

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void Windows_diagnostics_accept_single_complete_report()
    {
        int exitCode = InvokeFixtureFunction(
            "measure-windows-history-diagnostics.ps1",
            "Write-DiagnosticReport",
            CreateDiagnosticReport());

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Performance_scripts_redact_native_command_failure_output_end_to_end()
    {
        using var directory = new TemporaryDirectory();
        string cargoPath = Path.Combine(directory.Path, "cargo.cmd");
        File.WriteAllText(
            cargoPath,
            "@echo off\r\n>&2 echo C:\\Users\\jdoe\\private-source.example\r\nexit /b 37\r\n",
            Encoding.ASCII);

        AssertRedactedNativeCommandFailure(
            ExecutePowerShellFile(GetFixturePath("measure-history-search.ps1"), directory.Path),
            "History search performance command failed.");
        AssertRedactedNativeCommandFailure(
            ExecutePowerShellFile(GetFixturePath("measure-windows-history-diagnostics.ps1"), directory.Path),
            "Windows history diagnostics command failed.");
    }

    [Fact]
    public void Windows_diagnostics_redact_later_native_command_failures_end_to_end()
    {
        using var directory = new TemporaryDirectory();
        string workspacePath = Path.Combine(directory.Path, "workspace");
        string commandDirectory = CreateWindowsDiagnosticsFailureWorkspace(workspacePath);
        string scriptPath = Path.Combine(
            workspacePath,
            "scripts",
            "measure-windows-history-diagnostics.ps1");
        string commandLogPath = Path.Combine(directory.Path, "native-command.log");
        string processPath = commandDirectory
            + Path.PathSeparator
            + Environment.GetEnvironmentVariable("PATH");

        foreach (string failureStage in GetLaterWindowsDiagnosticsFailureStages())
        {
            File.WriteAllText(commandLogPath, string.Empty, new UTF8Encoding(false));

            PowerShellExecutionResult result = ExecutePowerShellFile(
                scriptPath,
                null,
                new Dictionary<string, string?>
                {
                    [WindowsDiagnosticsFailureStageEnvironmentVariable] = failureStage,
                    [WindowsDiagnosticsCommandLogEnvironmentVariable] = commandLogPath,
                    ["PATH"] = processPath,
                });

            string[] observedLogEntries = File.Exists(commandLogPath)
                ? File.ReadAllLines(commandLogPath)
                : Array.Empty<string>();
            Assert.True(
                result.ExitCode == 37,
                $"Failure stage '{failureStage}' returned exit code {result.ExitCode}. "
                    + $"Command log: {string.Join(",", observedLogEntries)}. "
                    + $"Standard output length: {result.StandardOutput.Length}. "
                    + $"Standard error length: {result.StandardError.Length}.");
            AssertRedactedNativeCommandFailure(
                result,
                "Windows history diagnostics command failed.");
            Assert.Equal(
                GetExpectedWindowsDiagnosticsCommandOrder(failureStage),
                observedLogEntries);
        }
    }

    [Fact]
    public void Test_failure_diagnostics_do_not_embed_child_process_output()
    {
        string source = ReadCurrentTestSource();

        AssertFailureDiagnosticsIncludeOnlyChildOutputLengths(
            GetSection(
                source,
                "    public void Windows_diagnostics_redact_later_native_command_failures_end_to_end()",
                "    [Fact]\n    public void Windows_diagnostics_suppress_every_non_report_child_process_output()"));
        AssertFailureDiagnosticsIncludeOnlyChildOutputLengths(
            GetSection(
                source,
                "    private static void CompileWindowsDiagnosticsFixtureExecutableWithCsharpCompiler(",
                "    private static string GetWindowsCsharpCompilerPath()"));
        AssertFailureDiagnosticsIncludeOnlyChildOutputLengths(
            GetSection(
                source,
                "    private static void CompileCsharpExecutable(",
                "    private static string[] GetLaterWindowsDiagnosticsFailureStages()"));
    }

    [Fact]
    public void Get_section_supports_windows_line_endings()
    {
        string source = ReadCurrentTestSource();
        string sourceWithWindowsLineEndings = Regex.Replace(source, @"\r?\n", "\r\n");

        string section = GetSection(
            sourceWithWindowsLineEndings,
            "    public void Windows_diagnostics_redact_later_native_command_failures_end_to_end()",
            "    [Fact]\n    public void Windows_diagnostics_suppress_every_non_report_child_process_output()");

        Assert.Contains("AssertRedactedNativeCommandFailure", section);
    }

    [Fact]
    public void Windows_diagnostics_suppress_every_non_report_child_process_output()
    {
        string windowsDiagnostics = ReadFixture("measure-windows-history-diagnostics.ps1");

        Assert.Matches(
            @"\$ffiBuildOutput\s*=\s*&\s*cargo build -p clipboard-ffi --release\s*\*>\s*\$null",
            windowsDiagnostics);
        Assert.Matches(
            @"\$toolchainOutput\s*=\s*&\s*pwsh[\s\S]*?-Configuration Release\s*\*>\s*\$null",
            windowsDiagnostics);
        Assert.Matches(
            @"\$fixtureBuildOutput\s*=\s*&\s*cargo build -p clipboard-core --release --bin prepare_history_performance\s*\*>\s*\$null",
            windowsDiagnostics);
        Assert.Matches(
            @"\$fixtureOutput\s*=\s*&\s*\$fixtureBinary --data-dir \$dataDirectory\s*\*>\s*\$null",
            windowsDiagnostics);
        Assert.Matches(
            @"\$restoreOutput\s*=\s*&\s*dotnet restore[\s\S]*?\s\*>\s*\$null",
            windowsDiagnostics);
    }

    [Fact]
    public void View_model_diagnostics_complete_from_property_changed_without_polling()
    {
        string program = ReadFixture("PerformanceProgram.cs");

        Assert.Contains("PropertyChangedEventHandler", program);
        Assert.Contains("new TaskCompletionSource", program);
        Assert.Contains("TrySetResult", program);
        Assert.False(
            Regex.IsMatch(program, @"Task\.Delay\s*\(\s*5\b"),
            "ViewModel diagnostics must not poll with Task.Delay(5).");
    }

    [Fact]
    public void View_model_diagnostics_cancel_active_search_before_releasing_the_client()
    {
        string program = ReadFixture("PerformanceProgram.cs");

        Assert.Contains("CancellationTokenSource searchCancellation", program);
        Assert.Contains("core.CancelSearches();", program);
        Assert.Contains("AbandonDiagnosticCore(core);", program);
        Assert.Contains("await triggerTask.WaitAsync", program);
        Assert.Contains("viewModel.ErrorMessage", program);
        Assert.Contains("Task.Run(", program);
        Assert.Contains("WaitForSearchesAsync", program);
        Assert.Contains("core.Dispose();", program);
        Assert.DoesNotContain("using ClipboardCoreClient viewModelClient", program);
    }

    [Fact]
    public void Ffi_diagnostics_use_the_timeout_safe_adapter_lifecycle()
    {
        string program = ReadFixture("PerformanceProgram.cs");
        string warmUp = GetSection(
            program,
            "private static async Task WarmUpAsync",
            "private static async Task<SearchMetric> MeasureFfiAsync");
        string ffiMeasurement = GetSection(
            program,
            "private static async Task<SearchMetric> MeasureFfiAsync",
            "private static async Task<SearchMetric> MeasureViewModelSubstringAsync");
        string ffiSearch = GetSection(
            program,
            "private static async Task<int> ExecuteFfiSearchAsync",
            "private static async Task<int> TriggerAndWaitForSearchAsync");

        Assert.Contains("OpenDiagnosticCore(arguments)", warmUp);
        Assert.Contains("ExecuteFfiSearchAsync", warmUp);
        Assert.DoesNotContain("ClipboardCoreClient client", warmUp);
        Assert.Contains("MeasureAsync(", ffiMeasurement);
        Assert.Contains("ExecuteFfiSampleAsync(arguments, requestFactory())", ffiMeasurement);
        Assert.DoesNotContain("ClipboardCoreClient client", ffiMeasurement);
        Assert.Contains("Func<Task<int>> execute", program);
        Assert.Contains("await searchTask.WaitAsync", ffiSearch);
        Assert.Contains("CancelSearchesAndWaitAsync(core, searchTask)", ffiSearch);
        Assert.Contains("DiagnosticMeasurementScope", program);
        Assert.Contains("scope.RegisterCleanup", program);
        Assert.DoesNotContain("OpenDiagnosticCores(", program);
        Assert.DoesNotContain("OpenViewModelSamples(", program);
        Assert.Contains("core.CancelSearches();", program);
        Assert.Contains("AbandonDiagnosticCore(core);", program);
    }

    [Fact]
    public void Diagnostic_report_includes_cold_search_objects_flag()
    {
        string program = ReadFixture("PerformanceProgram.cs");

        Assert.Contains("bool ColdSearchObjects", program);
        Assert.Contains("ColdSearchObjects:", program);
        Assert.Contains("PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower", program);
    }

    [Fact]
    public void Diagnostic_failure_output_includes_a_sanitized_cause()
    {
        string program = ReadFixture("PerformanceProgram.cs");

        Assert.Contains("DescribeFailure", program);
        Assert.Contains("ClipboardCoreException core", program);
        Assert.Contains("core.Status", program);
        Assert.Contains("DiagnosticCoreAdapter", program);
        Assert.Contains("LastSearchException", program);
        Assert.Contains("private Exception? _lastSearchException", program);
        Assert.Contains("core.LastSearchException is Exception searchException", program);
        Assert.Contains(
            "catch (Exception exception) when (exception is not OperationCanceledException)",
            program);
        Assert.DoesNotContain("exception.Message", program);
    }

    [Fact]
    public void Diagnostic_timeout_output_distinguishes_ffi_from_view_model()
    {
        string program = ReadFixture("PerformanceProgram.cs");

        Assert.Contains("FfiSearchTimeoutException", program);
        Assert.Contains("ViewModelSearchTimeoutException", program);
        Assert.Contains("FfiSearchTimeoutException => \"FFI search timed out.\"", program);
        Assert.Contains(
            "ViewModelSearchTimeoutException => \"ViewModel search timed out.\"",
            program);
    }

    [Fact]
    public void Image_lru_uses_a_complete_cloned_one_pixel_png_template()
    {
        string program = ReadFixture("PerformanceProgram.cs");

        Assert.Contains("private static readonly byte[] OnePixelPng", program);
        Assert.Contains("0x89, 0x50, 0x4e, 0x47", program);
        Assert.Contains("0x49, 0x44, 0x41, 0x54", program);
        Assert.Contains("0x49, 0x45, 0x4e, 0x44", program);
        Assert.Contains("0xae, 0x42, 0x60, 0x82", program);
        Assert.Contains("return OnePixelPng.ToArray();", program);
        Assert.DoesNotContain("bytes[^1] =", program);
    }

    [Fact]
    public void Performance_project_is_an_x64_release_diagnostics_executable_with_ffi_mapping()
    {
        string project = ReadFixture("Clipboard.Windows.Performance.csproj");

        Assert.Contains("<OutputType>Exe</OutputType>", project);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", project);
        Assert.Contains("<PlatformTarget>x64</PlatformTarget>", project);
        AssertFfiDllItem(
            project,
            @"..\..\target\debug\clipboard_ffi.dll",
            "Debug");
        AssertFfiDllItem(
            project,
            @"..\..\target\release\clipboard_ffi.dll",
            "Release");
    }

    private static void AssertPreservesChildExitCodeAfterCleanup(
        string script,
        string cleanupCommand)
    {
        Match exitCodeCapture = Regex.Match(
            script,
            @"(?<variable>\$[A-Za-z_][A-Za-z0-9_]*)\s*=\s*\$LASTEXITCODE",
            RegexOptions.IgnoreCase);

        Assert.True(
            exitCodeCapture.Success,
            "The script must capture the original $LASTEXITCODE before cleanup.");
        Assert.False(
            script.Contains("throw", StringComparison.OrdinalIgnoreCase),
            "The script must not replace a child process exit code with a generic throw failure.");

        int cleanupIndex = script.LastIndexOf(cleanupCommand, StringComparison.OrdinalIgnoreCase);
        int exitIndex = script.LastIndexOf(
            $"exit {exitCodeCapture.Groups["variable"].Value}",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(cleanupIndex >= 0, $"Expected cleanup command was not found: {cleanupCommand}");
        Assert.True(
            exitIndex > cleanupIndex,
            "The script must exit with the captured child process exit code after cleanup.");
    }

    private static void AssertContainsProtectedCleanup(string script, string cleanupCommand)
    {
        int cleanupIndex = script.LastIndexOf(cleanupCommand, StringComparison.OrdinalIgnoreCase);
        int precedingTryIndex = script.LastIndexOf("try", cleanupIndex, StringComparison.OrdinalIgnoreCase);
        int followingCatchIndex = script.IndexOf("catch", cleanupIndex, StringComparison.OrdinalIgnoreCase);

        Assert.True(precedingTryIndex >= 0, $"{cleanupCommand} must be enclosed in a cleanup try block.");
        Assert.True(followingCatchIndex > cleanupIndex, $"{cleanupCommand} must have a cleanup catch block.");
        Assert.Contains("$cleanupFailed = $true", script);
        Assert.Contains("[Console]::Error.WriteLine", script);
    }

    private static void AssertNativeCommandImmediatelyCapturesExitCode(
        string script,
        string outputVariable)
    {
        string pattern =
            $@"(?m)^\s*\${Regex.Escape(outputVariable)}\s*=\s*&[^\r\n]*\r?\n"
            + @"\s*\$exitCode\s*=\s*\$LASTEXITCODE\s*$";

        Assert.Matches(
            pattern,
            script);
    }

    private static void AssertSingleExitCodeInitialization(string script)
    {
        int initializationCount = Regex.Matches(
            script,
            @"(?im)^\s*\$exitCode\s*=\s*0\b").Count;

        Assert.Equal(
            1,
            initializationCount);
    }

    private static void AssertFfiDllItem(
        string project,
        string include,
        string configuration)
    {
        string pattern =
            $"<None\\s+Include=\"{Regex.Escape(include)}\"\\s+"
            + "Link=\"clipboard_ffi\\.dll\"\\s+"
            + "CopyToOutputDirectory=\"PreserveNewest\"\\s+"
            + $"Condition=\"'\\$\\(Configuration\\)' == '{Regex.Escape(configuration)}'\"\\s*/>";

        Assert.Matches(pattern, project);
    }

    private static void AssertHasStrictJsonPropertyValidation(string script, bool requireStrings)
    {
        Assert.Matches(
            @"function\s+Get-RequiredInt64[\s\S]*?PSObject\.Properties\[[\s\S]*?-isnot\s+\[long\]",
            script);
        Assert.Matches(
            @"function\s+Get-RequiredBoolean[\s\S]*?PSObject\.Properties\[[\s\S]*?-isnot\s+\[bool\]",
            script);

        if (requireStrings)
        {
            Assert.Matches(
                @"function\s+Get-RequiredString[\s\S]*?PSObject\.Properties\[[\s\S]*?-isnot\s+\[string\]",
                script);
            }
    }

    private static int InvokeFixtureFunction(
        string fixtureName,
        string functionName,
        params string[] output)
    {
        using var directory = new TemporaryDirectory();
        string fixture = ReadFixture(fixtureName);
        const string mainScriptMarker = "$repositoryRoot = $null";
        int markerIndex = fixture.IndexOf(mainScriptMarker, StringComparison.Ordinal);
        Assert.True(markerIndex > 0, $"Expected script entrypoint was not found: {fixtureName}");

        string definitionsPath = Path.Combine(directory.Path, "definitions.ps1");
        File.WriteAllText(definitionsPath, fixture[..markerIndex], new UTF8Encoding(false));

        string serializedOutput = JsonSerializer.Serialize(output);
        string encodedOutput = Convert.ToBase64String(Encoding.UTF8.GetBytes(serializedOutput));
        string invocationPath = Path.Combine(directory.Path, "invoke.ps1");
        File.WriteAllText(
            invocationPath,
            "$ErrorActionPreference = 'Stop'\n"
                + ". '"
                + EscapePowerShellSingleQuoted(definitionsPath)
                + "'\n"
                + "$serializedOutput = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('"
                + encodedOutput
                + "'))\n"
                + "$output = @($serializedOutput | ConvertFrom-Json -ErrorAction Stop)\n"
                + "if ("
                + functionName
                + " -Output $output) { exit 0 }\n"
                + "exit 1\n",
            new UTF8Encoding(false));

        return ExecutePowerShellFile(invocationPath).ExitCode;
    }

    private static string CreateHistoryMetric(string scenario)
    {
        return scenario switch
        {
            "substring" =>
                "{\"scenario\":\"substring\",\"samples\":30,\"results\":100,\"p50_ms\":10,\"p95_ms\":20,\"threshold_ms\":200,\"passed\":true}",
            "combined_filters" =>
                "{\"scenario\":\"combined_filters\",\"samples\":30,\"results\":10,\"p50_ms\":10,\"p95_ms\":20,\"threshold_ms\":200,\"passed\":true}",
            "empty_query" =>
                "{\"scenario\":\"empty_query\",\"samples\":3,\"results\":10000,\"p50_ms\":10,\"p95_ms\":20,\"threshold_ms\":null,\"passed\":true}",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown history scenario."),
        };
    }

    private static string CreateDiagnosticReport(bool duplicateFfiSamples = false)
    {
        string ffiSubstring = duplicateFfiSamples
            ? "{\"samples\":30,\"samples\":30,\"results\":100,\"p50_ms\":10,\"p95_ms\":20}"
            : "{\"samples\":30,\"results\":100,\"p50_ms\":10,\"p95_ms\":20}";

        return "{"
            + "\"git_revision\":\"1234567\""
            + ",\"windows_version\":\"10.0.22621.0\""
            + ",\"dotnet_version\":\"8.0.0\""
            + ",\"rust_version\":\"rustc 1.80.0 (12345678 2024-01-01)\""
            + ",\"logical_processor_count\":8"
            + ",\"physical_memory_bytes\":17179869184"
            + ",\"configuration\":\"Release\""
            + ",\"warm_os_file_cache\":true"
            + ",\"cold_search_objects\":true"
            + ",\"ffi_substring\":"
            + ffiSubstring
            + ",\"ffi_combined\":{\"samples\":30,\"results\":10,\"p50_ms\":10,\"p95_ms\":20}"
            + ",\"view_model_substring\":{\"samples\":30,\"results\":100,\"p50_ms\":10,\"p95_ms\":20}"
            + ",\"view_model_combined\":{\"samples\":30,\"results\":10,\"p50_ms\":10,\"p95_ms\":20}"
            + ",\"empty_query\":{\"samples\":3,\"results\":10000,\"p50_ms\":10,\"p95_ms\":20}"
            + ",\"image_lru\":{\"capacity\":64,\"entries\":64,\"oldest_evicted\":true,\"newest_available\":true,\"working_set_before_bytes\":1,\"working_set_after_bytes\":1}"
            + ",\"peak_working_set_bytes\":1"
            + ",\"managed_heap_bytes\":1"
            + "}";
    }

    private static void AssertRedactedNativeCommandFailure(
        PowerShellExecutionResult result,
        string expectedFailure)
    {
        Assert.Equal(37, result.ExitCode);

        string combinedOutput = result.StandardOutput + Environment.NewLine + result.StandardError;
        Assert.False(
            combinedOutput.Contains("jdoe", StringComparison.OrdinalIgnoreCase),
            "Native command output must not disclose a username.");
        Assert.False(
            combinedOutput.Contains("private-source", StringComparison.OrdinalIgnoreCase),
            "Native command output must not disclose a private source host.");

        string[] lines = combinedOutput.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(lines);
        Assert.Equal(expectedFailure, lines[0]);
    }

    private static string CreateWindowsDiagnosticsFailureWorkspace(string workspacePath)
    {
        string scriptsDirectory = Path.Combine(workspacePath, "scripts");
        string performanceDirectory = Path.Combine(
            workspacePath,
            "tests",
            "Clipboard.Windows.Performance");
        string fixtureOutputDirectory = Path.Combine(workspacePath, "target", "release");
        string commandDirectory = Path.Combine(workspacePath, "test-commands");

        Directory.CreateDirectory(scriptsDirectory);
        Directory.CreateDirectory(performanceDirectory);
        Directory.CreateDirectory(fixtureOutputDirectory);
        Directory.CreateDirectory(commandDirectory);
        File.WriteAllText(
            Path.Combine(workspacePath, "Cargo.toml"),
            "[workspace]\nresolver = \"2\"\n",
            new UTF8Encoding(false));
        File.Copy(
            GetFixturePath("measure-windows-history-diagnostics.ps1"),
            Path.Combine(scriptsDirectory, "measure-windows-history-diagnostics.ps1"));
        File.WriteAllText(
            Path.Combine(scriptsDirectory, "verify-windows-client-toolchain.ps1"),
            $$"""
            $commandLogPath = [Environment]::GetEnvironmentVariable('{{WindowsDiagnosticsCommandLogEnvironmentVariable}}')
            if (-not [string]::IsNullOrWhiteSpace($commandLogPath)) {
                [IO.File]::AppendAllText($commandLogPath, "toolchain`n")
            }

            if ([string]::Equals(
                [Environment]::GetEnvironmentVariable('{{WindowsDiagnosticsFailureStageEnvironmentVariable}}'),
                'toolchain',
                [StringComparison]::Ordinal)) {
                [Console]::Error.WriteLine('C:\Users\jdoe\private-source.example')
                exit 37
            }

            exit 0
            """,
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(performanceDirectory, "Clipboard.Windows.Performance.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """,
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(performanceDirectory, "Program.cs"),
            $$"""
            using System;
            using System.IO;

            public static class Program
            {
                public static int Main(string[] args)
                {
                    string commandLogPath = Environment.GetEnvironmentVariable("{{WindowsDiagnosticsCommandLogEnvironmentVariable}}");
                    if (!string.IsNullOrWhiteSpace(commandLogPath))
                    {
                        File.AppendAllText(commandLogPath, "fixture" + Environment.NewLine);
                    }

                    if (string.Equals(
                        Environment.GetEnvironmentVariable("{{WindowsDiagnosticsFailureStageEnvironmentVariable}}"),
                        "fixture",
                        StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine(@"C:\Users\jdoe\private-source.example");
                        return 37;
                    }

                    return 0;
                }
            }
            """,
            new UTF8Encoding(false));

        CompileWindowsDiagnosticsFixtureExecutableWithCsharpCompiler(
            performanceDirectory,
            fixtureOutputDirectory);
        CompileWindowsDiagnosticsFailureCommandExecutables(commandDirectory);
        return commandDirectory;
    }

    private static void CompileWindowsDiagnosticsFixtureExecutableWithCsharpCompiler(
        string performanceDirectory,
        string fixtureOutputDirectory)
    {
        string sourcePath = Path.Combine(performanceDirectory, "Program.cs");
        string fixtureExecutablePath = Path.Combine(
            fixtureOutputDirectory,
            "prepare_history_performance.exe");
        var startInfo = new ProcessStartInfo(GetWindowsCsharpCompilerPath())
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = performanceDirectory,
        };
        startInfo.ArgumentList.Add("/nologo");
        startInfo.ArgumentList.Add("/target:exe");
        startInfo.ArgumentList.Add("/out:" + fixtureExecutablePath);
        startInfo.ArgumentList.Add(sourcePath);

        PowerShellExecutionResult result = ExecuteProcess(startInfo);
        Assert.True(
            result.ExitCode == 0,
            "The minimal diagnostics fixture executable must compile with the Windows C# compiler successfully. "
                + $"Standard output length: {result.StandardOutput.Length}. "
                + $"Standard error length: {result.StandardError.Length}.");
        Assert.True(
            File.Exists(fixtureExecutablePath),
            "The minimal diagnostics fixture executable was not produced.");
    }

    private static string GetWindowsCsharpCompilerPath()
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string framework64Compiler = Path.Combine(
            windowsDirectory,
            "Microsoft.NET",
            "Framework64",
            "v4.0.30319",
            "csc.exe");
        if (File.Exists(framework64Compiler))
        {
            return framework64Compiler;
        }

        string frameworkCompiler = Path.Combine(
            windowsDirectory,
            "Microsoft.NET",
            "Framework",
            "v4.0.30319",
            "csc.exe");
        Assert.True(
            File.Exists(frameworkCompiler),
            "The Windows .NET Framework C# compiler must be available for the fixture executable.");
        return frameworkCompiler;
    }

    private static void CompileWindowsDiagnosticsFailureCommandExecutables(string commandDirectory)
    {
        string sourcePath = Path.Combine(commandDirectory, "FakeNativeCommand.cs");
        string source =
            "using System;\r\n"
            + "using System.Diagnostics;\r\n"
            + "using System.IO;\r\n"
            + "\r\n"
            + "public static class FakeNativeCommand\r\n"
            + "{\r\n"
            + "    public static int Main(string[] arguments)\r\n"
            + "    {\r\n"
            + "        string executableName = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);\r\n"
            + "        string step = null;\r\n"
            + "        if (string.Equals(executableName, \"cargo\", StringComparison.OrdinalIgnoreCase))\r\n"
            + "        {\r\n"
            + "            if (Contains(arguments, \"clipboard-ffi\")) step = \"ffi-build\";\r\n"
            + "            else if (Contains(arguments, \"prepare_history_performance\")) step = \"fixture-build\";\r\n"
            + "        }\r\n"
            + "        else if (string.Equals(executableName, \"dotnet\", StringComparison.OrdinalIgnoreCase) && arguments.Length > 0)\r\n"
            + "        {\r\n"
            + "            step = arguments[0];\r\n"
            + "        }\r\n"
            + "\r\n"
            + "        if (string.IsNullOrEmpty(step)) return 89;\r\n"
            + "        string commandLogPath = Environment.GetEnvironmentVariable(\""
            + WindowsDiagnosticsCommandLogEnvironmentVariable
            + "\");\r\n"
            + "        if (string.IsNullOrEmpty(commandLogPath)) return 89;\r\n"
            + "        File.AppendAllText(commandLogPath, step + Environment.NewLine);\r\n"
            + "        if (string.Equals(Environment.GetEnvironmentVariable(\""
            + WindowsDiagnosticsFailureStageEnvironmentVariable
            + "\"), step, StringComparison.Ordinal))\r\n"
            + "        {\r\n"
            + "            Console.Error.WriteLine(@\"C:\\Users\\jdoe\\private-source.example\");\r\n"
            + "            return 37;\r\n"
            + "        }\r\n"
            + "\r\n"
            + "        return 0;\r\n"
            + "    }\r\n"
            + "\r\n"
            + "    private static bool Contains(string[] arguments, string value)\r\n"
            + "    {\r\n"
            + "        foreach (string argument in arguments)\r\n"
            + "        {\r\n"
            + "            if (string.Equals(argument, value, StringComparison.Ordinal)) return true;\r\n"
            + "        }\r\n"
            + "\r\n"
            + "        return false;\r\n"
            + "    }\r\n"
            + "}\r\n";
        File.WriteAllText(sourcePath, source, new UTF8Encoding(false));

        CompileCsharpExecutable(
            sourcePath,
            Path.Combine(commandDirectory, "cargo.exe"),
            commandDirectory);
        CompileCsharpExecutable(
            sourcePath,
            Path.Combine(commandDirectory, "dotnet.exe"),
            commandDirectory);
    }

    private static void CompileCsharpExecutable(
        string sourcePath,
        string outputPath,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(GetWindowsCsharpCompilerPath())
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        startInfo.ArgumentList.Add("/nologo");
        startInfo.ArgumentList.Add("/target:exe");
        startInfo.ArgumentList.Add("/out:" + outputPath);
        startInfo.ArgumentList.Add(sourcePath);

        PowerShellExecutionResult result = ExecuteProcess(startInfo);
        Assert.True(
            result.ExitCode == 0,
            "The native command fixture executable must compile successfully. "
                + $"Standard output length: {result.StandardOutput.Length}. "
                + $"Standard error length: {result.StandardError.Length}.");
    }

    private static string[] GetLaterWindowsDiagnosticsFailureStages()
    {
        return new[] { "toolchain", "fixture-build", "fixture", "restore", "run" };
    }

    private static string[] GetExpectedWindowsDiagnosticsCommandOrder(string failureStage)
    {
        return failureStage switch
        {
            "toolchain" => new[] { "ffi-build", "toolchain" },
            "fixture-build" => new[] { "ffi-build", "toolchain", "fixture-build" },
            "fixture" => new[] { "ffi-build", "toolchain", "fixture-build", "fixture" },
            "restore" => new[] { "ffi-build", "toolchain", "fixture-build", "fixture", "restore" },
            "run" => new[] { "ffi-build", "toolchain", "fixture-build", "fixture", "restore", "run" },
            _ => throw new ArgumentOutOfRangeException(nameof(failureStage), failureStage, "Unknown failure stage."),
        };
    }

    private static PowerShellExecutionResult ExecutePowerShellFile(
        string scriptPath,
        string? pathPrefix = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        if (pathPrefix is not null)
        {
            string pathKey = startInfo.Environment.Keys.FirstOrDefault(
                    key => string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase))
                ?? "PATH";
            string existingPath = startInfo.Environment.TryGetValue(pathKey, out string? value)
                ? value ?? string.Empty
                : string.Empty;
            startInfo.Environment[pathKey] = pathPrefix + Path.PathSeparator + existingPath;
        }

        if (environment is not null)
        {
            foreach (KeyValuePair<string, string?> variable in environment)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        return ExecuteProcess(startInfo);
    }

    private static PowerShellExecutionResult ExecuteProcess(ProcessStartInfo startInfo)
    {
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell process could not be started.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new PowerShellExecutionResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string GetFixturePath(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        Assert.True(File.Exists(path), $"Expected test fixture was not found: {fileName}");
        return path;
    }

    private sealed record PowerShellExecutionResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"clipboard-performance-entrypoint-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private static string GetSection(string text, string startMarker, string endMarker)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected section start was not found: {startMarker}");

        int end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected section end was not found: {endMarker}");
        return text[start..end];
    }

    private static void AssertFailureDiagnosticsIncludeOnlyChildOutputLengths(string section)
    {
        Assert.DoesNotMatch(
            @"result\.StandardOutput(?!\.Length)",
            section);
        Assert.DoesNotMatch(
            @"result\.StandardError(?!\.Length)",
            section);
    }

    private static string ReadCurrentTestSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string sourcePath = Path.Combine(
                directory.FullName,
                "tests",
                "Clipboard.Windows.Tests",
                "Platform",
                "PerformanceEntrypointConfigurationTests.cs");
            if (File.Exists(sourcePath))
            {
                return File.ReadAllText(sourcePath);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "The PerformanceEntrypointConfigurationTests source file was not found.");
    }

    private static string ReadFixture(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        Assert.True(File.Exists(path), $"Expected test fixture was not found: {fileName}");
        return File.ReadAllText(path);
    }
}
