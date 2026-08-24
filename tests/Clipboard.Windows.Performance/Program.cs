using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;
using Clipboard.Windows.ViewModels;
using Clipboard.Windows.Views;

namespace Clipboard.Windows.Performance;

internal static class Program
{
    private const int PerformanceSamples = 30;
    private const int EmptyQuerySamples = 3;
    private const int ImageCacheCapacity = 64;
    private const int SearchTimeoutMilliseconds = 10_000;
    private const int SearchCancellationGraceMilliseconds = 250;
    private static readonly byte[] FixtureKey = Enumerable.Repeat((byte)0x6a, 32).ToArray();
    private static readonly ConcurrentBag<DiagnosticCoreAdapter> AbandonedDiagnosticCores = [];
    private static readonly AsyncLocal<DiagnosticMeasurementScope?> CurrentMeasurementScope = new();
    private static readonly byte[] OnePixelPng =
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
        0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4, 0x89,
        0x00, 0x00, 0x00, 0x0d, 0x49, 0x44, 0x41, 0x54,
        0x08, 0x1d, 0x63, 0xf8, 0xcf, 0xc0, 0xf0, 0x1f,
        0x00, 0x05, 0xfe, 0x02, 0xfe, 0xcc, 0x1c, 0xed, 0xa6,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44,
        0xae, 0x42, 0x60, 0x82,
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            DiagnosticArguments arguments = ParseArguments(args);
            await WarmUpAsync(arguments);

            SearchMetric ffiSubstring = await MeasureFfiAsync(
                arguments,
                CreateSubstringRequest,
                PerformanceSamples,
                expectedResults: 100);
            SearchMetric ffiCombined = await MeasureFfiAsync(
                arguments,
                CreateCombinedRequest,
                PerformanceSamples,
                expectedResults: 10);
            SearchMetric viewModelSubstring = await MeasureViewModelSubstringAsync(
                arguments,
                PerformanceSamples);
            SearchMetric viewModelCombined = await MeasureViewModelCombinedAsync(
                arguments,
                PerformanceSamples);
            SearchMetric emptyQuery = await MeasureEmptyQueryAsync(arguments, EmptyQuerySamples);
            ImageLruMetric imageLru = MeasureImageLru();

