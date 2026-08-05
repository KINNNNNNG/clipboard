using Clipboard.Windows.Core;
using Clipboard.Windows.Platform;
using Clipboard.Windows.ViewModels;
using Xunit;

namespace Clipboard.Windows.Tests.ViewModels;

public sealed class ClipboardPanelViewModelTests
{
    [Fact]
    public async Task Typing_debounces_for_fifty_milliseconds_and_searches_only_latest_text()
    {
        var delay = new ControlledDelay();
        var core = new FakePanelCore
        {
            Handler = (request, _) => Task.FromResult(Response(TextItem(request.Pattern))),
        };
        var viewModel = new ClipboardPanelViewModel(core, new FakePasteService(), delay);

        viewModel.QueryText = "first";
        viewModel.QueryText = "second";

        Assert.Equal(2, delay.Entries.Count);
        Assert.All(delay.Entries, entry => Assert.Equal(50, entry.Delay.TotalMilliseconds));
        Assert.True(delay.Entries[0].CancellationToken.IsCancellationRequested);
        delay.ReleaseLatest();
        await EventuallyAsync(() => core.Requests.Count == 1 && viewModel.Items.Count == 1);
        Assert.Equal("second", Assert.Single(core.Requests).Pattern);
        Assert.Equal("second", Assert.Single(viewModel.Items).Preview);
    }

    [Fact]
    public async Task Empty_query_loads_recent_items_in_substring_mode()
    {
        var core = new FakePanelCore
        {
            Handler = (_, _) => Task.FromResult(Response(TextItem("recent"))),
        };
        var viewModel = new ClipboardPanelViewModel(core, new FakePasteService());

        await viewModel.RefreshAsync();

        SearchRequestDto request = Assert.Single(core.Requests);
        Assert.Equal(string.Empty, request.Pattern);
        Assert.Equal(SearchModeDto.Substring, request.Mode);
        Assert.Equal("recent", Assert.Single(viewModel.Items).Preview);
    }

    [Fact]
    public async Task Invalid_regex_sets_query_error_and_preserves_last_valid_results()
    {
        var core = new FakePanelCore
        {
            Handler = (request, _) => request.Pattern == "["
                ? Task.FromException<SearchResponseDto>(
                    new ClipboardCoreException(CoreStatus.InvalidRegex))
                : Task.FromResult(Response(TextItem("preserved"))),
        };
        var viewModel = new ClipboardPanelViewModel(
            core,
            new FakePasteService(),
            new ControlledDelay());
        await viewModel.RefreshAsync();
        viewModel.UseRegex = true;
        viewModel.QueryText = "[";

        await viewModel.RefreshAsync();

        Assert.NotNull(viewModel.QueryError);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("preserved", Assert.Single(viewModel.Items).Preview);
    }

    [Fact]
    public async Task Regex_mode_reports_core_failure_as_history_error_instead_of_invalid_regex()
    {
        var core = new FakePanelCore
        {
            Handler = (request, _) => request.Mode == SearchModeDto.Regex
                ? Task.FromException<SearchResponseDto>(
                    new ClipboardCoreException(CoreStatus.CoreError))
                : Task.FromResult(Response(TextItem("preserved"))),
        };
        var viewModel = new ClipboardPanelViewModel(core, new FakePasteService());
        await viewModel.RefreshAsync();
        viewModel.UseRegex = true;

        await viewModel.RefreshAsync();

        Assert.Null(viewModel.QueryError);
        Assert.Equal("无法加载剪贴板历史。", viewModel.ErrorMessage);
        Assert.Equal("preserved", Assert.Single(viewModel.Items).Preview);
    }

    [Fact]
    public async Task Combined_filters_are_forwarded_to_the_rust_core()
    {
        var core = new FakePanelCore
        {
            Handler = (_, _) => Task.FromResult(Response()),
        };
        var viewModel = new ClipboardPanelViewModel(
            core,
            new FakePasteService(),
            new ControlledDelay());
        viewModel.SetFilters(
            createdAfterMs: 10,
            createdBeforeMs: 20,
            sourceApps: ["notepad.exe", "msedge.exe"],
            kinds: ["text", "image", "file_bundle"]);

        await viewModel.RefreshAsync();

        SearchFiltersDto filters = Assert.Single(core.Requests).Filters;
        Assert.Equal(10, filters.CreatedAfterMs);
        Assert.Equal(20, filters.CreatedBeforeMs);
        Assert.Equal(["notepad.exe", "msedge.exe"], filters.SourceApps);
        Assert.Equal(["text", "image", "file_bundle"], filters.Kinds);
    }

