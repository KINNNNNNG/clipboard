using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class RealtimeSyncCoordinatorTests
{
    [Fact]
    public async Task Notifications_inside_debounce_window_run_one_sync()
    {
        var delay = new ControlledDelay();
        var client = new RecordingRemoteSyncClient();
        await using var coordinator = CreateCoordinator(client, delay);

        coordinator.NotifyCaptured("text");
        coordinator.NotifyCaptured("image");

        await delay.WaitForCountAsync(2);
        Assert.All(delay.Requested, requested => Assert.Equal(RealtimeSyncCoordinator.Debounce, requested));
        await delay.ReleaseNextAsync();
        await client.WaitForCallCountAsync(1);

        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Notifications_after_a_sync_wait_for_the_remaining_minimum_interval()
    {
        var clock = new ManualTimeProvider();
        var delay = new ControlledDelay();
        var client = new RecordingRemoteSyncClient();
        await using var coordinator = CreateCoordinator(client, delay, timeProvider: clock);

        coordinator.NotifyCaptured("text");
        await delay.WaitForCountAsync(1);
        await delay.ReleaseNextAsync();
        await client.WaitForCallCountAsync(1);

        coordinator.NotifyCaptured("text");
        await delay.WaitForCountAsync(2);
        Assert.Equal(RealtimeSyncCoordinator.Debounce, delay.Requested[1]);
        await delay.ReleaseNextAsync();
        await delay.WaitForCountAsync(3);
        Assert.Equal(RealtimeSyncCoordinator.MinimumInterval, delay.Requested[2]);
        Assert.Equal(1, client.CallCount);

        clock.Advance(RealtimeSyncCoordinator.MinimumInterval);
        await delay.ReleaseNextAsync();
        await client.WaitForCallCountAsync(2);

        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task Notification_during_running_sync_runs_once_without_another_debounce()
    {
        var clock = new ManualTimeProvider();
        var delay = new ControlledDelay();
        var client = new BlockingRemoteSyncClient();
        await using var coordinator = CreateCoordinator(client, delay, timeProvider: clock);

        coordinator.NotifyCaptured("text");
        await delay.WaitForCountAsync(1);
        await delay.ReleaseNextAsync();
        await client.WaitForCallCountAsync(1);

        coordinator.NotifyCaptured("image");
        client.ReleaseCurrentCall();
        await delay.WaitForCountAsync(2);
        Assert.Equal(RealtimeSyncCoordinator.MinimumInterval, delay.Requested[1]);

        clock.Advance(RealtimeSyncCoordinator.MinimumInterval);
        await delay.ReleaseNextAsync();
        await client.WaitForCallCountAsync(2);
        Assert.Equal(2, client.CallCount);

        client.ReleaseCurrentCall();
    }

    [Fact]
    public async Task File_bundle_notifications_do_not_schedule_remote_sync()
    {
        var delay = new ControlledDelay();
        var client = new RecordingRemoteSyncClient();
        await using var coordinator = CreateCoordinator(client, delay);

        coordinator.NotifyCaptured("file_bundle");
        await Task.Yield();

        Assert.Empty(delay.Requested);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Disabling_sync_cancels_a_pending_debounce()
    {
        var delay = new ControlledDelay();
        var client = new RecordingRemoteSyncClient();
        await using var coordinator = CreateCoordinator(client, delay);

        coordinator.NotifyCaptured("text");
        await delay.WaitForCountAsync(1);
        coordinator.OnSyncSettingsSaved(false);
        await Task.Yield();

        Assert.Equal(0, client.CallCount);
    }

    private static RealtimeSyncCoordinator CreateCoordinator(
        IRealtimeSyncClient client,
        ControlledDelay delay,
        TimeProvider? timeProvider = null) =>
        new(
            client,
            new MemorySettingsStore(ClientSettings.Default with { Sync = EnabledSettings() }),
            new MemoryCredentialStore(),
            delay,
            timeProvider);

    private static SyncSettings EnabledSettings() => new(
        true,
        "webdav",
        "https://sync.example.test",
        "/clipboard",
        null,
        null,
        null,
        Guid.NewGuid().ToString("D"),
        "profile");

    private sealed class MemorySettingsStore(ClientSettings settings) : IClientSettingsStore
    {
        public Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MemoryCredentialStore : ISyncCredentialStore
    {
        public Task SaveAsync(string profileId, SyncCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SyncCredentials?> LoadAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SyncCredentials?>(new SyncCredentials("account", "secret"));

        public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ControlledDelay : IRealtimeSyncDelay
    {
        private readonly object _sync = new();
        private readonly Queue<TaskCompletionSource> _pending = [];
        private TaskCompletionSource _changed = NewCompletionSource();

        public List<TimeSpan> Requested { get; } = [];

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            var completion = NewCompletionSource();
            _ = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                completion);
            lock (_sync)
            {
                Requested.Add(duration);
                _pending.Enqueue(completion);
                _changed.TrySetResult();
                _changed = NewCompletionSource();
            }
            return completion.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForCountAsync(int expected)
        {
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    if (Requested.Count >= expected)
                    {
                        return;
                    }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public Task ReleaseNextAsync()
        {
            while (true)
            {
                TaskCompletionSource completion;
                lock (_sync)
                {
                    completion = _pending.Dequeue();
                }
                if (completion.TrySetResult())
                {
                    return Task.CompletedTask;
                }
            }
        }

        private static TaskCompletionSource NewCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private class RecordingRemoteSyncClient : IRealtimeSyncClient
    {
        private readonly object _sync = new();
        private TaskCompletionSource _changed = NewCompletionSource();

        public int CallCount { get; private set; }

        public virtual Task<SyncResponseDto> SyncAsync(
            SyncRemoteRequestDto request,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                CallCount++;
                _changed.TrySetResult();
                _changed = NewCompletionSource();
            }
            return Task.FromResult(new SyncResponseDto(0, 0, 1, 0));
        }

        public async Task WaitForCallCountAsync(int expected)
        {
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    if (CallCount >= expected)
                    {
                        return;
                    }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        protected void RecordCall()
        {
            lock (_sync)
            {
                CallCount++;
                _changed.TrySetResult();
                _changed = NewCompletionSource();
            }
        }

        private static TaskCompletionSource NewCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingRemoteSyncClient : RecordingRemoteSyncClient
    {
        private readonly object _sync = new();
        private TaskCompletionSource<SyncResponseDto>? _current;

        public override Task<SyncResponseDto> SyncAsync(
            SyncRemoteRequestDto request,
            CancellationToken cancellationToken)
        {
            RecordCall();
            lock (_sync)
            {
                _current = new TaskCompletionSource<SyncResponseDto>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _current.Task.WaitAsync(cancellationToken);
            }
        }

        public void ReleaseCurrentCall()
        {
            TaskCompletionSource<SyncResponseDto> current;
            lock (_sync)
            {
                current = _current ?? throw new InvalidOperationException("No sync call is running.");
                _current = null;
            }
            current.TrySetResult(new SyncResponseDto(0, 0, 1, 0));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
