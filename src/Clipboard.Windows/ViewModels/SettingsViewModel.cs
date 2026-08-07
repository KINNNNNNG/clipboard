using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;

namespace Clipboard.Windows.ViewModels;

internal sealed class SettingsViewModel : ObservableObject
{
    private readonly IClientSettingsStore _store;
    private readonly ISettingsRetentionService _retention;
    private readonly IGlobalShortcutConfigurator _shortcuts;
    private readonly IStartupSettingsService _startup;
    private readonly TimeProvider _timeProvider;
    private readonly IRetentionPolicyProvider? _retentionPolicy;
    private readonly IFavoriteFileCachePolicyProvider? _favoriteFileCachePolicy;
    private readonly ClipboardCoreClient? _syncCore;
    private readonly ISyncCredentialStore? _credentials;
    private readonly IGlobalLog? _globalLog;
    private ClientSettings _persisted = ClientSettings.Default;
    private bool _maxRegularItemsEnabled = true;
    private int _maxRegularItems = 1000;
    private bool _maxAgeDaysEnabled = true;
    private uint _maxAgeDays = 30;
    private bool _maxImageGiBEnabled = true;
    private double _maxImageGiB = 1;
    private bool _maxFavoriteFileCacheGiBEnabled = true;
    private double _maxFavoriteFileCacheGiB = 5;
    private bool _interceptWinV = true;
    private string _fallbackHotkey = "Alt+V";
    private bool _startWithWindows;
    private string _theme = "system";
    private string? _errorMessage;
    private bool _syncEnabled;
    private string _syncProvider = "webdav";
    private string _syncEndpoint = "https://";
    private string _syncRootPath = "/";
    private string _syncBucket = string.Empty;
    private string _syncRegion = "cn-hangzhou";
    private string _syncPrefix = "clipboard";
    private string _syncAccount = string.Empty;
    private string _syncSecret = string.Empty;
    private string _syncStatus = string.Empty;
    private string _loggingLevel = "info";
    private int _loggingRetentionDays = 7;
    private ulong _loggingMaxSizeBytes = 200UL * 1024 * 1024;

    public SettingsViewModel(
        IClientSettingsStore store,
        ISettingsRetentionService retention,
        IGlobalShortcutConfigurator shortcuts,
        IStartupSettingsService startup,
        TimeProvider? timeProvider = null,
        IRetentionPolicyProvider? retentionPolicy = null,
        IFavoriteFileCachePolicyProvider? favoriteFileCachePolicy = null,
        ClipboardCoreClient? syncCore = null,
        ISyncCredentialStore? credentials = null,
        IGlobalLog? globalLog = null)
    {
        _store = store;
        _retention = retention;
        _shortcuts = shortcuts;
        _startup = startup;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retentionPolicy = retentionPolicy;
        _favoriteFileCachePolicy = favoriteFileCachePolicy;
        _syncCore = syncCore;
        _credentials = credentials;
        _globalLog = globalLog;
    }

    public bool MaxRegularItemsEnabled
    {
        get => _maxRegularItemsEnabled;
        set => SetProperty(ref _maxRegularItemsEnabled, value);
    }

    public int MaxRegularItems
    {
        get => _maxRegularItems;
        set => SetProperty(ref _maxRegularItems, value);
    }

    public bool MaxAgeDaysEnabled
    {
        get => _maxAgeDaysEnabled;
        set => SetProperty(ref _maxAgeDaysEnabled, value);
    }

    public uint MaxAgeDays
    {
        get => _maxAgeDays;
        set => SetProperty(ref _maxAgeDays, value);
    }

    public bool MaxImageGiBEnabled
    {
        get => _maxImageGiBEnabled;
        set => SetProperty(ref _maxImageGiBEnabled, value);
    }

    public double MaxImageGiB
    {
        get => _maxImageGiB;
        set => SetProperty(ref _maxImageGiB, value);
    }