            using Process process = Process.GetCurrentProcess();
            var report = new DiagnosticReport(
                GetCommandVersion("git", "rev-parse", "HEAD"),
                Environment.OSVersion.Version.ToString(),
                Environment.Version.ToString(),
                GetCommandVersion("rustc", "--version"),
                Environment.ProcessorCount,
                GetPhysicalMemoryBytes(),
                arguments.Configuration,
                WarmOsFileCache: true,
                ColdSearchObjects: true,
                ffiSubstring,
                ffiCombined,
                viewModelSubstring,
                viewModelCombined,
                emptyQuery,
                imageLru,
                process.PeakWorkingSet64,
                GC.GetGCMemoryInfo().HeapSizeBytes);

            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Windows history diagnostics failed: {DescribeFailure(exception)}");
            return 1;
        }
    }

    private static DiagnosticArguments ParseArguments(IReadOnlyList<string> args)
    {
        if (args.Count != 6
            || !string.Equals(args[0], "--data-dir", StringComparison.Ordinal)
            || !string.Equals(args[2], "--vault-id", StringComparison.Ordinal)
            || !string.Equals(args[4], "--configuration", StringComparison.Ordinal)
            || !Guid.TryParse(args[3], out Guid vaultId)
            || !string.Equals(args[5], "Release", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Expected --data-dir <path> --vault-id <guid> --configuration Release.");
        }

        if (string.IsNullOrWhiteSpace(args[1]) || !Directory.Exists(args[1]))
        {
            throw new ArgumentException("The diagnostic data directory is unavailable.");
        }

        return new DiagnosticArguments(args[1], vaultId, args[5]);
    }

    private static async Task WarmUpAsync(DiagnosticArguments arguments)
    {
        DiagnosticCoreAdapter ffiCore = OpenDiagnosticCore(arguments);
        try
        {
            int resultCount = await ExecuteFfiSearchAsync(ffiCore, CreateSubstringRequest());
            AssertResultCount(resultCount, expectedResults: 100);
        }
        finally
        {
            DisposeDiagnosticCore(ffiCore);
        }

        DiagnosticCoreAdapter core = OpenDiagnosticCore(arguments);
        try
        {
            var viewModel = new ClipboardPanelViewModel(core, new DisabledPasteService());
            int resultCount = await TriggerAndWaitForSearchAsync(
                viewModel,
                core,
                () =>
                {
                    viewModel.QueryText = "performance-needle";
                    return Task.CompletedTask;
                });
            AssertResultCount(resultCount, expectedResults: 100);
        }
        finally
        {
            DisposeDiagnosticCore(core);
        }
    }

    private static async Task<SearchMetric> MeasureFfiAsync(
        DiagnosticArguments arguments,
        Func<SearchRequestDto> requestFactory,
        int sampleCount,
        int expectedResults)
    {
        return await MeasureAsync(
            sampleCount,
            expectedResults,
            () => ExecuteFfiSampleAsync(arguments, requestFactory()));
    }

    private static async Task<SearchMetric> MeasureViewModelSubstringAsync(
        DiagnosticArguments arguments,
        int sampleCount)
    {
        return await MeasureAsync(
            sampleCount,
            expectedResults: 100,
            () => ExecuteViewModelSubstringSampleAsync(arguments));
    }

    private static async Task<SearchMetric> MeasureViewModelCombinedAsync(
        DiagnosticArguments arguments,
        int sampleCount)
    {
        return await MeasureAsync(
            sampleCount,
            expectedResults: 10,
            () => ExecuteViewModelCombinedSampleAsync(arguments));
    }

    private static async Task<SearchMetric> MeasureEmptyQueryAsync(
        DiagnosticArguments arguments,
        int sampleCount)
    {
        return await MeasureAsync(
            sampleCount,
            expectedResults: 10_000,
            () => ExecuteEmptyQuerySampleAsync(arguments));
    }

    private static Task<int> ExecuteFfiSampleAsync(
        DiagnosticArguments arguments,
        SearchRequestDto request)
    {
        DiagnosticMeasurementScope scope = RequireMeasurementScope();
        DiagnosticCoreAdapter core = OpenDiagnosticCore(arguments);
        scope.RegisterCleanup(() => DisposeDiagnosticCore(core));
        scope.MarkReady();
        return scope.ExecuteAfterStartAsync(() => ExecuteFfiSearchAsync(core, request));
    }

    private static Task<int> ExecuteViewModelSubstringSampleAsync(
        DiagnosticArguments arguments) =>
        ExecuteViewModelSampleAsync(
            arguments,
            viewModel =>
            {
                viewModel.QueryText = "performance-needle";
                return Task.CompletedTask;
            });

    private static async Task<int> ExecuteViewModelCombinedSampleAsync(
        DiagnosticArguments arguments)
    {
        DiagnosticMeasurementScope scope = RequireMeasurementScope();
        DiagnosticCoreAdapter core;
        ClipboardPanelViewModel viewModel;
        try
        {
            core = OpenDiagnosticCore(arguments);
            scope.RegisterCleanup(() => DisposeDiagnosticCore(core));
            viewModel = new ClipboardPanelViewModel(core, new DisabledPasteService());
            int warmUpResultCount = await TriggerAndWaitForSearchAsync(
                viewModel,
                core,
                () =>
                {
                    viewModel.QueryText = "performance-needle";
                    return Task.CompletedTask;
                });
            AssertResultCount(warmUpResultCount, expectedResults: 100);
        }
        catch
        {
            scope.MarkReady();
            throw;
        }

        scope.MarkReady();
        return await scope.ExecuteAfterStartAsync(
            () => TriggerAndWaitForSearchAsync(
                viewModel,
                core,
                () =>
                {
                    viewModel.SetFilters(
                        createdAfterMs: 0,
                        createdBeforeMs: 900,
                        sourceApps: ["benchmark-source-00.exe"],
                        kinds: ["text"]);
                    return Task.CompletedTask;
                }));
    }

    private static Task<int> ExecuteEmptyQuerySampleAsync(
        DiagnosticArguments arguments) =>
        ExecuteViewModelSampleAsync(arguments, viewModel => viewModel.RefreshAsync());

    private static Task<int> ExecuteViewModelSampleAsync(
        DiagnosticArguments arguments,
        Func<ClipboardPanelViewModel, Task> trigger)
    {
        DiagnosticMeasurementScope scope = RequireMeasurementScope();
        DiagnosticCoreAdapter core = OpenDiagnosticCore(arguments);
        scope.RegisterCleanup(() => DisposeDiagnosticCore(core));
        var viewModel = new ClipboardPanelViewModel(core, new DisabledPasteService());
        scope.MarkReady();
        return scope.ExecuteAfterStartAsync(
            () => TriggerAndWaitForSearchAsync(viewModel, core, () => trigger(viewModel)));
    }

    private static async Task<int> ExecuteFfiSearchAsync(
        DiagnosticCoreAdapter core,
        SearchRequestDto request)
    {
        Task<SearchResponseDto> searchTask = core.SearchAsync(request);
        try
        {
            SearchResponseDto response = await searchTask.WaitAsync(
                TimeSpan.FromMilliseconds(SearchTimeoutMilliseconds));
            return response.Items.Count;
        }
        catch (TimeoutException)
        {
            await CancelSearchesAndWaitAsync(core, searchTask);
            throw new TimeoutException("The FFI search did not finish in time.");
        }
    }

    private static async Task<int> TriggerAndWaitForSearchAsync(
        ClipboardPanelViewModel viewModel,
        DiagnosticCoreAdapter core,
        Func<Task> trigger)
    {
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int loadingSeen = 0;
        PropertyChangedEventHandler handler = (_, eventArgs) =>
        {
            if (eventArgs.PropertyName != nameof(ClipboardPanelViewModel.IsLoading))
            {
                return;
            }

            if (viewModel.IsLoading)
            {
                Volatile.Write(ref loadingSeen, 1);
                return;
            }

            if (Volatile.Read(ref loadingSeen) != 1)
            {
                return;
            }

            if ((!string.IsNullOrWhiteSpace(viewModel.ErrorMessage)
                    || !string.IsNullOrWhiteSpace(viewModel.QueryError))
                && core.LastSearchException is Exception searchException)
            {
                completion.TrySetException(searchException);
                return;
            }

            completion.TrySetResult(viewModel.Items.Count);
        };
        viewModel.PropertyChanged += handler;

        try
        {
            Task triggerTask = trigger();
            try
            {
                int resultCount = await completion.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(SearchTimeoutMilliseconds));
                await triggerTask.WaitAsync(
                    TimeSpan.FromMilliseconds(SearchCancellationGraceMilliseconds));
                return resultCount;
            }
            catch (TimeoutException)
            {
                await CancelSearchesAndWaitAsync(core, triggerTask);
                throw new TimeoutException("The ViewModel search did not finish in time.");
            }
        }
        finally
        {
            viewModel.PropertyChanged -= handler;
        }
    }

    private static async Task CancelSearchesAndWaitAsync(
        DiagnosticCoreAdapter core,
        Task searchTask)
    {
        core.CancelSearches();
        try
        {
            await searchTask.WaitAsync(
                TimeSpan.FromMilliseconds(SearchCancellationGraceMilliseconds));
        }
        catch
        {
            // The original timeout remains the diagnostic failure after cleanup.
        }

        try
        {
            await core.WaitForSearchesAsync().WaitAsync(
                TimeSpan.FromMilliseconds(SearchCancellationGraceMilliseconds));
        }
        catch (TimeoutException)
        {
        }

        if (core.HasActiveSearches)
        {
            AbandonDiagnosticCore(core);
        }
    }

    private static ClipboardCoreClient OpenClient(DiagnosticArguments arguments) =>
        ClipboardCoreClient.Open(arguments.DataDirectory, arguments.VaultId, FixtureKey);

    private static DiagnosticCoreAdapter OpenDiagnosticCore(DiagnosticArguments arguments) =>
        new(OpenClient(arguments));

    private static DiagnosticMeasurementScope RequireMeasurementScope() =>
        CurrentMeasurementScope.Value
        ?? throw new InvalidOperationException("A diagnostic sample requires a measurement scope.");

    private static void DisposeDiagnosticCore(DiagnosticCoreAdapter core)
    {
        if (core.IsAbandoned)
        {
            return;
        }

        try
        {
            core.Dispose();
        }
        catch (InvalidOperationException) when (core.HasActiveSearches)
        {
            AbandonDiagnosticCore(core);
        }
    }

    private static void AbandonDiagnosticCore(DiagnosticCoreAdapter core)
    {
        if (core.TryAbandon())
        {
            AbandonedDiagnosticCores.Add(core);
        }
    }

    private static SearchRequestDto CreateSubstringRequest() =>
        new("performance-needle", SearchModeDto.Substring, SearchFiltersDto.Empty);

    private static SearchRequestDto CreateCombinedRequest() =>
        new(
            "performance-needle",
            SearchModeDto.Substring,
            new SearchFiltersDto(
                CreatedAfterMs: 0,
                CreatedBeforeMs: 900,
                SourceApps: ["benchmark-source-00.exe"],
                Kinds: ["text"]));

    private static async Task<SearchMetric> MeasureAsync(
        int samples,
        int expectedResults,
        Func<Task<int>> execute)
    {
        var elapsed = new List<long>(samples);
        for (int index = 0; index < samples; index++)
        {
            var scope = new DiagnosticMeasurementScope();
            CurrentMeasurementScope.Value = scope;
            try
            {
                Task<int> execution = execute();
                await scope.WaitForReadyAsync();

                Stopwatch watch = Stopwatch.StartNew();
                scope.Start();
                int results = await execution;
                watch.Stop();
                AssertResultCount(results, expectedResults);
                elapsed.Add(watch.ElapsedMilliseconds);
            }
            finally
            {
                CurrentMeasurementScope.Value = null;
                scope.Dispose();
            }
        }

        return ToMetric(elapsed, expectedResults);
    }

    private static SearchMetric ToMetric(IReadOnlyList<long> samples, int expectedResults) =>
        new(samples.Count, expectedResults, Percentile(samples, 50), Percentile(samples, 95));

    private static long Percentile(IReadOnlyList<long> samples, int percentile)
    {
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        }

        long[] ordered = samples.Order().ToArray();
        int rank = (int)Math.Ceiling(percentile * ordered.Length / 100d);
        return ordered[rank - 1];
    }

    private static ImageLruMetric MeasureImageLru()
    {
        using Process process = Process.GetCurrentProcess();
        long workingSetBefore = process.WorkingSet64;
        var cache = new BoundedLruCache<Guid, byte[]>(ImageCacheCapacity);
        Guid[] keys = Enumerable.Range(0, ImageCacheCapacity + 1)
            .Select(index => new Guid(index + 1, 0, 0, new byte[8]))
            .ToArray();

        for (int index = 0; index < keys.Length; index++)
        {
            cache.Set(keys[index], CreatePngBytes());
        }

        if (cache.Count != ImageCacheCapacity
            || cache.TryGetValue(keys[0], out _)
            || !cache.TryGetValue(keys[^1], out _))
        {
            throw new InvalidOperationException("The image LRU capacity contract was not preserved.");
        }

        process.Refresh();
        return new ImageLruMetric(
            ImageCacheCapacity,
            cache.Count,
            OldestEvicted: true,
            NewestAvailable: true,
            workingSetBefore,
            process.WorkingSet64);
    }

    private static byte[] CreatePngBytes()
    {
        return OnePixelPng.ToArray();
    }

    private static void AssertResultCount(int actual, int expectedResults)
    {
        if (actual != expectedResults)
        {
            throw new InvalidOperationException("Unexpected diagnostic result count.");
        }
    }

    private static string DescribeFailure(Exception exception) =>
        exception switch
        {
            ClipboardCoreException core => $"clipboard core status {core.Status}.",
            ArgumentException => "invalid diagnostic arguments.",
            TimeoutException => "ViewModel search timed out.",
            JsonException => "clipboard core returned invalid JSON.",
            DllNotFoundException => "clipboard FFI library is unavailable.",
            BadImageFormatException => "clipboard FFI library architecture is incompatible.",
            InvalidOperationException => "diagnostic assertion failed.",
            _ => exception.GetType().Name,
        };

    private static string GetCommandVersion(string executable, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return "unavailable";
            }

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)
                ? output
                : "unavailable";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static ulong GetPhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()),
        };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhysical : 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed class DiagnosticCoreAdapter : IClipboardPanelCore, IDisposable
    {
        private readonly ClipboardCoreClient _client;
        private readonly object _sync = new();
        private readonly CancellationTokenSource searchCancellation = new();
        private TaskCompletionSource<bool>? _searchesComplete;
        private Exception? _lastSearchException;
        private int _activeSearchCount;
        private bool _acceptSearches = true;
        private bool _abandoned;
        private bool _disposed;

        public DiagnosticCoreAdapter(ClipboardCoreClient client)
        {
            _client = client;
        }

        public Exception? LastSearchException
        {
            get
            {
                lock (_sync)
                {
                    return _lastSearchException;
                }
            }
        }

        public bool HasActiveSearches
        {
            get
            {
                lock (_sync)
                {
                    return _activeSearchCount != 0;
                }
            }
        }

        public bool IsAbandoned
        {
            get
            {
                lock (_sync)
                {
                    return _abandoned;
                }
            }
        }

        public Task<SearchResponseDto> SearchAsync(
            SearchRequestDto request,
            CancellationToken cancellationToken = default)
        {
            CancellationTokenSource linkedCancellation;
            lock (_sync)
            {
                if (!_acceptSearches || _disposed)
                {
                    return Task.FromCanceled<SearchResponseDto>(new CancellationToken(canceled: true));
                }

                linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    searchCancellation.Token);
                if (_activeSearchCount == 0)
                {
                    _searchesComplete = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _activeSearchCount++;
            }

            return RunSearchAsync(request, linkedCancellation);
        }

        public Task<MutationResponseDto> MarkUsedAsync(
            MarkUsedRequestDto request,
            CancellationToken cancellationToken = default) =>
            _client.MarkUsedAsync(request, cancellationToken);

        public Task<MutationResponseDto> SetFavoriteAsync(
            SetFavoriteRequestDto request,
            CancellationToken cancellationToken = default) =>
            _client.SetFavoriteAsync(request, cancellationToken);

        public Task<MutationResponseDto> CacheFileBundleAsync(
            Guid itemId,
            ulong maxBytes,
            CancellationToken cancellationToken = default) =>
            _client.CacheFileBundleAsync(itemId, maxBytes, cancellationToken);

        public Task<MutationResponseDto> UncacheFileBundleAsync(
            Guid itemId,
            CancellationToken cancellationToken = default) =>
            _client.UncacheFileBundleAsync(itemId, cancellationToken);

        public Task<MutationResponseDto> DeleteAsync(
            DeleteRequestDto request,
            CancellationToken cancellationToken = default) =>
            _client.DeleteAsync(request, cancellationToken);

        public Task<RetentionResponseDto> ClearUnfavoriteAsync(
            CancellationToken cancellationToken = default) =>
            _client.ClearUnfavoriteAsync(cancellationToken);

        public void CancelSearches()
        {
            lock (_sync)
            {
                _acceptSearches = false;
            }

            searchCancellation.Cancel();
        }

        public Task WaitForSearchesAsync()
        {
            lock (_sync)
            {
                return _activeSearchCount == 0
                    ? Task.CompletedTask
                    : _searchesComplete!.Task;
            }
        }

        public bool TryAbandon()
        {
            lock (_sync)
            {
                if (_abandoned || _disposed)
                {
                    return false;
                }

                _abandoned = true;
            }

            CancelSearches();
            return true;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed || _abandoned)
                {
                    return;
                }

                if (_activeSearchCount != 0)
                {
                    throw new InvalidOperationException(
                        "A diagnostic search is still using the clipboard core client.");
                }

                _acceptSearches = false;
                _disposed = true;
            }

            searchCancellation.Cancel();
            _client.Dispose();
            searchCancellation.Dispose();
        }

        private async Task<SearchResponseDto> RunSearchAsync(
            SearchRequestDto request,
            CancellationTokenSource linkedCancellation)
        {
            try
            {
                return await Task.Run(
                    () => _client.SearchAsync(request, linkedCancellation.Token),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_sync)
                {
                    _lastSearchException = exception;
                }

                throw;
            }
            finally
            {
                linkedCancellation.Dispose();
                CompleteSearch();
            }
        }

        private void CompleteSearch()
        {
            TaskCompletionSource<bool>? completion = null;
            lock (_sync)
            {
                _activeSearchCount--;
                if (_activeSearchCount == 0)
                {
                    completion = _searchesComplete;
                    _searchesComplete = null;
                }
            }

            completion?.TrySetResult(true);
        }
    }

    private sealed class DisabledPasteService : IClipboardItemPasteService
    {
        public Task<PasteResult> PasteAsync(
            ClipboardItemDto item,
            nint originalHwnd,
            CancellationToken cancellationToken = default) =>
            Task.FromException<PasteResult>(
                new NotSupportedException("Pasting is not available in diagnostics."));
    }

    private sealed record DiagnosticArguments(string DataDirectory, Guid VaultId, string Configuration);

    private sealed class DiagnosticMeasurementScope : IDisposable
    {
        private readonly TaskCompletionSource<bool> _ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _start = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _cleanup;

        public Task WaitForReadyAsync() => _ready.Task;

        public void MarkReady() => _ready.TrySetResult(true);

        public void Start() => _start.TrySetResult(true);

        public async Task<int> ExecuteAfterStartAsync(Func<Task<int>> execute)
        {
            await _start.Task.ConfigureAwait(false);
            return await execute().ConfigureAwait(false);
        }

        public void RegisterCleanup(Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            if (Interlocked.CompareExchange(ref _cleanup, cleanup, null) is not null)
            {
                throw new InvalidOperationException("The diagnostic sample already has cleanup.");
            }
        }

        public void Dispose()
        {
            Action? cleanup = Interlocked.Exchange(ref _cleanup, null);
            cleanup?.Invoke();
        }
    }

    private sealed record SearchMetric(int Samples, int Results, long P50Ms, long P95Ms);

    private sealed record ImageLruMetric(
        int Capacity,
        int Entries,
        bool OldestEvicted,
        bool NewestAvailable,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes);

    private sealed record DiagnosticReport(
        string GitRevision,
        string WindowsVersion,
        string DotnetVersion,
        string RustVersion,
        int LogicalProcessorCount,
        ulong PhysicalMemoryBytes,
        string Configuration,
        bool WarmOsFileCache,
        bool ColdSearchObjects,
        SearchMetric FfiSubstring,
        SearchMetric FfiCombined,
        SearchMetric ViewModelSubstring,
        SearchMetric ViewModelCombined,
        SearchMetric EmptyQuery,
        ImageLruMetric ImageLru,
        long PeakWorkingSetBytes,
        long ManagedHeapBytes);
}
