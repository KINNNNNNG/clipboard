using System.Collections.ObjectModel;
using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;

namespace Clipboard.Windows.ViewModels;

internal sealed class ClipboardPanelViewModel : ObservableObject
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(50);
    private readonly object _searchSync = new();
    private readonly IClipboardPanelCore _core;
    private readonly IClipboardItemPasteService _paste;
    private readonly IRetryDelay _delay;
    private CancellationTokenSource? _debounceCancellation;
    private CancellationTokenSource? _activeSearchCancellation;
    private long _searchVersion;
    private string _queryText = string.Empty;
    private bool _useRegex;
    private long? _createdAfterMs;
    private long? _createdBeforeMs;
    private IReadOnlyList<string> _sourceApps = [];
    private IReadOnlyList<string> _kinds = [];
    private int _selectedIndex = -1;
    private bool _isLoading;
    private string? _queryError;
    private string? _errorMessage;

    public ClipboardPanelViewModel(
        IClipboardPanelCore core,
        IClipboardItemPasteService paste,
        IRetryDelay? delay = null)
    {
        _core = core;
        _paste = paste;
        _delay = delay ?? new SystemRetryDelay();
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<ClipboardItemViewModel> Items { get; } = [];

    public string QueryText
    {
        get => _queryText;
        set
        {
            if (SetProperty(ref _queryText, value ?? string.Empty))
            {
                ScheduleSearch();
            }
        }
    }

    public bool UseRegex
    {
        get => _useRegex;
        set
        {
            if (SetProperty(ref _useRegex, value))
            {
                ScheduleSearch();
            }
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        private set
        {
            if (SetProperty(ref _selectedIndex, value))
            {
                OnPropertyChanged(nameof(SelectedItem));
            }
        }
    }

    public ClipboardItemViewModel? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < Items.Count
            ? Items[SelectedIndex]
            : null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? QueryError
    {
        get => _queryError;
        private set => SetProperty(ref _queryError, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public void SetFilters(
        long? createdAfterMs,
        long? createdBeforeMs,
        IReadOnlyList<string> sourceApps,
        IReadOnlyList<string> kinds)
    {
        _createdAfterMs = createdAfterMs;
        _createdBeforeMs = createdBeforeMs;
        _sourceApps = sourceApps.ToArray();
        _kinds = kinds.ToArray();
        ScheduleSearch();
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        CancelDebounce();
        return ExecuteSearchAsync(cancellationToken);
    }

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            SelectedIndex = -1;
            return;
        }
        int start = SelectedIndex < 0 ? 0 : SelectedIndex;
        SelectedIndex = Math.Clamp(start + delta, 0, Items.Count - 1);
    }

    public async Task<PasteResult?> PasteSelectedAsync(
        nint originalHwnd,
        CancellationToken cancellationToken = default)
    {
        ClipboardItemViewModel? selected = SelectedItem;
        if (selected is null)
        {
            return null;
        }
        return await _paste.PasteAsync(selected.Item, originalHwnd, cancellationToken);
    }

    public void HandleEscape() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void ScheduleSearch()
    {
        var cancellation = new CancellationTokenSource();
        lock (_searchSync)
        {
            CancellationTokenSource? previous = _debounceCancellation;
            _debounceCancellation = cancellation;
            previous?.Cancel();
        }
        _ = DebounceAndSearchAsync(cancellation);
    }

    private async Task DebounceAndSearchAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await _delay.DelayAsync(SearchDebounce, cancellation.Token);
            lock (_searchSync)
            {
                if (_debounceCancellation == cancellation)
                {
                    _debounceCancellation = null;
                }
            }
            await ExecuteSearchAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task ExecuteSearchAsync(CancellationToken cancellationToken)
    {
        var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long version;
        lock (_searchSync)
        {
            CancellationTokenSource? previous = _activeSearchCancellation;
            _activeSearchCancellation = active;
            version = ++_searchVersion;
            previous?.Cancel();
        }
        SearchRequestDto request = CreateRequest();
        if (IsCurrent(version))
        {
            IsLoading = true;
            ErrorMessage = null;
        }
        try
        {
            SearchResponseDto response = await _core.SearchAsync(request, active.Token);
            if (!IsCurrent(version))
            {
                return;
            }
            Items.Clear();
            foreach (ClipboardItemDto item in response.Items)
            {
                Items.Add(new ClipboardItemViewModel(item));
            }
            SelectedIndex = Items.Count == 0 ? -1 : 0;
            QueryError = null;
            ErrorMessage = null;
        }
        catch (OperationCanceledException) when (
            active.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch when (request.Mode == SearchModeDto.Regex && IsCurrent(version))
        {
            QueryError = "正则表达式无效。";
            ErrorMessage = null;
        }
        catch when (IsCurrent(version))
        {
            ErrorMessage = "无法加载剪贴板历史。";
        }
        finally
        {
            lock (_searchSync)
            {
                if (_activeSearchCancellation == active)
                {
                    _activeSearchCancellation = null;
                }
            }
            if (IsCurrent(version))
            {
                IsLoading = false;
            }
            active.Dispose();
        }
    }

    private SearchRequestDto CreateRequest() =>
        new(
            QueryText,
            UseRegex ? SearchModeDto.Regex : SearchModeDto.Substring,
            new SearchFiltersDto(
                _createdAfterMs,
                _createdBeforeMs,
                _sourceApps,
                _kinds));

    private bool IsCurrent(long version)
    {
        lock (_searchSync)
        {
            return _searchVersion == version;
        }
    }

    private void CancelDebounce()
    {
        lock (_searchSync)
        {
            CancellationTokenSource? debounce = _debounceCancellation;
            _debounceCancellation = null;
            debounce?.Cancel();
        }
    }
}
