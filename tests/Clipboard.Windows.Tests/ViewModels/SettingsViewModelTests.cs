using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;
using Clipboard.Windows.ViewModels;
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
