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
    private bool _showOnLaunch;

    public App()
    {
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
            _vault = new VaultBootstrapper().LoadOrCreate();
            _core = ClipboardCoreClient.Open(
                _vault.DataDirectory,
                _vault.VaultId,
                _vault.VaultKey);
            _vault.Dispose();
            _vault = null;

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
                new CompositeCaptureObserver(MainWindow, _realtimeSync),
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
                syncSettingsNotifier: _realtimeSync);
            await _settingsViewModel.LoadAsync();
            ApplyTheme(_settingsViewModel.Theme);
            _shortcuts.Configure(
                _settingsViewModel.InterceptWinV,
                HotkeyChord.Parse(_settingsViewModel.FallbackHotkey));
            MainWindow.SetShortcutState(_shortcuts.State);
            _capture.Start(dispatcher);

            nint handle = WindowNative.GetWindowHandle(MainWindow);
            _tray = new TrayIconService(
                handle,
                MainWindow.ShowPanel,
                OpenSettings,
                OpenLogs,
                ExitApplication);
            _tray.Start();
            if (_showOnLaunch)
            {
                MainWindow.ShowPanel();
            }
            else
            {
                _presenter.Hide();
            }
        }
        catch
        {
            _globalLog?.Write(LogLevel.Error, "app", "app.exception", new Dictionary<string, string>
            {
                ["error_category"] = "initialization",
            });
            MainWindow.SetStatus("无法初始化剪贴板服务");
        }
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
        _tray?.Dispose();
        _capture?.Dispose();
        _shortcuts?.Dispose();
        DisposeRealtimeSyncAsync().GetAwaiter().GetResult();
        _core?.Dispose();
        _vault?.Dispose();
        _globalLog?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
