using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;
using Clipboard.Windows.ViewModels;
using Clipboard.Windows.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Clipboard.Windows;

public partial class App : Microsoft.UI.Xaml.Application
{
    public static MainWindow? MainWindow { get; private set; }

    private ClipboardCoreClient? _core;
    private VaultMaterial? _vault;
    private ClipboardCaptureCoordinator? _capture;
    private GlobalShortcutService? _shortcuts;
    private TrayIconService? _tray;
    private WindowPresenter? _presenter;
    private SettingsViewModel? _settingsViewModel;
    private FileGlobalLog? _globalLog;
    private RealtimeSyncCoordinator? _realtimeSync;
    private readonly ClientSettingsStore _settingsStore = new();
    private readonly SingleWindowLifetime<SettingsWindow> _settingsWindows = new();
    private readonly SingleWindowLifetime<LogWindow> _logWindows = new();
    private readonly PreviousInstanceCloser _previousInstanceCloser = new();
    private bool _showOnLaunch;
    private string? _startupNotice;
    private readonly IVaultSnapshotService _snapshotService = new VaultSnapshotService();
    private ClientSettings _snapshotSettings = ClientSettings.Default;
    private DispatcherQueueTimer? _snapshotTimer;

    public App()
    {
        _previousInstanceCloser.Close();
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (MainWindow is not null)
        {
            MainWindow.ShowPanel();
            return;
        }

        MainWindow = new MainWindow();
        _showOnLaunch = args.Arguments.Contains("--show", StringComparison.OrdinalIgnoreCase)
            || Environment.GetCommandLineArgs().Contains(
                "--show",
                StringComparer.OrdinalIgnoreCase);
        MainWindow.Closed += (_, _) => OnExit(this, EventArgs.Empty);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (MainWindow is null)
        {
            return;
        }
        try
        {
            DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("Unable to access the UI dispatcher.");
            _globalLog = new FileGlobalLog(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clipboard",
                "logs"));
            _globalLog.Write(LogLevel.Info, "app", "app.start");

            // The tray icon is the only always-available entry point, so create it before
            // anything that can fail: a core failure must not leave an invisible process.
            nint trayHandle = WindowNative.GetWindowHandle(MainWindow);
            _tray = new TrayIconService(
                trayHandle,
                MainWindow.ShowPanel,
                OpenSettings,
                OpenLogs,
                ExitApplication);
            _tray.Start();

            _vault = new VaultBootstrapper().LoadOrCreate();
            _snapshotSettings = await _settingsStore.LoadAsync();
            _core = OpenCoreWithRecovery(_vault, _snapshotSettings);

            _presenter = new WindowPresenter(MainWindow);
            var writer = new WindowsClipboardWriter();
            var reader = new WindowsClipboardReader();
            var suppression = new ClipboardSuppression();
            var retentionPolicy = new RetentionPolicyProvider();
            var favoriteFileCachePolicy = new FavoriteFileCachePolicyProvider();
            var credentials = new SyncCredentialStore();
            _realtimeSync = new RealtimeSyncCoordinator(
                _core,
                _settingsStore,
                credentials,
                globalLog: _globalLog);
            _realtimeSync.Start();
            var paste = new PasteCoordinator(
                _core,
                writer,
                new ForegroundWindowService(),
                suppression,
                MainWindow.HidePanel);
            var panel = new ClipboardPanelViewModel(
                _core,
                paste,
                favoriteFileCachePolicy: favoriteFileCachePolicy,
                realtimeSync: _realtimeSync);
            MainWindow.Configure(panel, _presenter, _core, writer);

            _capture = new ClipboardCaptureCoordinator(
                reader,
                _core,
                new SourceApplicationResolver(),
                suppression,
                new CompositeCaptureObserver(
                    MainWindow,
                    _realtimeSync,
                    new SnapshotWriteCounter(
                        SnapshotPolicy.WritesPerSnapshot,
                        () => CreateSnapshotQuietly("write-threshold"))),
                retentionPolicy: retentionPolicy);

            _shortcuts = new GlobalShortcutService(dispatcher, MainWindow.ShowPanel);
            _settingsViewModel = new SettingsViewModel(
                _settingsStore,
                _core,
                _shortcuts,
                new StartupService(),
                retentionPolicy: retentionPolicy,
                favoriteFileCachePolicy: favoriteFileCachePolicy,
                syncCore: _core,
                credentials: credentials,
                globalLog: _globalLog,
                syncSettingsNotifier: _realtimeSync,
                updateService: _core,
                installerLauncher: new UpdateInstallerLauncher(),
                requestExit: ExitApplication);
            await _settingsViewModel.LoadAsync();
            ApplyTheme(_settingsViewModel.Theme);
            if (_settingsViewModel.ShouldCheckForUpdatesOnStartup)
            {
                _ = CheckForUpdatesOnStartupAsync();
            }
            _shortcuts.Configure(
                _settingsViewModel.InterceptWinV,
                HotkeyChord.Parse(_settingsViewModel.FallbackHotkey));
            MainWindow.SetShortcutState(_shortcuts.State);
            _capture.Start(dispatcher);
            StartSnapshotTimer(dispatcher);

            if (_showOnLaunch)
            {
                MainWindow.ShowPanel();
            }
            else
            {
                _presenter.Hide();
            }
            if (_startupNotice is not null)
            {
                MainWindow.SetStatus(_startupNotice);
            }
        }
        catch (Exception error)
        {
            CoreStatus status = error is ClipboardCoreException coreError
                ? coreError.Status
                : CoreStatus.CoreError;
            _globalLog?.Write(LogLevel.Error, "app", "app.exception", new Dictionary<string, string>
            {
                ["error_category"] = error is ClipboardCoreException
                    ? CoreStatusMessages.ForLog(status)
                    : "initialization",
            });
            MainWindow.SetStatus(CoreStatusMessages.ForVaultOpen(status));
        }
    }

    /// <summary>
    /// Opens the clipboard core, moving a damaged history database aside so the client can start.
    /// </summary>
    /// <remarks>
    /// Only damage is recovered: a marker mismatch means the key belongs to another vault, so that
    /// is reported instead. The damaged file is preserved under a timestamped name.
    /// </remarks>
    private ClipboardCoreClient OpenCoreWithRecovery(VaultMaterial vault, ClientSettings settings)
    {
        try
        {
            return ClipboardCoreClient.Open(vault.DataDirectory, vault.VaultId, vault.VaultKey);
        }
        catch (ClipboardCoreException error) when (
            error.Status is CoreStatus.VaultUnreadable or CoreStatus.VaultCorrupt)
        {
            if (TryRestoreNewestSnapshot(vault, settings))
            {
                _globalLog?.Write(LogLevel.Warn, "app", "vault.restore", new Dictionary<string, string>
                {
                    ["error_category"] = CoreStatusMessages.ForLog(error.Status),
                    ["source"] = "snapshot",
                });
                _startupNotice = "剪贴板历史数据库已损坏，已从最近的快照恢复。";
                return ClipboardCoreClient.Open(vault.DataDirectory, vault.VaultId, vault.VaultKey);
            }

            string? quarantined = VaultRecovery.QuarantineHistory(
                vault.DataDirectory,
                DateTimeOffset.UtcNow);
            _globalLog?.Write(LogLevel.Warn, "app", "vault.rebuild", new Dictionary<string, string>
            {
                ["error_category"] = CoreStatusMessages.ForLog(error.Status),
                ["quarantine"] = quarantined is null ? "none" : "history",
            });
            _startupNotice = quarantined is null
                ? "剪贴板历史数据库无法解密，已重建为空库。"
                : "剪贴板历史数据库无法解密，已备份原文件并重建为空库。";
            return ClipboardCoreClient.Open(vault.DataDirectory, vault.VaultId, vault.VaultKey);
        }
    }

    /// <summary>
    /// Restores the newest verified snapshot, or returns false when none can be used.
    /// </summary>
    private bool TryRestoreNewestSnapshot(VaultMaterial vault, ClientSettings settings)
    {
        try
        {
            string root = SnapshotPolicy.ResolveDirectory(settings);
            foreach (VaultSnapshot snapshot in _snapshotService.List(vault, root))
            {
                if (_snapshotService.Restore(
                    vault,
                    snapshot.Directory,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                {
                    return true;
                }
            }
        }
        catch
        {
            // A failed restore falls back to rebuilding an empty database.
        }
        return false;
    }

    private void StartSnapshotTimer(DispatcherQueue dispatcher)
    {
        _snapshotTimer = dispatcher.CreateTimer();
        _snapshotTimer.Interval = TimeSpan.FromMinutes(_snapshotSettings.SnapshotIntervalMinutes);
        _snapshotTimer.Tick += (_, _) => CreateSnapshotQuietly("interval");
        _snapshotTimer.Start();
    }

    /// <summary>
    /// Writes a verified snapshot. Failures are logged and never disturb clipboard capture.
    /// </summary>
    private void CreateSnapshotQuietly(string trigger)
    {
        if (_vault is null)
        {
            return;
        }
        bool created;
        try
        {
            created = _snapshotService.Create(
                _vault,
                SnapshotPolicy.ResolveDirectory(_snapshotSettings),
                _snapshotSettings.SnapshotKeep) is not null;
        }
        catch
        {
            created = false;
        }
        _globalLog?.Write(LogLevel.Info, "app", "vault.snapshot", new Dictionary<string, string>
        {
            ["trigger"] = trigger,
            ["result"] = created ? "ok" : "failed",
        });
    }

    private async void OpenSettings()
    {
        if (_settingsViewModel is null || _presenter is null)
        {
            return;
        }
        SettingsWindow window = _settingsWindows.GetOrCreate(
            () => CreateSettingsWindow(_settingsViewModel),
            out bool created);
        if (!created)
        {
            window.Activate();
            return;
        }
        _presenter.IsSettingsWindowOpen = true;
        ApplyTheme(_settingsViewModel.Theme);
        try
        {
            await window.ShowAsync();
        }
        catch
        {
            _settingsWindows.Release(window);
            _presenter.IsSettingsWindowOpen = false;
            window.Close();
            MainWindow?.SetStatus("无法打开设置");
        }
    }

    private SettingsWindow CreateSettingsWindow(SettingsViewModel viewModel)
    {
        var window = new SettingsWindow(viewModel, ApplyTheme);
        window.Closed += (_, _) =>
        {
            if (_presenter is not null)
            {
                _presenter.IsSettingsWindowOpen = false;
            }
            _settingsWindows.Release(window);
        };
        return window;
    }

    private async void OpenLogs()
    {
        if (_settingsViewModel is null || _globalLog is null)
        {
            return;
        }
        LogWindow window = _logWindows.GetOrCreate(
            () => new LogWindow(new LogViewModel(
                _globalLog,
                _settingsViewModel.SaveLoggingLevelAsync)),
            out bool created);
        if (!created)
        {
            window.Activate();
            return;
        }
        window.Closed += (_, _) => _logWindows.Release(window);
        try
        {
            await window.ShowAsync();
        }
        catch
        {
            _logWindows.Release(window);
            window.Close();
            MainWindow?.SetStatus("无法打开日志");
        }
    }

    private void ExitApplication()
    {
        _settingsWindows.Current?.Close();
        _logWindows.Current?.Close();
        MainWindow?.Close();
    }

    /// <summary>
    /// Runs the optional startup update check without blocking clipboard capture.
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_settingsViewModel is null)
        {
            return;
        }
        try
        {
            await _settingsViewModel.CheckForUpdatesAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // A failed startup check must never disturb the clipboard client.
        }
    }

    private async Task DisposeRealtimeSyncAsync()
    {
        if (_realtimeSync is not null)
        {
            await _realtimeSync.DisposeAsync();
            _realtimeSync = null;
        }
    }

    private void ApplyTheme(string theme)
    {
        ElementTheme requestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (MainWindow?.Content is FrameworkElement mainRoot)
        {
            mainRoot.RequestedTheme = requestedTheme;
        }
        if (_settingsWindows.Current?.Content is FrameworkElement settingsRoot)
        {
            settingsRoot.RequestedTheme = requestedTheme;
        }
        if (_logWindows.Current?.Content is FrameworkElement logRoot)
        {
            logRoot.RequestedTheme = requestedTheme;
        }
    }

    private void OnExit(object sender, object args)
    {
        CreateSnapshotQuietly("exit");
        _tray?.Dispose();
        _capture?.Dispose();
        _shortcuts?.Dispose();
        DisposeRealtimeSyncAsync().GetAwaiter().GetResult();
        _core?.Dispose();
        _vault?.Dispose();
        _globalLog?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
