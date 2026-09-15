using Clipboard.Windows.Platform;
using Clipboard.Windows.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Clipboard.Windows.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Action<string> _applyTheme;

    internal SettingsWindow(SettingsViewModel viewModel, Action<string> applyTheme)
    {
        _viewModel = viewModel;
        _applyTheme = applyTheme;
        InitializeComponent();
        Root.DataContext = viewModel;
        ResizeWindow();
    }

    internal async Task ShowAsync()
    {
        await _viewModel.LoadAsync();
        SyncControlsFromViewModel();
        Activate();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            SyncViewModelFromControls();
        }
        catch (Exception error) when (error is ArgumentException or InvalidCastException or OverflowException)
        {
            ErrorText.Text = "请输入有效的保留上限。";
            return;
        }
        bool saved = await _viewModel.SaveAsync();
        ErrorText.Text = _viewModel.ErrorMessage ?? string.Empty;
        if (saved)
        {
            _applyTheme(_viewModel.Theme);
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs args) => Close();

    private void SyncControlsFromViewModel()
    {
        MaxItemsEnabledCheckBox.IsChecked = _viewModel.MaxRegularItemsEnabled;
        MaxItemsNumberBox.Value = _viewModel.MaxRegularItems;
        MaxAgeEnabledCheckBox.IsChecked = _viewModel.MaxAgeDaysEnabled;
        MaxAgeNumberBox.Value = _viewModel.MaxAgeDays;
        MaxImageEnabledCheckBox.IsChecked = _viewModel.MaxImageGiBEnabled;
        MaxImageNumberBox.Value = _viewModel.MaxImageGiB;
        MaxFavoriteFileCacheEnabledCheckBox.IsChecked = _viewModel.MaxFavoriteFileCacheGiBEnabled;
        MaxFavoriteFileCacheNumberBox.Value = _viewModel.MaxFavoriteFileCacheGiB;
        InterceptWinVToggle.IsOn = _viewModel.InterceptWinV;
        FallbackHotkeyTextBox.Text = _viewModel.FallbackHotkey;
        StartWithWindowsToggle.IsOn = _viewModel.StartWithWindows;
        ThemeComboBox.SelectedIndex = _viewModel.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        UpdateCheckOnStartupCheckBox.IsChecked = _viewModel.UpdateCheckOnStartup;
        SyncUpdateControls();
        ErrorText.Text = string.Empty;
        SyncEnabledToggle.IsOn = _viewModel.SyncEnabled;
        SyncProviderComboBox.SelectedIndex = _viewModel.SyncProvider == "oss" ? 1 : 0;
        UpdateSyncProviderVisibility();
        SyncEndpointTextBox.Text = _viewModel.SyncEndpoint;
        SyncRootPathTextBox.Text = _viewModel.SyncRootPath;
        SyncBucketTextBox.Text = _viewModel.SyncBucket;
        SyncRegionTextBox.Text = _viewModel.SyncRegion;
        SyncPrefixTextBox.Text = _viewModel.SyncPrefix;
        SyncAccountTextBox.Text = _viewModel.SyncAccount;
        SyncSecretPasswordBox.Password = _viewModel.SyncSecret;
        SyncStatusText.Text = _viewModel.SyncStatus;
    }

    private void SyncViewModelFromControls()
    {
        _viewModel.MaxRegularItemsEnabled = MaxItemsEnabledCheckBox.IsChecked == true;
        _viewModel.MaxRegularItems = checked((int)MaxItemsNumberBox.Value);
        _viewModel.MaxAgeDaysEnabled = MaxAgeEnabledCheckBox.IsChecked == true;
        _viewModel.MaxAgeDays = checked((uint)MaxAgeNumberBox.Value);
        _viewModel.MaxImageGiBEnabled = MaxImageEnabledCheckBox.IsChecked == true;
        _viewModel.MaxImageGiB = MaxImageNumberBox.Value;
        _viewModel.MaxFavoriteFileCacheGiBEnabled = MaxFavoriteFileCacheEnabledCheckBox.IsChecked == true;
        _viewModel.MaxFavoriteFileCacheGiB = MaxFavoriteFileCacheNumberBox.Value;
        _viewModel.InterceptWinV = InterceptWinVToggle.IsOn;
        _viewModel.FallbackHotkey = FallbackHotkeyTextBox.Text;
        _viewModel.StartWithWindows = StartWithWindowsToggle.IsOn;
        _viewModel.Theme = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? "system";
        _viewModel.UpdateCheckOnStartup = UpdateCheckOnStartupCheckBox.IsChecked == true;
        SyncViewModelFromSyncControls();
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs args)
    {
        SyncViewModelFromControls();
        await _viewModel.CheckForUpdatesAsync();
        SyncUpdateControls();
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs args)
    {
        await _viewModel.DownloadAndInstallUpdateAsync();
        SyncUpdateControls();
    }

    private async void SkipUpdateButton_Click(object sender, RoutedEventArgs args)
    {
        await _viewModel.SkipUpdateAsync();
        SyncUpdateControls();
    }

    private void SyncUpdateControls()
    {
        CurrentVersionText.Text = $"当前版本 {_viewModel.CurrentVersion}";
        UpdateStatusText.Text = _viewModel.UpdateStatus;
        CheckUpdateButton.IsEnabled = _viewModel.CanCheckForUpdates;
        InstallUpdateButton.IsEnabled = _viewModel.CanInstallUpdate;
        SkipUpdateButton.IsEnabled = _viewModel.CanSkipUpdate;
    }

    private void SyncViewModelFromSyncControls()
    {
        _viewModel.SyncEnabled = SyncEnabledToggle.IsOn;
        _viewModel.SyncProvider = (SyncProviderComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "webdav";
        _viewModel.SyncEndpoint = SyncEndpointTextBox.Text;
        _viewModel.SyncRootPath = SyncRootPathTextBox.Text;
        _viewModel.SyncBucket = SyncBucketTextBox.Text;
        _viewModel.SyncRegion = SyncRegionTextBox.Text;
        _viewModel.SyncPrefix = SyncPrefixTextBox.Text;
        _viewModel.SyncAccount = SyncAccountTextBox.Text;
        _viewModel.SyncSecret = SyncSecretPasswordBox.Password;
    }

    private void SyncProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args) =>
        UpdateSyncProviderVisibility();

    private void UpdateSyncProviderVisibility()
    {
        if (WebDavFields is null || OssFields is null)
        {
            return;
        }
        bool isOss = (SyncProviderComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "oss";
        WebDavFields.Visibility = isOss ? Visibility.Collapsed : Visibility.Visible;
        OssFields.Visibility = isOss ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SaveSyncButton_Click(object sender, RoutedEventArgs args)
    {
        SyncViewModelFromSyncControls();
        bool saved = await _viewModel.SaveSyncAsync();
        SyncStatusText.Text = saved ? _viewModel.SyncStatus : _viewModel.ErrorMessage ?? "无法保存同步设置。";
    }

    private async void ProbeSyncButton_Click(object sender, RoutedEventArgs args)
    {
        SyncViewModelFromSyncControls();
        await _viewModel.ProbeSyncAsync();
        SyncStatusText.Text = _viewModel.SyncStatus;
    }

    private async void RunSyncButton_Click(object sender, RoutedEventArgs args)
    {
        SyncViewModelFromSyncControls();
        await _viewModel.RunSyncAsync();
        SyncStatusText.Text = _viewModel.SyncStatus;
    }

    private void ResizeWindow()
    {
        nint handle = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        AppWindow.GetFromWindowId(id).Resize(new SizeInt32(600, 760));
    }
}
