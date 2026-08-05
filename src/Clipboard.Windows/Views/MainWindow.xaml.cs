using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;
using Clipboard.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Clipboard.Windows.Views;

public sealed partial class MainWindow : Window, IClipboardCaptureObserver
{
    private ClipboardPanelViewModel? _viewModel;
    private WindowPresenter? _presenter;
    private IClipboardItemContentReader? _contentReader;
    private IClipboardWriter? _clipboardWriter;
    private UiOperationRunner? _operationRunner;
    private readonly BoundedLruCache<Guid, BitmapImage> _imageCache = new(64);

    public MainWindow()
    {
        InitializeComponent();
        Activated += OnActivated;
    }

    internal void Configure(
        ClipboardPanelViewModel viewModel,
        WindowPresenter presenter,
        IClipboardItemContentReader contentReader,
        IClipboardWriter clipboardWriter)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }
        _viewModel = viewModel;
        _presenter = presenter;
        _contentReader = contentReader;
        _clipboardWriter = clipboardWriter;
        _operationRunner = new UiOperationRunner(
            viewModel.ReportOperationError,
            viewModel.ClearOperationError);
        Root.DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.CloseRequested += (_, _) => HidePanel();
        SyncHistorySelection();
    }

    public void ShowPanel()
    {
        if (_presenter is null || _viewModel is null)
        {
            return;
        }
        _presenter.Show();
        _ = RunUiOperationAsync(
            () => _viewModel.RefreshAsync(),
            "无法加载剪贴板历史。");
        Activate();
        SearchBox.Focus(FocusState.Programmatic);
    }

    public void HidePanel() => _presenter?.Hide();

    internal void SetShortcutState(GlobalShortcutState state)
    {
        ShortcutStateText.Text = state switch
        {
            GlobalShortcutState.Intercepted => "Win+V 已接管",
            GlobalShortcutState.Fallback => "备用快捷键已启用",
            _ => "快捷键不可用",
        };
    }

    internal void SetStatus(string status) => ShortcutStateText.Text = status;

    void IClipboardCaptureObserver.OnCaptured(CaptureNotification notification)
    {
        if (_viewModel is not null)
        {
            _ = RunUiOperationAsync(
                () => _viewModel.RefreshAsync(),
                "无法刷新剪贴板历史。");
        }
    }

    void IClipboardCaptureObserver.OnFailure(CaptureFailure failure)
    {
        ShortcutStateText.Text = "剪贴板读取失败";
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _presenter?.HandleDeactivated();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.QueryText = SearchBox.Text;
        }
    }

    private void RegexButton_Changed(object sender, RoutedEventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.UseRegex = RegexButton.IsChecked == true;
        }
    }

    private void ApplyFiltersButton_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel is null)
        {
            return;
        }
        long? after = TimeFilterComboBox.SelectedIndex switch
        {
            1 => DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(),
            2 => DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds(),
            3 => DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds(),
            _ => null,
        };
        IReadOnlyList<string> sourceApps = SourceFilterBox.Text
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var kinds = new List<string>(3);
        if (TextKindCheckBox.IsChecked == true)
        {
            kinds.Add("text");
        }
        if (ImageKindCheckBox.IsChecked == true)
        {
            kinds.Add("image");
        }
        if (FileKindCheckBox.IsChecked == true)
        {
            kinds.Add("file_bundle");
        }
        _viewModel.SetFilters(after, null, sourceApps, kinds);
        FilterButton.Flyout?.Hide();
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel is not null)
        {
            await RunUiOperationAsync(
                () => _viewModel.ClearUnfavoriteAsync(),
                "无法清除剪贴板历史。");
        }
    }

    private async void FavoriteButton_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.Tag is not ClipboardItemViewModel item)
        {
            return;
        }
        await RunUiOperationAsync(
            () => _viewModel.ToggleFavoriteAsync(item),
            "无法更新收藏状态。");
    }

    private void MoreButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is ClipboardItemViewModel item
            && sender is Button button)
        {
            var menu = new MenuFlyout();
            var copy = new MenuFlyoutItem { Text = "复制" };
            copy.Click += async (_, _) => await RunUiOperationAsync(
                () => CopyItemAsync(item),
                "无法复制此记录。");
            menu.Items.Add(copy);
            var favorite = new MenuFlyoutItem
            {
                Text = item.Favorite ? "取消收藏" : "收藏",
            };
            favorite.Click += async (_, _) => await RunUiOperationAsync(
                () => _viewModel!.ToggleFavoriteAsync(item),
                "无法更新收藏状态。");
            menu.Items.Add(favorite);
            var delete = new MenuFlyoutItem { Text = "删除" };
            delete.Click += async (_, _) => await RunUiOperationAsync(
                () => _viewModel!.DeleteAsync(item),
                "无法删除此记录。");
            menu.Items.Add(delete);
            menu.ShowAt(button);
        }
    }

    private async void HistoryList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (_viewModel is null || _presenter is null || args.ClickedItem is not ClipboardItemViewModel item)
        {
            return;
        }
        int index = _viewModel.Items.IndexOf(item);
        if (index >= 0)
        {
            _viewModel.SelectIndex(index);
            await RunUiOperationAsync(
                () => _viewModel.PasteSelectedAsync(_presenter.OriginalForegroundWindow),
                "无法粘贴此记录。");
        }
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_viewModel is null || HistoryList.SelectedIndex == _viewModel.SelectedIndex)
        {
            return;
        }
        if (HistoryList.SelectedIndex >= 0)
        {
            _viewModel.SelectIndex(HistoryList.SelectedIndex);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ClipboardPanelViewModel.SelectedIndex))
        {
            return;
        }
        if (DispatcherQueue.HasThreadAccess)
        {
            SyncHistorySelection();
        }
        else
        {
            DispatcherQueue.TryEnqueue(SyncHistorySelection);
        }
    }

    private void SyncHistorySelection()
    {
        if (_viewModel is null)
        {
            return;
        }
        int selectedIndex = _viewModel.SelectedIndex;
        if (HistoryList.SelectedIndex != selectedIndex)
        {
            HistoryList.SelectedIndex = selectedIndex;
        }
        if (selectedIndex >= 0 && selectedIndex < _viewModel.Items.Count)
        {
            HistoryList.ScrollIntoView(
                _viewModel.Items[selectedIndex],
                ScrollIntoViewAlignment.Default);
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_viewModel is null)
        {
            return;
        }
        switch (args.Key)
        {
            case global::Windows.System.VirtualKey.Down:
                _viewModel.MoveSelection(1);
                args.Handled = true;
                break;
            case global::Windows.System.VirtualKey.Up:
                _viewModel.MoveSelection(-1);
                args.Handled = true;
                break;
            case global::Windows.System.VirtualKey.Enter:
                _ = RunUiOperationAsync(
                    () => _viewModel.PasteSelectedAsync(
                        _presenter?.OriginalForegroundWindow ?? 0),
                    "无法粘贴此记录。");
                args.Handled = true;
                break;
            case global::Windows.System.VirtualKey.Escape:
                _viewModel.HandleEscape();
                args.Handled = true;
                break;
        }
    }

    private void ImagePreview_Loaded(object sender, RoutedEventArgs args) =>
        LoadImagePreview(sender as Image);

    private void ImagePreview_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args) =>
        LoadImagePreview(sender as Image);

    private async void LoadImagePreview(Image? image)
    {
        if (_contentReader is null
            || image is null
            || image.DataContext is not ClipboardItemViewModel item
            || !item.IsImage)
        {
            return;
        }
        Guid requestedItemId = item.Id;
        var tracker = image.Tag as ImagePreviewLoadTracker ?? new ImagePreviewLoadTracker();
        if (!tracker.Begin(requestedItemId))
        {
            return;
        }
        image.Tag = tracker;
        image.Source = null;
        image.Visibility = Visibility.Visible;
        if (_imageCache.TryGetValue(item.Id, out BitmapImage? cached))
        {
            if (IsCurrentPreview(image, tracker, requestedItemId))
            {
                image.Source = cached;
            }
            return;
        }
        try
        {
            byte[] png = await _contentReader.ReadImageAsync(item.Id);
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(png.AsBuffer());
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                _imageCache.Set(requestedItemId, bitmap);
                if (IsCurrentPreview(image, tracker, requestedItemId))
                {
                    image.Source = bitmap;
                }
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(png);
            }
        }
        catch
        {
            if (IsCurrentPreview(image, tracker, requestedItemId))
            {
                image.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static bool IsCurrentPreview(
        Image image,
        ImagePreviewLoadTracker tracker,
        Guid requestedItemId) =>
        ReferenceEquals(image.Tag, tracker)
        && tracker.IsCurrent(requestedItemId)
        && image.DataContext is ClipboardItemViewModel current
        && current.Id == requestedItemId;

    private Task<bool> RunUiOperationAsync(Func<Task> operation, string failureMessage) =>
        _operationRunner?.RunAsync(operation, failureMessage)
        ?? Task.FromResult(false);

    private async Task CopyItemAsync(ClipboardItemViewModel item)
    {
        if (_clipboardWriter is null)
        {
            return;
        }
        if (item.IsFileBundle)
        {
            if (_contentReader is null)
            {
                throw new InvalidOperationException("File history content reader is unavailable.");
            }
            try
            {
                FileBundleResponseDto bundle = await _contentReader.ReadFileBundleAsync(item.Id);
                await _clipboardWriter.WriteFilesAsync(bundle.Entries);
            }
            catch (Exception error) when (
                error is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException
                or IOException)
            {
                item.MarkSourceUnavailable();
                throw;
            }
        }
        else if (item.IsImage && _contentReader is not null)
        {
            byte[] png = await _contentReader.ReadImageAsync(item.Id);
            try
            {
                await _clipboardWriter.WriteImageAsync(png);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(png);
            }
        }
        else
        {
            await _clipboardWriter.WriteTextAsync(item.Preview);
        }
    }
}