    [Fact]
    public async Task Refresh_cancels_old_request_and_prevents_stale_result_overwrite()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        var core = new FakePanelCore
        {
            Handler = (_, cancellationToken) =>
            {
                call++;
                if (call == 1)
                {
                    firstStarted.SetResult();
                    var completion = new TaskCompletionSource<SearchResponseDto>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    cancellationToken.Register(() =>
                    {
                        firstCancelled.TrySetResult();
                        completion.TrySetCanceled(cancellationToken);
                    });
                    return completion.Task;
                }
                return Task.FromResult(Response(TextItem("newest")));
            },
        };
        var viewModel = new ClipboardPanelViewModel(core, new FakePasteService());

        Task first = viewModel.RefreshAsync();
        await firstStarted.Task;
        await viewModel.RefreshAsync();
        await first;

        Assert.True(firstCancelled.Task.IsCompletedSuccessfully);
        Assert.Equal("newest", Assert.Single(viewModel.Items).Preview);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Keyboard_selection_enter_and_escape_drive_expected_commands()
    {
        var first = TextItem("first");
        var second = TextItem("second");
        var core = new FakePanelCore
        {
            Handler = (_, _) => Task.FromResult(Response(first, second)),
        };
        var paste = new FakePasteService();
        var viewModel = new ClipboardPanelViewModel(core, paste);
        int closeRequests = 0;
        viewModel.CloseRequested += (_, _) => closeRequests++;
        await viewModel.RefreshAsync();

        viewModel.MoveSelection(1);
        PasteResult? result = await viewModel.PasteSelectedAsync(new nint(42));
        viewModel.HandleEscape();

        Assert.Equal(1, viewModel.SelectedIndex);
        Assert.Equal(second.Id, paste.LastItem?.Id);
        Assert.Equal(new nint(42), paste.LastOriginalWindow);
        Assert.Equal(PasteResultKind.Pasted, result?.Kind);
        Assert.Equal(1, closeRequests);
    }

    [Fact]
    public async Task Keyboard_selection_is_clamped_at_list_boundaries()
    {
        var core = new FakePanelCore
        {
            Handler = (_, _) => Task.FromResult(Response(
                TextItem("first"),
                TextItem("second"),
                TextItem("third"))),
        };
        var viewModel = new ClipboardPanelViewModel(core, new FakePasteService());
        await viewModel.RefreshAsync();

        viewModel.MoveSelection(-1);
        Assert.Equal(0, viewModel.SelectedIndex);
        viewModel.MoveSelection(10);
        Assert.Equal(2, viewModel.SelectedIndex);
        viewModel.MoveSelection(1);
        Assert.Equal(2, viewModel.SelectedIndex);
    }

