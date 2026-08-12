using System.Globalization;
using Clipboard.Windows.Core;

namespace Clipboard.Windows.Platform;

internal interface IRealtimeSyncClient
{
    Task<SyncResponseDto> SyncAsync(
        SyncRemoteRequestDto request,
        CancellationToken cancellationToken = default);
}

internal interface IRealtimeSyncDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

internal interface IRealtimeSyncSettingsNotifier
{
    void OnSyncSettingsSaved(bool enabled);
}

internal interface IRealtimeSyncNotifier
{
    void NotifyChanged(string kind);
}

internal sealed class SystemRealtimeSyncDelay : IRealtimeSyncDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);
}

internal sealed class RealtimeSyncCoordinator :
    IAsyncDisposable,
    IRealtimeSyncSettingsNotifier,
    IRealtimeSyncNotifier,
    IClipboardCaptureObserver
{
    internal static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RetryMaximumDelay = TimeSpan.FromMinutes(15);

    private readonly object _stateLock = new();
    private readonly IRealtimeSyncClient _client;
    private readonly IClientSettingsStore _settingsStore;
    private readonly ISyncCredentialStore _credentials;
    private readonly IRealtimeSyncDelay _delay;
    private readonly TimeProvider _timeProvider;
    private readonly IGlobalLog? _globalLog;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private CancellationTokenSource? _debounceCancellation;
    private Task? _debounceTask;
    private Task? _workerTask;
    private DateTimeOffset? _lastAutomaticRunUtc;
    private bool _pending;
    private bool _running;
    private bool _automaticPaused;
    private int _retryAttempt;
    private bool _disposed;

    public void Start()
    {
        NotifyChanged("startup");
    }

    public RealtimeSyncCoordinator(
        IRealtimeSyncClient client,
        IClientSettingsStore settingsStore,
        ISyncCredentialStore credentials,
        IRealtimeSyncDelay? delay = null,
        TimeProvider? timeProvider = null,
        IGlobalLog? globalLog = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _delay = delay ?? new SystemRealtimeSyncDelay();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _globalLog = globalLog;
    }

    public void NotifyCaptured(string kind)
    {
        NotifyChanged(kind);
    }

    public void NotifyChanged(string kind)
    {
        if (kind is not ("text" or "image" or "startup"))
        {
            return;
        }

        CancellationTokenSource? previous = null;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _pending = true;
            if (_running || _automaticPaused)
            {
                return;
            }

            previous = _debounceCancellation;
            var current = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _debounceCancellation = current;
            _debounceTask = RunDebounceAsync(current);
        }
        TryCancel(previous);
    }

    public void OnCaptured(CaptureNotification notification) => NotifyChanged(notification.Kind);

    public void OnFailure(CaptureFailure failure) =>
        _globalLog?.Write(LogLevel.Warn, "sync", "sync.realtime.capture_failure", new Dictionary<string, string>
        {
            ["error_category"] = "capture",
        });

    public void OnSyncSettingsSaved(bool enabled)
    {
        if (enabled)
        {
            lock (_stateLock)
            {
                _automaticPaused = false;
                _retryAttempt = 0;
                _lastAutomaticRunUtc = null;
            }
            NotifyChanged("startup");
            return;
        }

        CancellationTokenSource? debounce;
        lock (_stateLock)
        {
            _pending = false;
            _automaticPaused = false;
            _retryAttempt = 0;
            debounce = _debounceCancellation;
            _debounceCancellation = null;
        }
        TryCancel(debounce);
    }

    public async ValueTask DisposeAsync()
    {
        Task? debounce;
        Task? worker;
        CancellationTokenSource? debounceCancellation;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = false;
            debounce = _debounceTask;
            worker = _workerTask;
            debounceCancellation = _debounceCancellation;
            _debounceCancellation = null;
        }

        TryCancel(_shutdown);
        TryCancel(debounceCancellation);
        await AwaitCancellationAsync(debounce);
        await AwaitCancellationAsync(worker);
        debounceCancellation?.Dispose();
        _shutdown.Dispose();
        _syncGate.Dispose();
    }

    private async Task RunDebounceAsync(CancellationTokenSource source)
    {
        try
        {
            await _delay.DelayAsync(Debounce, source.Token);
            lock (_stateLock)
            {
                if (_disposed || !ReferenceEquals(_debounceCancellation, source) || !_pending || _running)
                {
                    return;
                }

                _debounceCancellation = null;
                _running = true;
                _workerTask = Task.Run(RunWorkerAsync);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        finally
        {
            source.Dispose();
        }
    }

    private async Task RunWorkerAsync()
    {
        bool skipMinimumInterval = false;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                if (!skipMinimumInterval)
                {
                    await WaitForMinimumIntervalAsync(_shutdown.Token);
                }
                skipMinimumInterval = false;
                _shutdown.Token.ThrowIfCancellationRequested();

                lock (_stateLock)
                {
                    if (_disposed || !_pending)
                    {
                        return;
                    }
                    _pending = false;
                }

                SyncAttemptOutcome outcome = await SyncOnceAsync(_shutdown.Token);

                TimeSpan? retryDelay = null;
                lock (_stateLock)
                {
                    if (outcome == SyncAttemptOutcome.AuthenticationFailure)
                    {
                        _automaticPaused = true;
                        return;
                    }
                    if (outcome == SyncAttemptOutcome.RetryableFailure)
                    {
                        _pending = true;
                        retryDelay = RetryDelayForAttempt(_retryAttempt++);
                    }
                    else
                    {
                        _retryAttempt = 0;
                    }
                }

                if (retryDelay is { } delay)
                {
                    await _delay.DelayAsync(delay, _shutdown.Token);
                    skipMinimumInterval = true;
                }

                lock (_stateLock)
                {
                    if (!_pending)
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_stateLock)
            {
                _running = false;
            }
        }
    }

    private async Task WaitForMinimumIntervalAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? lastRun;
        lock (_stateLock)
        {
            lastRun = _lastAutomaticRunUtc;
        }
        if (lastRun is null)
        {
            return;
        }

        TimeSpan remaining = MinimumInterval - (_timeProvider.GetUtcNow() - lastRun.Value);
        if (remaining > TimeSpan.Zero)
        {
            await _delay.DelayAsync(remaining, cancellationToken);
        }
    }

    private async Task<SyncAttemptOutcome> SyncOnceAsync(CancellationToken cancellationToken)
    {
        ClientSettings settings;
        try
        {
            settings = await _settingsStore.LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            WriteEnd("unknown", "failure", 0, "settings_error", "settings_load_failed");
            return SyncAttemptOutcome.RetryableFailure;
        }

        SyncSettings? sync = settings.Sync;
        if (sync is not { Enabled: true })
        {
            return SyncAttemptOutcome.Success;
        }

        WriteStart(sync.Provider);
        try
        {
            RemoteConfigDto remote = await RemoteSyncRequestFactory.CreateAsync(
                sync,
                _credentials,
                cancellationToken: cancellationToken);
            var request = new SyncRemoteRequestDto(Guid.Parse(sync.DeviceId), remote);

            await _syncGate.WaitAsync(cancellationToken);
            try
            {
                lock (_stateLock)
                {
                    _lastAutomaticRunUtc = _timeProvider.GetUtcNow();
                }
                SyncResponseDto response = await _client.SyncAsync(request, cancellationToken);
                int count = response.Pulled + response.Merged + response.Uploaded;
                if (response.ErrorCategory is null)
                {
                    WriteEnd(sync.Provider, "success", count);
                    return SyncAttemptOutcome.Success;
                }
                else
                {
                    WriteEnd(
                        sync.Provider,
                        "failure",
                        count,
                        response.ErrorCategory,
                        response.ErrorCode,
                        SanitizeRemoteErrorDetail(response.ErrorDetail),
                        response.ErrorOperation);
                    return response.ErrorCategory == "authentication"
                        ? SyncAttemptOutcome.AuthenticationFailure
                        : IsRetryableCategory(response.ErrorCategory)
                            ? SyncAttemptOutcome.RetryableFailure
                            : SyncAttemptOutcome.Success;
                }
            }
            finally
            {
                _syncGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            WriteEnd(sync.Provider, "failure", 0, "client_error", "sync_failed");
            return SyncAttemptOutcome.RetryableFailure;
        }
    }

    private static TimeSpan RetryDelayForAttempt(int attempt)
    {
        int exponent = Math.Clamp(attempt, 0, 5);
        double seconds = RetryBaseDelay.TotalSeconds * Math.Pow(2, exponent);
        double jitter = 1.0 + Random.Shared.NextDouble() * 0.25;
        return TimeSpan.FromSeconds(Math.Min(seconds * jitter, RetryMaximumDelay.TotalSeconds));
    }

    private static bool IsRetryableCategory(string? category) =>
        category is "network" or "network_timeout" or "network_connect" or "network_request" or "rate_limited";

    private enum SyncAttemptOutcome
    {
        Success,
        RetryableFailure,
        AuthenticationFailure,
    }

    private void WriteStart(string provider) =>
        _globalLog?.Write(LogLevel.Info, "sync", "sync.realtime.start", new Dictionary<string, string>
        {
            ["provider"] = provider,
        });

    private void WriteEnd(
        string provider,
        string status,
        int count,
        string? errorCategory = null,
        string? errorCode = null,
        string? errorDetail = null,
        string? errorOperation = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["provider"] = provider,
            ["status"] = status,
            ["count"] = count.ToString(CultureInfo.InvariantCulture),
        };
        AddField(fields, "error_category", errorCategory);
        AddField(fields, "error_code", errorCode);
        AddField(fields, "error_detail", errorDetail);
        AddField(fields, "error_operation", errorOperation);
        _globalLog?.Write(LogLevel.Info, "sync", "sync.realtime.end", fields);
    }

    private static void AddField(Dictionary<string, string> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[name] = value;
        }
    }

    private static string? SanitizeRemoteErrorDetail(string? detail) => detail switch
    {
        "network_timeout" or "network_connect" or "network_request" => detail,
        { Length: 8 } when detail.StartsWith("http_", StringComparison.Ordinal)
            && detail[5] is >= '0' and <= '9'
            && detail[6] is >= '0' and <= '9'
            && detail[7] is >= '0' and <= '9' => detail,
        _ => null,
    };

    private static async Task AwaitCancellationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
