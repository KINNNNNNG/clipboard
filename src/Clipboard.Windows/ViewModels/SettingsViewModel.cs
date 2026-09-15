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
    private readonly IRealtimeSyncSettingsNotifier? _syncSettingsNotifier;
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
    private readonly IUpdateService? _updateService;
    private readonly IUpdateInstallerLauncher? _installerLauncher;
    private readonly Action? _requestExit;
    private bool _updateCheckOnStartup;
    private string? _skippedUpdateVersion;
    private string? _latestVersion;
    private string? _pendingInstallerUrl;
    private string? _pendingChecksumsUrl;
    private string _updateStatus = string.Empty;
    private bool _updateAvailable;
    private bool _updateBusy;
    private string _snapshotDirectory = string.Empty;
    private int _snapshotIntervalMinutes = SnapshotPolicy.DefaultIntervalMinutes;
    private int _snapshotKeep = SnapshotPolicy.DefaultKeep;

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
        IGlobalLog? globalLog = null,
        IRealtimeSyncSettingsNotifier? syncSettingsNotifier = null,
        IUpdateService? updateService = null,
        IUpdateInstallerLauncher? installerLauncher = null,
        Action? requestExit = null,
        string? currentVersion = null)
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
        _syncSettingsNotifier = syncSettingsNotifier;
        _updateService = updateService;
        _installerLauncher = installerLauncher;
        _requestExit = requestExit;
        CurrentVersion = currentVersion ?? ApplicationVersion.Current;
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

    /// <summary>
    /// Version of the running client; update checks compare against this value.
    /// </summary>
    public string CurrentVersion { get; }

    public bool UpdateCheckOnStartup
    {
        get => _updateCheckOnStartup;
        set => SetProperty(ref _updateCheckOnStartup, value);
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (SetProperty(ref _updateAvailable, value))
            {
                OnPropertyChanged(nameof(CanInstallUpdate));
                OnPropertyChanged(nameof(CanSkipUpdate));
            }
        }
    }

    public bool CanCheckForUpdates => _updateService is not null && !_updateBusy;

    public bool CanInstallUpdate =>
        _updateAvailable && _installerLauncher is not null && !_updateBusy;

    public bool CanSkipUpdate => _updateAvailable && !_updateBusy;

    /// <summary>
    /// Reports whether the startup check should run for this session.
    /// </summary>
    public bool ShouldCheckForUpdatesOnStartup => _updateCheckOnStartup && _updateService is not null;

    /// <summary>
    /// Snapshot location; an empty value means the default directory next to the data directory.
    /// </summary>
    public string SnapshotDirectory
    {
        get => _snapshotDirectory;
        set => SetProperty(ref _snapshotDirectory, value);
    }

    public int SnapshotIntervalMinutes
    {
        get => _snapshotIntervalMinutes;
        set => SetProperty(ref _snapshotIntervalMinutes, value);
    }

    public int SnapshotKeep
    {
        get => _snapshotKeep;
        set => SetProperty(ref _snapshotKeep, value);
    }

    /// <summary>
    /// Asks the client to rebuild local history from the configured remote on the next start.
    /// </summary>
    /// <remarks>
    /// The local database is quarantined on the next start, because the running client holds it
    /// open; the fresh database then pulls the remote history through the normal startup sync.
    /// </remarks>
    /// <summary>Starts a fresh client instance so the pending restore can run.</summary>
    public bool RestartClient() => ClientRestarter.Restart(() => _requestExit?.Invoke());

    public Task<bool> RequestRemoteHistoryRestoreAsync(
        CancellationToken cancellationToken = default) =>
        RequestRemoteHistoryRestoreAsync(SnapshotPolicy.LocalRoot, cancellationToken);

    internal Task<bool> RequestRemoteHistoryRestoreAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_persisted.Sync?.Enabled != true)
        {
            ErrorMessage = "请先启用并保存同步设置，再从远端恢复历史。";
            return Task.FromResult(false);
        }
        try
        {
            VaultRestoreRequest.Write(root);
            return Task.FromResult(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "无法写入恢复请求，请检查数据目录权限。";
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Asks the release feed whether a newer version is published.
    /// </summary>
    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (_updateService is null || _updateBusy)
        {
            return;
        }

        SetUpdateBusy(true);
        try
        {
            UpdateStatus = "正在检查更新…";
            UpdateCheckResponseDto response = await _updateService.CheckUpdateAsync(
                new CheckUpdateRequestDto(CurrentVersion, IncludePrerelease: false),
                cancellationToken);
            _latestVersion = response.LatestVersion;
            _pendingInstallerUrl = response.InstallerUrl;
            _pendingChecksumsUrl = response.ChecksumsUrl;
            bool skipped = _skippedUpdateVersion is not null
                && string.Equals(
                    _skippedUpdateVersion,
                    response.LatestVersion,
                    StringComparison.Ordinal);
            UpdateAvailable = response.Available && !skipped;
            UpdateStatus = response.Available
                ? skipped
                    ? $"已跳过版本 {response.LatestVersion}。"
                    : $"发现新版本 {response.LatestVersion}。"
                : "当前已是最新版本。";
            WriteUpdateLog("update.check.end", UpdateAvailable ? "available" : "current");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClipboardCoreException error)
        {
            UpdateAvailable = false;
            UpdateStatus = CoreStatusMessages.ForUpdate(error.Status);
            WriteUpdateLog("update.check.failed", CoreStatusMessages.ForLog(error.Status));
        }
        catch
        {
            UpdateAvailable = false;
            UpdateStatus = "检查更新失败，请稍后重试。";
            WriteUpdateLog("update.check.failed", "unexpected");
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    /// <summary>
    /// Downloads the verified installer and hands the session over to it.
    /// </summary>
    public async Task DownloadAndInstallUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_updateService is null || _installerLauncher is null || _updateBusy)
        {
            return;
        }
        if (!_updateAvailable
            || _latestVersion is null
            || _pendingInstallerUrl is null
            || _pendingChecksumsUrl is null)
        {
            UpdateStatus = "更新信息不完整，请重新检查更新。";
            return;
        }

        SetUpdateBusy(true);
        try
        {
            UpdateStatus = $"正在下载 {_latestVersion}…";
            UpdateDownloadResponseDto response = await _updateService.DownloadUpdateAsync(
                new DownloadUpdateRequestDto(
                    _latestVersion,
                    _pendingInstallerUrl,
                    _pendingChecksumsUrl,
                    UpdatesDirectory),
                cancellationToken);
            if (!_installerLauncher.Launch(response.InstallerPath))
            {
                UpdateStatus = "无法启动安装程序，请手动运行已下载的安装包。";
                WriteUpdateLog("update.install.failed", "launch");
                return;
            }

            UpdateStatus = "安装程序已启动，客户端即将退出并完成更新。";
            WriteUpdateLog("update.install.start", "ok");
            _requestExit?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClipboardCoreException error)
        {
            UpdateStatus = CoreStatusMessages.ForUpdate(error.Status);
            WriteUpdateLog("update.download.failed", CoreStatusMessages.ForLog(error.Status));
        }
        catch
        {
            UpdateStatus = "更新失败，请稍后重试。";
            WriteUpdateLog("update.download.failed", "unexpected");
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    /// <summary>
    /// Remembers the offered version so later checks stay quiet about it.
    /// </summary>
    public async Task SkipUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_latestVersion is null)
        {
            return;
        }
        _skippedUpdateVersion = _latestVersion;
        UpdateAvailable = false;
        UpdateStatus = $"已跳过版本 {_latestVersion}。";
        await SaveAsync(cancellationToken);
    }

    internal static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clipboard",
        "updates");

    private void SetUpdateBusy(bool busy)
    {
        if (_updateBusy == busy)
        {
            return;
        }
        _updateBusy = busy;
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanInstallUpdate));
        OnPropertyChanged(nameof(CanSkipUpdate));
    }

    private void WriteUpdateLog(string eventName, string status) =>
        _globalLog?.Write(
            LogLevel.Info,
            "update",
            eventName,
            new Dictionary<string, string> { ["status"] = status });

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
            CurrentLoggingSettings,
            UpdateCheckOnStartup,
            _skippedUpdateVersion,
            string.IsNullOrWhiteSpace(SnapshotDirectory) ? null : SnapshotDirectory.Trim(),
            SnapshotIntervalMinutes,
            SnapshotKeep);
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
        UpdateCheckOnStartup = settings.UpdateCheckOnStartup;
        _skippedUpdateVersion = settings.SkippedUpdateVersion;
        SnapshotDirectory = settings.SnapshotDirectory ?? string.Empty;
        SnapshotIntervalMinutes = settings.SnapshotIntervalMinutes;
        SnapshotKeep = settings.SnapshotKeep;
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
                _syncSettingsNotifier?.OnSyncSettingsSaved(false);
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
            _syncSettingsNotifier?.OnSyncSettingsSaved(sync.Enabled);
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
        await WriteLogAsync(LogLevel.Info, "sync", "sync.probe.start", new Dictionary<string, string>
        {
            ["provider"] = SyncProvider,
        }, cancellationToken);
        try
        {
            RemoteConfigDto remote = await BuildRemoteAsync(cancellationToken);
            RemoteProbeResponseDto response = await (_syncCore ?? throw new InvalidOperationException()).ProbeRemoteAsync(
                new ProbeRemoteRequestDto(remote),
                cancellationToken);
            if (!response.Available)
            {
                string category = response.ErrorCategory ?? "remote";
                string? code = response.ErrorCode;
                string? detail = SanitizeRemoteErrorDetail(response.ErrorDetail);
                string? display = code ?? FormatRemoteErrorDetail(detail);
                SyncStatus = display is null ? "连接失败。" : $"连接失败：{display}。";
                var fields = new Dictionary<string, string>
                {
                    ["provider"] = SyncProvider,
                    ["status"] = "failure",
                    ["error_category"] = category,
                };
                if (code is not null)
                {
                    fields["error_code"] = code;
                }
                if (detail is not null)
                {
                    fields["error_detail"] = detail;
                }
                await WriteLogAsync(LogLevel.Warn, "sync", "sync.probe.end", fields, cancellationToken);
                return;
            }
            SyncStatus = "连接成功。";
            await WriteLogAsync(LogLevel.Info, "sync", "sync.probe.end", new Dictionary<string, string>
            {
                ["provider"] = SyncProvider,
                ["status"] = "success",
            }, cancellationToken);
        }
        catch (ClipboardCoreException error)
        {
            SyncStatus = "连接失败。";
            await WriteLogAsync(LogLevel.Warn, "sync", "sync.probe.end", new Dictionary<string, string>
            {
                ["provider"] = SyncProvider,
                ["status"] = "failure",
                ["error_category"] = CoreStatusMessages.ForLog(error.Status),
            }, cancellationToken);
        }
        catch
        {
            SyncStatus = "连接失败。";
            await WriteLogAsync(LogLevel.Warn, "sync", "sync.probe.end", new Dictionary<string, string>
            {
                ["provider"] = SyncProvider,
                ["status"] = "failure",
                ["error_category"] = "remote",
            }, cancellationToken);
        }
    }

    public async Task RunSyncAsync(CancellationToken cancellationToken = default)
    {
        await WriteLogAsync(LogLevel.Info, "sync", "sync.remote.start", new Dictionary<string, string>
        {
            ["provider"] = SyncProvider,
        }, cancellationToken);
        try
        {
            RemoteConfigDto remote = await BuildRemoteAsync(cancellationToken);
            SyncSettings sync = BuildSyncSettings() ?? throw new InvalidOperationException();
            SyncResponseDto response = await (_syncCore ?? throw new InvalidOperationException()).SyncRemoteAsync(
                new SyncRemoteRequestDto(Guid.Parse(sync.DeviceId), remote), cancellationToken);
            if (response.ErrorCategory is not null)
            {
                string? detail = SanitizeRemoteErrorDetail(response.ErrorDetail);
                string? display = response.ErrorCode ?? FormatRemoteErrorDetail(detail);
                SyncStatus = display is null ? "同步失败。" : $"同步失败：{display}。";
                var fields = new Dictionary<string, string>
                {
                    ["provider"] = SyncProvider,
                    ["status"] = "failure",
                    ["error_category"] = response.ErrorCategory,
                };
                if (response.ErrorCode is not null)
                {
                    fields["error_code"] = response.ErrorCode;
                }
                if (detail is not null)
                {
                    fields["error_detail"] = detail;
                }
                if (response.ErrorOperation is not null)
                {
                    fields["error_operation"] = response.ErrorOperation;
                }
                await WriteLogAsync(LogLevel.Error, "sync", "sync.remote.end", fields, cancellationToken);
                return;
            }
            SyncStatus = $"已同步：拉取 {response.Pulled}，合并 {response.Merged}，上传 {response.Uploaded}。";
            await WriteLogAsync(LogLevel.Info, "sync", "sync.remote.end", new Dictionary<string, string>
            {
                ["provider"] = SyncProvider,
                ["status"] = "success",
                ["count"] = (response.Pulled + response.Merged + response.Uploaded).ToString(),
            }, cancellationToken);
        }
        catch (ClipboardCoreException error)
        {
            SyncStatus = "同步失败。";
            await WriteLogAsync(LogLevel.Error, "sync", "sync.remote.end", new Dictionary<string, string>
            {
                ["provider"] = SyncProvider,
                ["status"] = "failure",
                ["error_category"] = CoreStatusMessages.ForLog(error.Status),
            }, cancellationToken);
        }
        catch
        {
            SyncStatus = "同步失败。";
            await WriteLogAsync(LogLevel.Error, "sync", "sync.remote.end", new Dictionary<string, string>
            {
                ["provider"] = SyncProvider,
                ["status"] = "failure",
                ["error_category"] = "remote",
            }, cancellationToken);
        }
    }

    private async Task WriteLogAsync(
        LogLevel level,
        string component,
        string eventName,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        if (_globalLog is null)
        {
            return;
        }
        _globalLog.Write(level, component, eventName, fields);
        await _globalLog.FlushAsync(cancellationToken);
    }

    private static string? FormatRemoteErrorDetail(string? detail) => SanitizeRemoteErrorDetail(detail) switch
    {
        null => null,
        var value when value.StartsWith("http_", StringComparison.Ordinal) =>
            $"HTTP {value[5..]}",
        "network_timeout" => "网络超时",
        "network_connect" => "网络连接失败",
        "network_request" => "网络请求失败",
        _ => null,
    };

    private static string? SanitizeRemoteErrorDetail(string? detail) => detail switch
    {
        "network_timeout" or "network_connect" or "network_request" => detail,
        { Length: 8 } when detail.StartsWith("http_", StringComparison.Ordinal) &&
            detail[5] is >= '0' and <= '9' &&
            detail[6] is >= '0' and <= '9' &&
            detail[7] is >= '0' and <= '9' => detail,
        _ => null,
    };

    private SyncSettings? BuildSyncSettings() => !SyncEnabled ? null : new(
        true, SyncProvider, SyncEndpoint, SyncRootPath, SyncBucket, SyncRegion, SyncPrefix,
        _persisted.Sync?.DeviceId ?? Guid.NewGuid().ToString("D"),
        _persisted.Sync?.CredentialProfileId ?? Guid.NewGuid().ToString("N"));

    private async Task<RemoteConfigDto> BuildRemoteAsync(CancellationToken cancellationToken)
    {
        SyncSettings sync = BuildSyncSettings() ?? throw new InvalidOperationException();
        return await RemoteSyncRequestFactory.CreateAsync(
            sync,
            _credentials ?? throw new InvalidOperationException(),
            SyncAccount,
            SyncSecret,
            cancellationToken);
    }
}