    public bool MaxFavoriteFileCacheGiBEnabled
    {
        get => _maxFavoriteFileCacheGiBEnabled;
        set => SetProperty(ref _maxFavoriteFileCacheGiBEnabled, value);
    }

    public double MaxFavoriteFileCacheGiB
    {
        get => _maxFavoriteFileCacheGiB;
        set => SetProperty(ref _maxFavoriteFileCacheGiB, value);
    }

    public bool InterceptWinV
    {
        get => _interceptWinV;
        set => SetProperty(ref _interceptWinV, value);
    }

    public string FallbackHotkey
    {
        get => _fallbackHotkey;
        set => SetProperty(ref _fallbackHotkey, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public string Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool SyncEnabled { get => _syncEnabled; set => SetProperty(ref _syncEnabled, value); }
    public string SyncProvider { get => _syncProvider; set => SetProperty(ref _syncProvider, value); }
    public string SyncEndpoint { get => _syncEndpoint; set => SetProperty(ref _syncEndpoint, value); }
    public string SyncRootPath { get => _syncRootPath; set => SetProperty(ref _syncRootPath, value); }
    public string SyncBucket { get => _syncBucket; set => SetProperty(ref _syncBucket, value); }
    public string SyncRegion { get => _syncRegion; set => SetProperty(ref _syncRegion, value); }
    public string SyncPrefix { get => _syncPrefix; set => SetProperty(ref _syncPrefix, value); }
    public string SyncAccount { get => _syncAccount; set => SetProperty(ref _syncAccount, value); }
    public string SyncSecret { get => _syncSecret; set => SetProperty(ref _syncSecret, value); }
    public string SyncStatus { get => _syncStatus; private set => SetProperty(ref _syncStatus, value); }
    public string LoggingLevel { get => _loggingLevel; set => SetProperty(ref _loggingLevel, value); }
    public int LoggingRetentionDays { get => _loggingRetentionDays; set => SetProperty(ref _loggingRetentionDays, value); }
    public ulong LoggingMaxSizeBytes { get => _loggingMaxSizeBytes; private set => SetProperty(ref _loggingMaxSizeBytes, value); }
    public LoggingSettings CurrentLoggingSettings => new(LoggingLevel, LoggingRetentionDays, LoggingMaxSizeBytes);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ClientSettings settings = await _store.LoadAsync(cancellationToken);
        Apply(settings);
        _persisted = settings;
        _retentionPolicy?.Update(settings);
        _favoriteFileCachePolicy?.Update(settings);
        _globalLog?.ApplySettings(settings.Logging ?? LoggingSettings.Default);
        ErrorMessage = null;
    }

    public Task<bool> SaveAsync(CancellationToken cancellationToken = default) =>
        SaveSettingsAsync(_persisted.Sync, cancellationToken);

    private async Task<bool> SaveSettingsAsync(
        SyncSettings? sync,
        CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        _globalLog?.Write(LogLevel.Debug, "settings", "settings.save.start");
        ClientSettings candidate;
        HotkeyChord chord;
        try
        {
            candidate = BuildSettings(sync);
            candidate.Validate();
            chord = HotkeyChord.Parse(candidate.FallbackHotkey);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            ErrorMessage = "设置值无效。";
            return false;
        }

        bool startupChanged = candidate.StartWithWindows != _persisted.StartWithWindows;
        if (startupChanged)
        {
            try
            {
                await _startup.SetEnabledAsync(candidate.StartWithWindows, cancellationToken);
            }
            catch
            {
                StartWithWindows = _persisted.StartWithWindows;
                ErrorMessage = "无法更新开机启动设置。";
                return false;
            }
        }

        try
        {
            await _store.SaveAsync(candidate, cancellationToken);
            await _retention.ApplyRetentionAsync(
                new ApplyRetentionRequestDto(
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    new RetentionPolicyDto(
                        candidate.MaxRegularItems,
                        candidate.MaxAgeDays,
                        candidate.MaxImageBytes)),
                cancellationToken);
            _shortcuts.Configure(candidate.InterceptWinV, chord);
            _persisted = candidate;
            _retentionPolicy?.Update(candidate);
            _favoriteFileCachePolicy?.Update(candidate);
            _globalLog?.ApplySettings(candidate.Logging ?? LoggingSettings.Default);
            _globalLog?.Write(LogLevel.Info, "settings", "settings.save.end", new Dictionary<string, string>
            {
                ["status"] = "success",
            });
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (startupChanged)
            {
                try
                {
                    await _startup.SetEnabledAsync(_persisted.StartWithWindows, CancellationToken.None);
                }
                catch
                {
                }
                StartWithWindows = _persisted.StartWithWindows;
            }
            ErrorMessage = "无法保存或应用设置。";
            _globalLog?.Write(LogLevel.Error, "settings", "settings.save.end", new Dictionary<string, string>
            {
                ["status"] = "failure",
                ["error_category"] = "settings",
            });
            return false;
        }
    }

    private ClientSettings BuildSettings(SyncSettings? sync)
    {
        ulong? maxImageBytes = null;
        if (MaxImageGiBEnabled)
        {
            if (!double.IsFinite(MaxImageGiB) || MaxImageGiB <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxImageGiB));
            }
            maxImageBytes = checked((ulong)Math.Round(
                MaxImageGiB * ClientSettings.BytesPerGiB,
                MidpointRounding.AwayFromZero));
        }
        ulong? maxFavoriteFileCacheBytes = null;
        if (MaxFavoriteFileCacheGiBEnabled)
        {
            if (!double.IsFinite(MaxFavoriteFileCacheGiB) || MaxFavoriteFileCacheGiB <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxFavoriteFileCacheGiB));
            }
            maxFavoriteFileCacheBytes = checked((ulong)Math.Round(
                MaxFavoriteFileCacheGiB * ClientSettings.BytesPerGiB,
                MidpointRounding.AwayFromZero));
        }
        return new ClientSettings(
            MaxRegularItemsEnabled ? MaxRegularItems : null,
            MaxAgeDaysEnabled ? MaxAgeDays : null,
            maxImageBytes,
            InterceptWinV,
            FallbackHotkey,
            StartWithWindows,
            Theme,
            maxFavoriteFileCacheBytes,
            sync,
            CurrentLoggingSettings);
    }

    private void Apply(ClientSettings settings)
    {
        MaxRegularItemsEnabled = settings.MaxRegularItems.HasValue;
        MaxRegularItems = settings.MaxRegularItems ?? 1000;
        MaxAgeDaysEnabled = settings.MaxAgeDays.HasValue;
        MaxAgeDays = settings.MaxAgeDays ?? 30;
        MaxImageGiBEnabled = settings.MaxImageBytes.HasValue;
        MaxImageGiB = settings.MaxImageBytes.HasValue
            ? settings.MaxImageBytes.Value / (double)ClientSettings.BytesPerGiB
            : 1;
        MaxFavoriteFileCacheGiBEnabled = settings.MaxFavoriteFileCacheBytes.HasValue;
        MaxFavoriteFileCacheGiB = settings.MaxFavoriteFileCacheBytes.HasValue
            ? settings.MaxFavoriteFileCacheBytes.Value / (double)ClientSettings.BytesPerGiB
            : ClientSettings.DefaultMaxFavoriteFileCacheBytes / (double)ClientSettings.BytesPerGiB;
        InterceptWinV = settings.InterceptWinV;
        FallbackHotkey = settings.FallbackHotkey;
        StartWithWindows = settings.StartWithWindows;
        Theme = settings.Theme;
        SyncSettings? sync = settings.Sync;
        SyncEnabled = sync?.Enabled == true;
        SyncProvider = sync?.Provider ?? "webdav";
        SyncEndpoint = sync?.Endpoint ?? "https://";
        SyncRootPath = sync?.RootPath ?? "/";
        SyncBucket = sync?.Bucket ?? string.Empty;
        SyncRegion = sync?.Region ?? "cn-hangzhou";
        SyncPrefix = sync?.Prefix ?? "clipboard";
        SyncAccount = string.Empty;
        SyncSecret = string.Empty;
        SyncStatus = string.Empty;
        LoggingSettings logging = settings.Logging ?? LoggingSettings.Default;
        LoggingLevel = logging.Level;
        LoggingRetentionDays = logging.RetentionDays;
        LoggingMaxSizeBytes = logging.MaxSizeBytes;
    }

    public async Task<bool> SaveSyncAsync(CancellationToken cancellationToken = default)
    {
        SyncSettings? sync = BuildSyncSettings();
        if (sync is null)
        {
            bool disabledSyncSaved = await SaveSettingsAsync(null, cancellationToken);
            if (disabledSyncSaved)
            {
                SyncStatus = "同步设置已保存。";
            }
            return disabledSyncSaved;
        }
        try
        {
            ClientSettings candidate = BuildSettings(sync);
            candidate.Validate();
            _ = HotkeyChord.Parse(candidate.FallbackHotkey);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            ErrorMessage = "设置值无效。";
            return false;
        }
        if (_credentials is null)
        {
            ErrorMessage = "请输入同步账号和凭据。";
            return false;
        }

        SyncCredentials? previousCredentials;
        SyncCredentials credentials;
        bool updateCredentials;
        try
        {
            previousCredentials = await _credentials.LoadAsync(sync.CredentialProfileId!, cancellationToken);
            bool accountProvided = !string.IsNullOrWhiteSpace(SyncAccount);
            bool secretProvided = !string.IsNullOrWhiteSpace(SyncSecret);
            if (accountProvided != secretProvided)
            {
                ErrorMessage = "请输入完整的同步账号和凭据。";
                return false;
            }
            credentials = accountProvided
                ? new SyncCredentials(SyncAccount, SyncSecret)
                : previousCredentials ?? throw new InvalidOperationException();
            updateCredentials = accountProvided;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ErrorMessage = "请输入同步账号和凭据。";
            return false;
        }

        if (updateCredentials)
        {
            try
            {
                await _credentials.SaveAsync(sync.CredentialProfileId!, credentials, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ErrorMessage = "无法保存同步凭据。";
                return false;
            }
        }

        bool saved = await SaveSettingsAsync(sync, cancellationToken);
        if (!saved && updateCredentials)
        {
            try
            {
                if (previousCredentials is null)
                {
                    await _credentials.DeleteAsync(sync.CredentialProfileId!, CancellationToken.None);
                }
                else
                {
                    await _credentials.SaveAsync(
                        sync.CredentialProfileId!,
                        previousCredentials,
                        CancellationToken.None);
                }
            }
            catch
            {
            }
        }
        if (saved)
        {
            SyncStatus = "同步设置已保存。";
        }
        return saved;
    }

    public async Task<bool> SaveLoggingLevelAsync(
        string level,
        CancellationToken cancellationToken = default)
    {
        if (level is not ("trace" or "debug" or "info" or "warn" or "error"))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }
        LoggingLevel = level;
        return await SaveAsync(cancellationToken);
    }

    public async Task ProbeSyncAsync(CancellationToken cancellationToken = default)
    {
        _globalLog?.Write(LogLevel.Info, "sync", "sync.probe.start", new Dictionary<string, string>
        {
            ["provider"] = SyncProvider,
        });
        try
        {
            RemoteConfigDto remote = await BuildRemoteAsync(cancellationToken);
            await (_syncCore ?? throw new InvalidOperationException()).ProbeRemoteAsync(new ProbeRemoteRequestDto(remote), cancellationToken);
            SyncStatus = "连接成功。";
            _globalLog?.Write(LogLevel.Info, "sync", "sync.probe.end", new Dictionary<string, string>
            {
                ["provider"] = SyncProvider,
                ["status"] = "success",
            });
        }
        catch
        {
            SyncStatus = "连接失败。";
            _globalLog?.Write(LogLevel.Warn, "sync", "sync.probe.end", new Dictionary<string, string>
            {
                ["provider"] = SyncProvider,
                ["status"] = "failure",
                ["error_category"] = "remote",
            });
        }
    }

    public async Task RunSyncAsync(CancellationToken cancellationToken = default)
    {
        _globalLog?.Write(LogLevel.Info, "sync", "sync.remote.start", new Dictionary<string, string>
        {
            ["provider"] = SyncProvider,
        });
        try
        {
            RemoteConfigDto remote = await BuildRemoteAsync(cancellationToken);
            SyncSettings sync = BuildSyncSettings() ?? throw new InvalidOperationException();
            SyncResponseDto response = await (_syncCore ?? throw new InvalidOperationException()).SyncRemoteAsync(
                new SyncRemoteRequestDto(Guid.Parse(sync.DeviceId), remote), cancellationToken);
            SyncStatus = $"已同步：拉取 {response.Pulled}，合并 {response.Merged}，上传 {response.Uploaded}。";
            _globalLog?.Write(LogLevel.Info, "sync", "sync.remote.end", new Dictionary<string, string>
            {
                ["provider"] = SyncProvider,
                ["status"] = "success",
                ["count"] = (response.Pulled + response.Merged + response.Uploaded).ToString(),
            });
        }
        catch
        {
            SyncStatus = "同步失败。";
            _globalLog?.Write(LogLevel.Error, "sync", "sync.remote.end", new Dictionary<string, string>
            {
                ["provider"] = SyncProvider,
                ["status"] = "failure",
                ["error_category"] = "remote",
            });
        }
    }

    private SyncSettings? BuildSyncSettings() => !SyncEnabled ? null : new(
        true, SyncProvider, SyncEndpoint, SyncRootPath, SyncBucket, SyncRegion, SyncPrefix,
        _persisted.Sync?.DeviceId ?? Guid.NewGuid().ToString("D"),
        _persisted.Sync?.CredentialProfileId ?? Guid.NewGuid().ToString("N"));

    private async Task<RemoteConfigDto> BuildRemoteAsync(CancellationToken cancellationToken)
    {
        SyncSettings sync = BuildSyncSettings() ?? throw new InvalidOperationException();
        sync.Validate();
        SyncCredentials? saved = _credentials is null ? null : await _credentials.LoadAsync(sync.CredentialProfileId!, cancellationToken);
        string account = string.IsNullOrWhiteSpace(SyncAccount) ? saved?.Account ?? string.Empty : SyncAccount;
        string secret = string.IsNullOrWhiteSpace(SyncSecret) ? saved?.Secret ?? string.Empty : SyncSecret;
        if (sync.Provider == "oss")
        {
            return new RemoteConfigDto(
                "oss",
                1,
                sync.Endpoint,
                Region: sync.Region,
                Bucket: sync.Bucket,
                Prefix: sync.Prefix,
                AccessKeyId: account,
                AccessKeySecret: secret);
        }

        Uri endpoint = new(sync.Endpoint.TrimEnd('/') + "/");
        Uri remoteEndpoint = new(endpoint, sync.RootPath!.TrimStart('/'));
        if (remoteEndpoint.Scheme != endpoint.Scheme ||
            remoteEndpoint.Host != endpoint.Host ||
            remoteEndpoint.Port != endpoint.Port)
        {
            throw new InvalidOperationException();
        }
        return new RemoteConfigDto(
            "webdav",
            1,
            remoteEndpoint.ToString(),
            Username: account,
            Password: secret);
    }
}
