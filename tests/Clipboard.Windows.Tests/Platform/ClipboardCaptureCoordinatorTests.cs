using System.Collections.Concurrent;
using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class ClipboardCaptureCoordinatorTests
{
    [Fact]
    public async Task Unicode_text_is_ingested_once_and_unknown_source_is_preserved()
    {
        var reader = new FakeReader(ClipboardPayload.Text("跨设备剪贴板"));
        var core = new FakeCore();
        var observer = new FakeObserver();
        var coordinator = CreateCoordinator(reader, core, observer, sourceApp: "unknown");

        await coordinator.CaptureAsync();

        IngestTextRequestDto captured = Assert.Single(core.TextRequests);
        Assert.Equal("跨设备剪贴板", captured.Text);
        Assert.Equal("unknown", captured.SourceApp);
        CaptureNotification notification = Assert.Single(observer.Captured);
        Assert.Equal("text", notification.Kind);
        Assert.DoesNotContain("跨设备剪贴板", notification.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Normalized_png_is_ingested_with_dimensions()
    {
        byte[] png = [0x89, 0x50, 0x4e, 0x47];
        var reader = new FakeReader(ClipboardPayload.Image(png, 640, 480));
        var core = new FakeCore();
        var observer = new FakeObserver();
        var coordinator = CreateCoordinator(reader, core, observer, sourceApp: "mspaint.exe");

        await coordinator.CaptureAsync();

        CapturedImage captured = Assert.Single(core.ImageRequests);
        Assert.Equal((uint)640, captured.Request.Width);
        Assert.Equal((uint)480, captured.Request.Height);
        Assert.Equal("mspaint.exe", captured.Request.SourceApp);
        Assert.Equal(png, captured.Png);
        Assert.Equal("image", Assert.Single(observer.Captured).Kind);
    }

    [Fact]
    public async Task Successful_capture_applies_the_current_retention_policy_before_notifying()
    {
        var core = new FakeCore();
        var observer = new FakeObserver();
        var policy = new RetentionPolicyProvider();
        policy.Update(new ClientSettings(2, 5, 10, true, "Alt+V", false, "system"));
        var clock = new ManualTimeProvider();
        var coordinator = CreateCoordinator(
            new FakeReader(ClipboardPayload.Text("retained")),
            core,
            observer,
            retentionPolicy: policy,
            timeProvider: clock);

        await coordinator.CaptureAsync();

        ApplyRetentionRequestDto request = Assert.Single(core.RetentionRequests);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds(), request.NowMs);
        Assert.Equal(2, request.Policy.MaxRegularItems);
        Assert.Equal((uint)5, request.Policy.MaxAgeDays);
        Assert.Equal((ulong)10, request.Policy.MaxImageBytes);
        Assert.Single(observer.Captured);
    }

    [Fact]
    public async Task Clipboard_lock_retries_with_bounded_backoff()
    {
        var reader = new FakeReader(
            new ClipboardBusyException(),
            new ClipboardBusyException(),
            new ClipboardBusyException(),
            new ClipboardBusyException(),
            ClipboardPayload.Text("after retry"));
        var delay = new FakeDelay();
        var core = new FakeCore();
        var coordinator = CreateCoordinator(
            reader,
            core,
            new FakeObserver(),
            retryDelay: delay);

        await coordinator.CaptureAsync();

        Assert.Equal(5, reader.Attempts);
        Assert.Equal(
            [20, 50, 100, 200],
            delay.Delays.Select(value => (int)value.TotalMilliseconds).ToArray());
        Assert.Single(core.TextRequests);
    }

    [Fact]
    public void Clipboard_retry_reads_on_the_dispatcher_context()
    {
        var context = new PumpSynchronizationContext();
        var reader = new ContextCheckingReader(context);
        var core = new FakeCore();
        var coordinator = CreateCoordinator(
            reader,
            core,
            new FakeObserver(),
            retryDelay: new AsynchronousDelay());

        context.Run(() => coordinator.CaptureAsync());

        Assert.Equal(2, reader.Attempts);
        Assert.Single(core.TextRequests);
    }

    [Fact]
    public async Task Suppression_token_skips_own_clipboard_write()
    {
        var suppression = new ClipboardSuppression();
        suppression.RegisterText("written by this app");
        var core = new FakeCore();
        var coordinator = CreateCoordinator(
            new FakeReader(ClipboardPayload.Text("written by this app")),
            core,
            new FakeObserver(),
            suppression: suppression);

        await coordinator.CaptureAsync();

        Assert.Empty(core.TextRequests);
        Assert.Empty(core.ImageRequests);
    }

    [Fact]
    public async Task Core_failure_notifies_without_exposing_clipboard_content()
    {
        const string secret = "database failure secret";
        var core = new FakeCore
        {
            Failure = new ClipboardCoreException(CoreStatus.CoreError),
        };
        var observer = new FakeObserver();
        var coordinator = CreateCoordinator(
            new FakeReader(ClipboardPayload.Text(secret)),
            core,
            observer);

        await coordinator.CaptureAsync();

        CaptureFailure failure = Assert.Single(observer.Failures);
        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        Assert.Empty(observer.Captured);
    }

    private static ClipboardCaptureCoordinator CreateCoordinator(
        IClipboardReader reader,
        IClipboardCaptureSink core,
        IClipboardCaptureObserver observer,
        string sourceApp = "notepad.exe",
        ClipboardSuppression? suppression = null,
        IRetryDelay? retryDelay = null,
        IRetentionPolicyProvider? retentionPolicy = null,
        TimeProvider? timeProvider = null) =>
        new(
            reader,
            core,
            new FakeSourceResolver(sourceApp),
            suppression ?? new ClipboardSuppression(),
            observer,
            retryDelay ?? new FakeDelay(),
            timeProvider,
            retentionPolicy);

    private sealed class FakeReader : IClipboardReader
    {
        private readonly Queue<object> _results;

        public FakeReader(params object[] results)
        {
            _results = new Queue<object>(results);
        }

        public int Attempts { get; private set; }

        public Task<ClipboardPayload> ReadAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            object result = _results.Dequeue();
            return result is Exception error
                ? Task.FromException<ClipboardPayload>(error)
                : Task.FromResult((ClipboardPayload)result);
        }
    }

    private sealed class ContextCheckingReader(SynchronizationContext expectedContext)
        : IClipboardReader
    {
        public int Attempts { get; private set; }

        public Task<ClipboardPayload> ReadAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            if (!ReferenceEquals(SynchronizationContext.Current, expectedContext))
            {
                return Task.FromException<ClipboardPayload>(
                    new InvalidOperationException("Clipboard read left the dispatcher context."));
            }
            return Attempts == 1
                ? Task.FromException<ClipboardPayload>(new ClipboardBusyException())
                : Task.FromResult(ClipboardPayload.Text("after retry"));
        }
    }

    private sealed class FakeCore : IClipboardCaptureSink
    {
        public List<IngestTextRequestDto> TextRequests { get; } = [];
        public List<CapturedImage> ImageRequests { get; } = [];
        public List<ApplyRetentionRequestDto> RetentionRequests { get; } = [];
        public Exception? Failure { get; init; }

        public Task<MutationResponseDto> IngestTextAsync(
            IngestTextRequestDto request,
            CancellationToken cancellationToken = default)
        {
            TextRequests.Add(request);
            return Complete();
        }

        public Task<MutationResponseDto> IngestImageAsync(
            IngestImageRequestDto request,
            ReadOnlyMemory<byte> png,
            CancellationToken cancellationToken = default)
        {
            ImageRequests.Add(new CapturedImage(request, png.ToArray()));
            return Complete();
        }

        public Task<MutationResponseDto> IngestFileBundleAsync(
            IngestFileBundleRequestDto request,
            CancellationToken cancellationToken = default) => Complete();

        public Task<RetentionResponseDto> ApplyRetentionAsync(
            ApplyRetentionRequestDto request,
            CancellationToken cancellationToken = default)
        {
            RetentionRequests.Add(request);
            return Task.FromResult(new RetentionResponseDto(0, 0));
        }

        private Task<MutationResponseDto> Complete() => Failure is null
            ? Task.FromResult(new MutationResponseDto(Guid.NewGuid()))
            : Task.FromException<MutationResponseDto>(Failure);
    }

    private sealed class FakeSourceResolver(string sourceApp) : ISourceApplicationResolver
    {
        public string Resolve() => sourceApp;
    }

    private sealed class FakeObserver : IClipboardCaptureObserver
    {
        public List<CaptureNotification> Captured { get; } = [];
        public List<CaptureFailure> Failures { get; } = [];

        public void OnCaptured(CaptureNotification notification) => Captured.Add(notification);

        public void OnFailure(CaptureFailure failure) => Failures.Add(failure);
    }

    private sealed class FakeDelay : IRetryDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class AsynchronousDelay : IRetryDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(10);
                completion.SetResult();
            });
            return completion.Task;
        }
    }

    private sealed class PumpSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = [];

        public override void Post(SendOrPostCallback callback, object? state) =>
            _work.Add((callback, state));

        public void Run(Func<Task> action)
        {
            SynchronizationContext? previous = Current;
            try
            {
                SetSynchronizationContext(this);
                Task task = action();
                DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                while (!task.IsCompleted)
                {
                    if (_work.TryTake(out var item, 50))
                    {
                        item.Callback(item.State);
                    }
                    else if (DateTimeOffset.UtcNow >= deadline)
                    {
                        throw new TimeoutException("Dispatcher context did not complete the operation.");
                    }
                }
                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private sealed record CapturedImage(IngestImageRequestDto Request, byte[] Png);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