    [Fact]
    public async Task Toggling_favorite_persists_state_and_updates_the_card()
    {
        ClipboardItemDto item = TextItem("keep me");
        var core = new FakePanelCore
        {
            Handler = (_, _) => Task.FromResult(Response(item)),
        };
        var viewModel = new ClipboardPanelViewModel(
            core,
            new FakePasteService(),
            timeProvider: new ManualTimeProvider(1234),
            nodeId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

        await viewModel.RefreshAsync();
        ClipboardItemViewModel card = Assert.Single(viewModel.Items);

        await viewModel.ToggleFavoriteAsync(card);

        Assert.True(card.Favorite);
        Assert.Equal(item.Id, Assert.Single(core.FavoriteRequests).ItemId);
        Assert.True(core.FavoriteRequests[0].Favorite);
        Assert.Equal(1234, core.FavoriteRequests[0].Updated.PhysicalMs);
    }

    [Fact]
    public async Task Mutations_in_the_same_millisecond_increment_the_hlc_logical_counter()
    {
        ClipboardItemDto item = TextItem("toggle twice");
        var core = new FakePanelCore
        {
            Handler = (_, _) => Task.FromResult(Response(item)),
        };
        var viewModel = new ClipboardPanelViewModel(
            core,
            new FakePasteService(),
            timeProvider: new ManualTimeProvider(1234),
            nodeId: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        await viewModel.RefreshAsync();
        ClipboardItemViewModel card = Assert.Single(viewModel.Items);

        await viewModel.ToggleFavoriteAsync(card);
        await viewModel.ToggleFavoriteAsync(card);

        Assert.Equal((uint)0, core.FavoriteRequests[0].Updated.Logical);
        Assert.Equal((uint)1, core.FavoriteRequests[1].Updated.Logical);
        Assert.False(card.Favorite);
    }

    [Fact]
    public async Task Deleting_item_removes_it_from_the_visible_history()
    {
        ClipboardItemDto first = TextItem("first");
        ClipboardItemDto second = TextItem("second");
        var core = new FakePanelCore
        {
            Handler = (_, _) => Task.FromResult(Response(first, second)),
        };
        var viewModel = new ClipboardPanelViewModel(core, new FakePasteService());

        await viewModel.RefreshAsync();
        await viewModel.DeleteAsync(viewModel.Items[0]);

        Assert.Single(viewModel.Items);
        Assert.Equal("second", viewModel.Items[0].Preview);
        Assert.Equal(first.Id, Assert.Single(core.DeleteRequests).ItemId);
    }

    [Fact]
    public async Task Clearing_unfavorite_items_refreshes_the_visible_history()
    {
        var core = new FakePanelCore
        {
            Handler = (_, _) => Task.FromResult(Response(TextItem("remaining"))),
        };
        var viewModel = new ClipboardPanelViewModel(core, new FakePasteService());

        await viewModel.ClearUnfavoriteAsync();

        Assert.Equal(1, core.ClearCalls);
        Assert.Equal("remaining", Assert.Single(viewModel.Items).Preview);
    }

    [Fact]
    public async Task Empty_state_and_explicit_selection_follow_visible_items()
    {
        ClipboardItemDto first = TextItem("first");
        ClipboardItemDto second = TextItem("second");
        var core = new FakePanelCore
        {
            Handler = (_, _) => Task.FromResult(Response(first, second)),
        };
        var viewModel = new ClipboardPanelViewModel(core, new FakePasteService());

        Assert.True(viewModel.IsEmpty);
        await viewModel.RefreshAsync();
        viewModel.SelectIndex(1);

        Assert.False(viewModel.IsEmpty);
        Assert.Equal(second.Id, viewModel.SelectedItem?.Id);
    }

    private static ClipboardItemDto TextItem(string preview) =>
        new(Guid.NewGuid(), "text", preview, "notepad.exe", 100, false, null, null, null);

    private static SearchResponseDto Response(params ClipboardItemDto[] items) => new(items);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakePanelCore : IClipboardPanelCore
    {
        public Func<SearchRequestDto, CancellationToken, Task<SearchResponseDto>> Handler
        {
            get;
            init;
        } = (_, _) => Task.FromResult(Response());

        public List<SearchRequestDto> Requests { get; } = [];

        public List<SetFavoriteRequestDto> FavoriteRequests { get; } = [];

        public List<DeleteRequestDto> DeleteRequests { get; } = [];

        public int ClearCalls { get; private set; }

        public Task<SearchResponseDto> SearchAsync(
            SearchRequestDto request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Handler(request, cancellationToken);
        }

        public Task<MutationResponseDto> SetFavoriteAsync(
            SetFavoriteRequestDto request,
            CancellationToken cancellationToken = default)
        {
            FavoriteRequests.Add(request);
            return Task.FromResult(new MutationResponseDto(request.ItemId));
        }

        public Task<MutationResponseDto> DeleteAsync(
            DeleteRequestDto request,
            CancellationToken cancellationToken = default)
        {
            DeleteRequests.Add(request);
            return Task.FromResult(new MutationResponseDto(request.ItemId));
        }

        public Task<RetentionResponseDto> ClearUnfavoriteAsync(
            CancellationToken cancellationToken = default)
        {
            ClearCalls++;
            return Task.FromResult(new RetentionResponseDto(0, 0));
        }
    }

    private sealed class FakePasteService : IClipboardItemPasteService
    {
        public ClipboardItemDto? LastItem { get; private set; }
        public nint LastOriginalWindow { get; private set; }

        public Task<PasteResult> PasteAsync(
            ClipboardItemDto item,
            nint originalHwnd,
            CancellationToken cancellationToken = default)
        {
            LastItem = item;
            LastOriginalWindow = originalHwnd;
            return Task.FromResult(new PasteResult(PasteResultKind.Pasted, item.Id));
        }
    }

    private sealed class ControlledDelay : IRetryDelay
    {
        public List<DelayEntry> Entries { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            Entries.Add(new DelayEntry(delay, cancellationToken, completion));
            return completion.Task;
        }

        public void ReleaseLatest() => Entries[^1].Completion.TrySetResult();
    }

    private sealed class ManualTimeProvider(long milliseconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private sealed record DelayEntry(
        TimeSpan Delay,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion);
}
