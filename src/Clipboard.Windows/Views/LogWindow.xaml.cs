using Clipboard.Windows.ViewModels;
using Clipboard.Windows.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;
using SystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Clipboard.Windows.Views;

public sealed partial class LogWindow : Window
{
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };

    internal LogWindow(LogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _refreshTimer.Tick += RefreshTimer_Tick;
        Closed += (_, _) => _refreshTimer.Stop();
        LevelComboBox.SelectedIndex = Array.IndexOf(
            new[] { "trace", "debug", "info", "warn", "error" },
            ViewModel.SelectedLevel);
        FilterComboBox.SelectedIndex = 0;
        ResizeWindow();
    }

    internal LogViewModel ViewModel { get; }

    internal async Task ShowAsync()
    {
        await RefreshAsync();
        Activate();
    }

    private async Task RefreshAsync()
    {
        await ViewModel.RefreshAsync();
        StorageText.Text = ViewModel.StorageSummary;
    }

    private async void RefreshTimer_Tick(object? sender, object args)
    {
        if (AutoRefreshToggle.IsOn)
        {
            await RefreshAsync();
        }
    }

    private async void LevelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if ((LevelComboBox.SelectedItem as ComboBoxItem)?.Tag is not string level)
        {
            return;
        }
        await ViewModel.ChangeLevelAsync(level);
    }

    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        string? value = (FilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        ViewModel.MinimumLevel = value is null ? null : Enum.Parse<LogLevel>(value, true);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs args) =>
        ViewModel.SearchText = SearchTextBox.Text;

    private void AutoRefreshToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (AutoRefreshToggle.IsOn)
        {
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs args) => await RefreshAsync();

    private void CopyButton_Click(object sender, RoutedEventArgs args)
    {
        var package = new DataPackage();
        package.SetText(ViewModel.VisibleText);
        SystemClipboard.SetContent(package);
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "清空日志",
            Content = "这会删除当前所有日志文件。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearAsync();
            StorageText.Text = ViewModel.StorageSummary;
        }
    }

    private void ResizeWindow()
    {
        nint handle = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        AppWindow.GetFromWindowId(id).Resize(new SizeInt32(920, 640));
    }
}
