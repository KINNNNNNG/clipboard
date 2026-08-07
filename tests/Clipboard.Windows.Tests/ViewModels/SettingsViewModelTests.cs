using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;
using Clipboard.Windows.ViewModels;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Clipboard.Windows.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void Defaults_match_the_product_contract()
    {
        ClientSettings settings = ClientSettings.Default;

        Assert.Equal(1000, settings.MaxRegularItems);
        Assert.Equal((uint)30, settings.MaxAgeDays);
        Assert.Equal(1024UL * 1024 * 1024, settings.MaxImageBytes);
        Assert.Equal(5UL * ClientSettings.BytesPerGiB, settings.MaxFavoriteFileCacheBytes);
        Assert.True(settings.InterceptWinV);
        Assert.Equal("Alt+V", settings.FallbackHotkey);
        Assert.False(settings.StartWithWindows);
        Assert.Equal("system", settings.Theme);
    }

    [Fact]
    public async Task Settings_store_round_trips_nullable_limits_atomically()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var store = new ClientSettingsStore(path);
        var settings = new ClientSettings(
            null,
            7,
            null,
            false,
            "Ctrl+Shift+V",
            true,
            "dark");

        await store.SaveAsync(settings);
        ClientSettings reopened = await store.LoadAsync();

        Assert.Equal(settings, reopened);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(0, 30, 1024)]
    [InlineData(1000, 0, 1024)]
    [InlineData(1000, 30, 0)]
    public void Enabled_limits_must_be_greater_than_zero(
        int maxItems,
        uint maxAgeDays,
        ulong maxImageBytes)
    {
        var settings = ClientSettings.Default with
        {
            MaxRegularItems = maxItems,
            MaxAgeDays = maxAgeDays,
            MaxImageBytes = maxImageBytes,
        };

        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }

    [Fact]
    public async Task Save_persists_settings_applies_retention_and_reconfigures_shortcut()
    {
        var store = new MemorySettingsStore(ClientSettings.Default);
        var retention = new FakeRetentionService();
        var shortcuts = new FakeShortcutConfigurator();
        var startup = new FakeStartupSettingsService();
        var clock = new ManualTimeProvider();
        var viewModel = new SettingsViewModel(store, retention, shortcuts, startup, clock);
        await viewModel.LoadAsync();
        viewModel.MaxRegularItemsEnabled = false;
        viewModel.MaxAgeDays = 7;
        viewModel.MaxImageGiBEnabled = false;
        viewModel.InterceptWinV = false;
        viewModel.FallbackHotkey = "Ctrl+Shift+V";

        bool saved = await viewModel.SaveAsync();

        Assert.True(saved);
        Assert.Null(store.Current.MaxRegularItems);
        Assert.Equal((uint)7, store.Current.MaxAgeDays);
        Assert.Null(store.Current.MaxImageBytes);
        ApplyRetentionRequestDto request = Assert.Single(retention.Requests);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds(), request.NowMs);
        Assert.Null(request.Policy.MaxRegularItems);
        Assert.Equal((uint)7, request.Policy.MaxAgeDays);
        Assert.Null(request.Policy.MaxImageBytes);
        Assert.Equal(false, shortcuts.LastInterceptWinV);
        Assert.Equal("Ctrl+Shift+V", shortcuts.LastChord?.ToString());
    }

    [Fact]
    public async Task Loading_and_saving_settings_updates_the_capture_retention_policy()
    {
        var provider = new RetentionPolicyProvider();
        var store = new MemorySettingsStore(new ClientSettings(4, 8, 12, true, "Alt+V", false, "system"));
        var viewModel = new SettingsViewModel(
            store,
            new FakeRetentionService(),
            new FakeShortcutConfigurator(),
            new FakeStartupSettingsService(),
            retentionPolicy: provider);

        await viewModel.LoadAsync();
        Assert.Equal(4, provider.Current.MaxRegularItems);
        Assert.Equal((uint)8, provider.Current.MaxAgeDays);
        Assert.Equal((ulong)12, provider.Current.MaxImageBytes);

        viewModel.MaxRegularItems = 2;
        viewModel.MaxAgeDays = 3;
        viewModel.MaxImageGiB = 2;
        await viewModel.SaveAsync();

        Assert.Equal(2, provider.Current.MaxRegularItems);
        Assert.Equal((uint)3, provider.Current.MaxAgeDays);
        Assert.Equal(2UL * ClientSettings.BytesPerGiB, provider.Current.MaxImageBytes);
    }

    [Fact]
    public async Task Loading_and_saving_settings_updates_the_favorite_file_cache_policy()
    {
        var provider = new FavoriteFileCachePolicyProvider();
        var store = new MemorySettingsStore(ClientSettings.Default);
        var viewModel = new SettingsViewModel(
            store,
            new FakeRetentionService(),
            new FakeShortcutConfigurator(),
            new FakeStartupSettingsService(),
            favoriteFileCachePolicy: provider);

        await viewModel.LoadAsync();
        Assert.True(viewModel.MaxFavoriteFileCacheGiBEnabled);
        Assert.Equal(5, viewModel.MaxFavoriteFileCacheGiB);
        Assert.Equal(5UL * ClientSettings.BytesPerGiB, provider.MaxFavoriteFileCacheBytes);

        viewModel.MaxFavoriteFileCacheGiBEnabled = false;
        bool saved = await viewModel.SaveAsync();

        Assert.True(saved);
        Assert.Null(store.Current.MaxFavoriteFileCacheBytes);
        Assert.Null(provider.MaxFavoriteFileCacheBytes);
    }

    [Fact]
    public async Task Startup_failure_rolls_back_value_and_reports_sanitized_error()
    {
        const string secret = "startup failure secret";
        var store = new MemorySettingsStore(ClientSettings.Default);
        var startup = new FakeStartupSettingsService
        {
            Failure = new UnauthorizedAccessException(secret),
        };
        var viewModel = new SettingsViewModel(
            store,
            new FakeRetentionService(),
            new FakeShortcutConfigurator(),
            startup,
            new ManualTimeProvider());
        await viewModel.LoadAsync();
        viewModel.StartWithWindows = true;

        bool saved = await viewModel.SaveAsync();

        Assert.False(saved);
        Assert.False(viewModel.StartWithWindows);
        Assert.DoesNotContain(secret, viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Invalid_fallback_hotkey_is_reported_without_saving()
    {
        var store = new MemorySettingsStore(ClientSettings.Default);
        var viewModel = new SettingsViewModel(
            store,
            new FakeRetentionService(),
            new FakeShortcutConfigurator(),
            new FakeStartupSettingsService());
        await viewModel.LoadAsync();
        viewModel.FallbackHotkey = "V";

        bool saved = await viewModel.SaveAsync();

        Assert.False(saved);
        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Save_sync_rejects_missing_credentials_without_persisting_an_enabled_configuration()
    {
        var store = new MemorySettingsStore(ClientSettings.Default);
        var credentials = new MemoryCredentialStore();
        var viewModel = CreateViewModel(store, credentials);
        await viewModel.LoadAsync();
        viewModel.SyncEnabled = true;
        viewModel.SyncProvider = "webdav";
        viewModel.SyncEndpoint = "https://sync.example.test";
        viewModel.SyncRootPath = "/clipboard";

        bool saved = await viewModel.SaveSyncAsync();

        Assert.False(saved);
        Assert.False(store.Current.Sync?.Enabled ?? false);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, credentials.SaveCalls);
    }

    [Fact]
    public async Task Save_sync_writes_credentials_outside_settings_json()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var store = new ClientSettingsStore(path);
        var credentials = new MemoryCredentialStore();
        var viewModel = CreateViewModel(store, credentials);
        await viewModel.LoadAsync();
        viewModel.SyncEnabled = true;
        viewModel.SyncProvider = "webdav";
        viewModel.SyncEndpoint = "https://sync.example.test";
        viewModel.SyncRootPath = "/clipboard";
        viewModel.SyncAccount = "account-value";
        viewModel.SyncSecret = "secret-value";

        bool saved = await viewModel.SaveSyncAsync();
        string settingsJson = await File.ReadAllTextAsync(path);

        Assert.True(saved);
        Assert.DoesNotContain("account-value", settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", settingsJson, StringComparison.Ordinal);
        Assert.Equal(1, credentials.SaveCalls);
    }

    [Fact]
    public async Task Probe_and_run_sync_keep_status_text_redacted()
    {
        var store = new MemorySettingsStore(ClientSettings.Default with
        {
            Sync = new SyncSettings(
                true,
                "webdav",
                "https://sync.example.test",
                "/clipboard",
                null,
                null,
                null,
                Guid.NewGuid().ToString("D"),
                "profile"),
        });
        var credentials = new MemoryCredentialStore
        {
            Current = new SyncCredentials("account-value", "secret-value"),
        };
        var viewModel = CreateViewModel(store, credentials);
        await viewModel.LoadAsync();

        await viewModel.ProbeSyncAsync();
        AssertRedacted(viewModel.SyncStatus);
        await viewModel.RunSyncAsync();
        AssertRedacted(viewModel.SyncStatus);
    }

    [Fact]
    public async Task General_save_preserves_existing_sync_profile_without_touching_credentials()
    {
        var sync = new SyncSettings(
            true,
            "webdav",
            "https://sync.example.test",
            "/clipboard",
            null,
            null,
            null,
            Guid.NewGuid().ToString("D"),
            "profile");
        var store = new MemorySettingsStore(ClientSettings.Default with { Sync = sync });
        var credentials = new MemoryCredentialStore();
        var viewModel = CreateViewModel(store, credentials);
        await viewModel.LoadAsync();
        viewModel.Theme = "dark";

        bool saved = await viewModel.SaveAsync();

        Assert.True(saved);
        Assert.Equal(sync.CredentialProfileId, store.Current.Sync?.CredentialProfileId);
        Assert.Equal(0, credentials.SaveCalls);
    }

    [Fact]
    public async Task Loading_sync_settings_keeps_credential_fields_empty()
    {
        var store = new MemorySettingsStore(ClientSettings.Default with
        {
            Sync = new SyncSettings(
                true,
                "oss",
                "https://oss-cn-hangzhou.aliyuncs.com",
                null,
                "bucket-value",
                "cn-hangzhou",
                "clipboard",
                Guid.NewGuid().ToString("D"),
                "profile"),
        });
        var viewModel = CreateViewModel(store, new MemoryCredentialStore
        {
            Current = new SyncCredentials("account-value", "secret-value"),
        });

        await viewModel.LoadAsync();

        Assert.Equal(string.Empty, viewModel.SyncAccount);
        Assert.Equal(string.Empty, viewModel.SyncSecret);
    }

    [Fact]
    public async Task Save_sync_rejects_an_absolute_webdav_root_path()
    {
        var store = new MemorySettingsStore(ClientSettings.Default);
        var credentials = new MemoryCredentialStore();
        var viewModel = CreateViewModel(store, credentials);
        await viewModel.LoadAsync();
        viewModel.SyncEnabled = true;
        viewModel.SyncProvider = "webdav";
        viewModel.SyncEndpoint = "https://sync.example.test/root";
        viewModel.SyncRootPath = "https://untrusted.example/";
        viewModel.SyncAccount = "account-value";
        viewModel.SyncSecret = "secret-value";

        bool saved = await viewModel.SaveSyncAsync();

        Assert.False(saved);
        Assert.Null(store.Current.Sync);
        Assert.Equal(0, credentials.SaveCalls);
    }

    [Fact]
    public async Task Save_sync_reuses_existing_credentials_when_both_input_fields_are_empty()
    {
        SyncSettings sync = WebDavSettings("https://sync.example.test/old");
        var store = new MemorySettingsStore(ClientSettings.Default with { Sync = sync });
        var credentials = new MemoryCredentialStore
        {
            Current = new SyncCredentials("existing-account", "existing-secret"),
        };
        var viewModel = CreateViewModel(store, credentials);
        await viewModel.LoadAsync();
        viewModel.SyncEndpoint = "https://sync.example.test/new";

        bool saved = await viewModel.SaveSyncAsync();

        Assert.True(saved);
        Assert.Equal("https://sync.example.test/new", store.Current.Sync?.Endpoint);
        Assert.Equal(0, credentials.SaveCalls);
    }

    [Fact]
    public async Task Probe_sync_uses_the_current_unsaved_configuration()
    {
        SyncSettings sync = WebDavSettings("https://sync.example.test/old");
        var store = new MemorySettingsStore(ClientSettings.Default with { Sync = sync });
        var credentials = new MemoryCredentialStore
        {
            Current = new SyncCredentials("existing-account", "existing-secret"),
        };
        var native = new RecordingNative("{\"available\":true}"u8.ToArray());
        using var core = ClipboardCoreClient.Open("C:\\clipboard-test", Guid.NewGuid(), new byte[32], native);
        var viewModel = CreateViewModel(store, credentials, core);
        await viewModel.LoadAsync();
        viewModel.SyncEndpoint = "https://sync.example.test/new";
        viewModel.SyncRootPath = "/changed";

        await viewModel.ProbeSyncAsync();

        using JsonDocument request = JsonDocument.Parse(native.Request!);
        JsonElement remote = request.RootElement.GetProperty("payload").GetProperty("remote");
        Assert.Equal("https://sync.example.test/new/changed", remote.GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task Run_sync_uses_a_device_id_from_the_current_first_time_configuration()
    {
        var store = new MemorySettingsStore(ClientSettings.Default);
        var credentials = new MemoryCredentialStore();
        var native = new RecordingNative("{\"pulled\":0,\"merged\":0,\"uploaded\":0,\"rejected_local_only\":0}"u8.ToArray());
        using var core = ClipboardCoreClient.Open("C:\\clipboard-test", Guid.NewGuid(), new byte[32], native);
        var viewModel = CreateViewModel(store, credentials, core);
        await viewModel.LoadAsync();
        viewModel.SyncEnabled = true;
        viewModel.SyncProvider = "webdav";
        viewModel.SyncEndpoint = "https://sync.example.test/new";
        viewModel.SyncRootPath = "/clipboard";
        viewModel.SyncAccount = "account-value";
        viewModel.SyncSecret = "secret-value";

        await viewModel.RunSyncAsync();

        using JsonDocument request = JsonDocument.Parse(native.Request!);
        Assert.Equal("sync_remote", request.RootElement.GetProperty("type").GetString());
        Assert.True(Guid.TryParse(request.RootElement.GetProperty("payload").GetProperty("device_id").GetString(), out _));
    }

    [Fact]
    public async Task Save_sync_restores_existing_credentials_when_settings_persistence_fails()
    {
        SyncSettings sync = WebDavSettings("https://sync.example.test/old");
        var store = new FailingSettingsStore(ClientSettings.Default with { Sync = sync });
        var credentials = new MemoryCredentialStore
        {
            Current = new SyncCredentials("old-account", "old-secret"),
        };
        var viewModel = CreateViewModel(store, credentials);
        await viewModel.LoadAsync();
        viewModel.SyncAccount = "new-account";
        viewModel.SyncSecret = "new-secret";

        bool saved = await viewModel.SaveSyncAsync();

        Assert.False(saved);
        Assert.Equal(new SyncCredentials("old-account", "old-secret"), credentials.Current);
    }

    private static SyncSettings WebDavSettings(string endpoint) => new(
        true,
        "webdav",
        endpoint,
        "/clipboard",
        null,
        null,
        null,
        Guid.NewGuid().ToString("D"),
        "profile");

    private static SettingsViewModel CreateViewModel(
        IClientSettingsStore store,
        ISyncCredentialStore credentials,
        ClipboardCoreClient? syncCore = null) =>
        new(
            store,
            new FakeRetentionService(),
            new FakeShortcutConfigurator(),
            new FakeStartupSettingsService(),
            syncCore: syncCore,
            credentials: credentials);

    private static void AssertRedacted(string status)
    {
        Assert.DoesNotContain("sync.example.test", status, StringComparison.Ordinal);
        Assert.DoesNotContain("account-value", status, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", status, StringComparison.Ordinal);
    }

    private sealed class MemorySettingsStore(ClientSettings initial) : IClientSettingsStore
    {
        public ClientSettings Current { get; private set; } = initial;
        public int SaveCalls { get; private set; }

        public Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(
            ClientSettings settings,
            CancellationToken cancellationToken = default)
        {
            Current = settings;
            SaveCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRetentionService : ISettingsRetentionService
    {
        public List<ApplyRetentionRequestDto> Requests { get; } = [];

        public Task<RetentionResponseDto> ApplyRetentionAsync(
            ApplyRetentionRequestDto request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new RetentionResponseDto(0, 0));
        }
    }

    private sealed class FakeShortcutConfigurator : IGlobalShortcutConfigurator
    {
        public bool? LastInterceptWinV { get; private set; }
        public HotkeyChord? LastChord { get; private set; }

        public GlobalShortcutState Configure(bool interceptWinV, HotkeyChord fallback)
        {
            LastInterceptWinV = interceptWinV;
            LastChord = fallback;
            return GlobalShortcutState.Fallback;
        }
    }

    private sealed class FakeStartupSettingsService : IStartupSettingsService
    {
        public Exception? Failure { get; init; }

        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }

    private sealed class FailingSettingsStore(ClientSettings initial) : IClientSettingsStore
    {
        public Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(initial);

        public Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("simulated settings failure"));
    }

    private sealed class MemoryCredentialStore : ISyncCredentialStore
    {
        public int SaveCalls { get; private set; }
        public SyncCredentials? Current { get; set; }

        public Task SaveAsync(
            string profileId,
            SyncCredentials credentials,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            Current = credentials;
            return Task.CompletedTask;
        }

        public Task<SyncCredentials?> LoadAsync(
            string profileId,
            CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
        {
            Current = null;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNative(byte[] response) : IClipboardCoreNative
    {
        public byte[]? Request { get; private set; }

        public CoreStatus OpenV2(
            ReadOnlySpan<byte> dataDirectory,
            ReadOnlySpan<byte> vaultKey,
            ReadOnlySpan<byte> vaultId,
            out nint handle)
        {
            handle = 1;
            return CoreStatus.Ok;
        }

        public CoreStatus Execute(nint handle, ReadOnlySpan<byte> request, out CoreBuffer responseBuffer)
        {
            Request = request.ToArray();
            nint pointer = Marshal.AllocHGlobal(response.Length);
            Marshal.Copy(response, 0, pointer, response.Length);
            responseBuffer = new CoreBuffer(pointer, (nuint)response.Length, (nuint)response.Length);
            return CoreStatus.Ok;
        }

        public CoreStatus IngestImage(
            nint handle,
            ReadOnlySpan<byte> metadata,
            ReadOnlySpan<byte> png,
            out CoreBuffer responseBuffer) => throw new NotSupportedException();

        public CoreStatus ReadImage(nint handle, ReadOnlySpan<byte> itemId, out CoreBuffer responseBuffer) =>
            throw new NotSupportedException();

        public void FreeBuffer(CoreBuffer buffer) => Marshal.FreeHGlobal(buffer.Pointer);

        public void Close(nint handle)
        {
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"clipboard-settings-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
