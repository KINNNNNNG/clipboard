using System.Text.RegularExpressions;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class PerformanceEntrypointConfigurationTests
{
    [Fact]
    public void Release_ffi_configuration_uses_matching_native_library()
    {
        string project = ReadFixture("Clipboard.Windows.csproj");
        string toolchain = ReadFixture("verify-windows-client-toolchain.ps1");

        Assert.Contains(@"..\..\target\debug\clipboard_ffi.dll", project);
        Assert.Contains(@"..\..\target\release\clipboard_ffi.dll", project);
        Assert.Contains("Condition=\"'$(Configuration)' == 'Debug'\"", project);
        Assert.Contains("Condition=\"'$(Configuration)' == 'Release'\"", project);
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
    public void Performance_scripts_contain_cleanup_failures_without_overriding_child_exit_codes()
    {
        string historySearch = ReadFixture("measure-history-search.ps1");
        string windowsDiagnostics = ReadFixture("measure-windows-history-diagnostics.ps1");

        AssertContainsProtectedCleanup(historySearch, "Pop-Location");
        AssertContainsProtectedCleanup(windowsDiagnostics, "Remove-Item");
    }

    [Fact]
    public void Windows_diagnostics_suppresses_fixture_success_output()
    {
        string windowsDiagnostics = ReadFixture("measure-windows-history-diagnostics.ps1");

        Assert.Contains("& $fixtureBinary --data-dir $dataDirectory | Out-Null", windowsDiagnostics);
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
        Assert.Contains(@"..\..\target\debug\clipboard_ffi.dll", project);
        Assert.Contains(@"..\..\target\release\clipboard_ffi.dll", project);
        Assert.Contains("Condition=\"'$(Configuration)' == 'Debug'\"", project);
        Assert.Contains("Condition=\"'$(Configuration)' == 'Release'\"", project);
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

    private static string GetSection(string text, string startMarker, string endMarker)
    {
        int start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected section start was not found: {startMarker}");

        int end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected section end was not found: {endMarker}");
        return text[start..end];
    }

    private static string ReadFixture(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        Assert.True(File.Exists(path), $"Expected test fixture was not found: {fileName}");
        return File.ReadAllText(path);
    }
}
