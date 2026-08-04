using Clipboard.Windows.Core;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using SystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Clipboard.Windows.Platform;

internal interface ISourceApplicationResolver
{
    string Resolve();
}

internal interface IClipboardCaptureObserver
{
    void OnCaptured(CaptureNotification notification);

    void OnFailure(CaptureFailure failure);
}

internal interface IRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemRetryDelay : IRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

internal sealed record CaptureNotification(Guid ItemId, string Kind);

internal sealed record CaptureFailure(string Message);

internal sealed class ClipboardCaptureCoordinator : IDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
    ];

    private readonly IClipboardReader _reader;
    private readonly IClipboardCaptureSink _core;
    private readonly ISourceApplicationResolver _sourceResolver;
    private readonly ClipboardSuppression _suppression;
    private readonly IClipboardCaptureObserver _observer;
    private readonly IRetryDelay _retryDelay;
    private readonly TimeProvider _timeProvider;
    private readonly IRetentionPolicyProvider _retentionPolicy;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private DispatcherQueue? _dispatcherQueue;
    private int _disposed;

    public ClipboardCaptureCoordinator(
        IClipboardReader reader,
        IClipboardCaptureSink core,
        ISourceApplicationResolver sourceResolver,
        ClipboardSuppression suppression,
        IClipboardCaptureObserver observer,
        IRetryDelay? retryDelay = null,
        TimeProvider? timeProvider = null,
        IRetentionPolicyProvider? retentionPolicy = null)
    {
        _reader = reader;
        _core = core;
        _sourceResolver = sourceResolver;
        _suppression = suppression;
        _observer = observer;
        _retryDelay = retryDelay ?? new SystemRetryDelay();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retentionPolicy = retentionPolicy ?? new RetentionPolicyProvider();
    }

    public async Task CaptureAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            ClipboardPayload payload = await ReadWithRetryAsync(cancellationToken);
            string sourceApp = ResolveSourceApplication();
            long capturedMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            switch (payload.Kind)
            {
                case ClipboardPayloadKind.Text when payload.TextContent is not null:
                    if (_suppression.TryConsumeText(payload.TextContent))
                    {
                        return;
                    }
                    MutationResponseDto text = await _core.IngestTextAsync(
                            new IngestTextRequestDto(payload.TextContent, sourceApp, capturedMs),
                            cancellationToken);
                    await ApplyRetentionAsync(cancellationToken);
                    _observer.OnCaptured(new CaptureNotification(text.ItemId, "text"));
                    break;

                case ClipboardPayloadKind.Image when !payload.Png.IsEmpty:
                    if (_suppression.TryConsumeImage(payload.Png.Span))
                    {
                        return;
                    }
                    MutationResponseDto image = await _core.IngestImageAsync(
                            new IngestImageRequestDto(
                                payload.Width,
                                payload.Height,
                                sourceApp,
                                capturedMs),
                            payload.Png,
                            cancellationToken);
                    await ApplyRetentionAsync(cancellationToken);
                    _observer.OnCaptured(new CaptureNotification(image.ItemId, "image"));
                    break;

                case ClipboardPayloadKind.FileBundle when payload.FileEntries.Count > 0:
                    if (_suppression.TryConsumeFileBundle(
                        payload.FileEntries.Select(file => file.Path)))
                    {
                        return;
                    }
                    var files = await _core.IngestFileBundleAsync(
                        new IngestFileBundleRequestDto(
                            payload.FileEntries
                                .Select(file => new FileEntryDto(
                                    file.Path,
                                    file.IsDirectory
                                        ? FileEntryKindDto.Directory
                                        : FileEntryKindDto.File,
                                    file.Size,
                                    file.ModifiedMs))
                                .ToArray(),
                            sourceApp,
                            capturedMs),
                        cancellationToken);
                    await ApplyRetentionAsync(cancellationToken);
                    _observer.OnCaptured(new CaptureNotification(files.ItemId, "file_bundle"));
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _observer.OnFailure(new CaptureFailure("Clipboard capture failed."));
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public void Start(DispatcherQueue dispatcherQueue)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        if (_dispatcherQueue is not null)
        {
            throw new InvalidOperationException("Clipboard capture has already started.");
        }
        _dispatcherQueue = dispatcherQueue;
        SystemClipboard.ContentChanged += OnClipboardContentChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_dispatcherQueue is not null)
        {
            SystemClipboard.ContentChanged -= OnClipboardContentChanged;
            _dispatcherQueue = null;
        }
    }

    private async Task<ClipboardPayload> ReadWithRetryAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await _reader.ReadAsync(cancellationToken);
            }
            catch (ClipboardBusyException) when (attempt < RetryDelays.Length)
            {
                await _retryDelay.DelayAsync(RetryDelays[attempt], cancellationToken);
            }
        }
    }

    private string ResolveSourceApplication()
    {
        try
        {
            string source = _sourceResolver.Resolve();
            return string.IsNullOrWhiteSpace(source) ? "unknown" : source;
        }
        catch
        {
            return "unknown";
        }
    }

    private Task ApplyRetentionAsync(CancellationToken cancellationToken) =>
        _core.ApplyRetentionAsync(
            new ApplyRetentionRequestDto(
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                _retentionPolicy.Current),
            cancellationToken);

    private void OnClipboardContentChanged(object? sender, object args)
    {
        _dispatcherQueue?.TryEnqueue(() => _ = CaptureAsync());
    }
}
