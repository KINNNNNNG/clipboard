using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class PanelPlacementTests
{
    private static readonly PanelSize LogicalPanelSize = new(386, 500);
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1080);

    [Fact]
    public void Cursor_in_each_corner_uses_the_nearest_available_quadrant()
    {
        Assert.Equal(
            new PanelPlacementResult(108, 108, 386, 500),
            PanelPlacement.Calculate(new PixelPoint(100, 100), WorkArea, 96, LogicalPanelSize));
        Assert.Equal(
            new PanelPlacementResult(1406, 108, 386, 500),
            PanelPlacement.Calculate(new PixelPoint(1800, 100), WorkArea, 96, LogicalPanelSize));
        Assert.Equal(
            new PanelPlacementResult(108, 392, 386, 500),
            PanelPlacement.Calculate(new PixelPoint(100, 900), WorkArea, 96, LogicalPanelSize));
        Assert.Equal(
            new PanelPlacementResult(1406, 392, 386, 500),
            PanelPlacement.Calculate(new PixelPoint(1800, 900), WorkArea, 96, LogicalPanelSize));
    }

    [Fact]
    public void Placement_stays_inside_taskbar_reduced_work_area()
    {
        var workArea = new PixelRect(0, 0, 1920, 1040);

        PanelPlacementResult placement = PanelPlacement.Calculate(
            new PixelPoint(1900, 1000),
            workArea,
            96,
            LogicalPanelSize);

        Assert.InRange(placement.Left, workArea.Left, workArea.Right - placement.Width);
        Assert.InRange(placement.Top, workArea.Top, workArea.Bottom - placement.Height);
    }

    [Theory]
    [InlineData(96, 386, 500)]
    [InlineData(144, 579, 750)]
    [InlineData(192, 772, 1000)]
    public void Logical_size_is_scaled_for_each_monitor_dpi(
        uint dpi,
        int expectedWidth,
        int expectedHeight)
    {
        PanelPlacementResult placement = PanelPlacement.Calculate(
            new PixelPoint(300, 300),
            new PixelRect(0, 0, 3840, 2160),
            dpi,
            LogicalPanelSize);

        Assert.Equal(expectedWidth, placement.Width);
        Assert.Equal(expectedHeight, placement.Height);
    }

    [Fact]
    public void Height_is_limited_to_seventy_percent_of_work_area()
    {
        PanelPlacementResult placement = PanelPlacement.Calculate(
            new PixelPoint(100, 100),
            new PixelRect(0, 0, 1200, 600),
            192,
            LogicalPanelSize);

        Assert.Equal(420, placement.Height);
        Assert.Equal(772, placement.Width);
    }

    [Fact]
    public void Window_presenter_records_source_window_and_applies_monitor_placement()
    {
        var backend = new FakeWindowBackend
        {
            ForegroundWindow = new nint(99),
            Cursor = new PixelPoint(1800, 900),
            Monitor = new MonitorSnapshot(WorkArea, 96),
        };
        var presenter = new WindowPresenter(backend, LogicalPanelSize);

        presenter.Show();

        Assert.Equal(new nint(99), presenter.OriginalForegroundWindow);
        Assert.True(backend.Configured);
        Assert.Equal(new PanelPlacementResult(1406, 392, 386, 500), backend.LastPlacement);
        Assert.Equal(1, backend.ShowCalls);
    }

    [Fact]
    public void Deactivation_hides_panel_except_while_settings_are_open_and_escape_always_hides()
    {
        var backend = new FakeWindowBackend
        {
            Cursor = new PixelPoint(100, 100),
            Monitor = new MonitorSnapshot(WorkArea, 96),
        };
        var presenter = new WindowPresenter(backend, LogicalPanelSize)
        {
            IsSettingsWindowOpen = true,
        };

        presenter.HandleDeactivated();
        Assert.Equal(0, backend.HideCalls);

        presenter.IsSettingsWindowOpen = false;
        presenter.HandleDeactivated();
        presenter.HandleEscape();
        Assert.Equal(2, backend.HideCalls);
    }

    [Fact]
    public void Repeated_show_does_not_replace_source_with_the_panel_window()
    {
        var backend = new FakeWindowBackend
        {
            PanelWindow = new nint(7),
            ForegroundWindow = new nint(99),
            Cursor = new PixelPoint(100, 100),
            Monitor = new MonitorSnapshot(WorkArea, 96),
        };
        var presenter = new WindowPresenter(backend, LogicalPanelSize);

        presenter.Show();
        backend.ForegroundWindow = backend.PanelWindow;
        presenter.Show();

        Assert.Equal(new nint(99), presenter.OriginalForegroundWindow);
    }

    private sealed class FakeWindowBackend : IWindowPlacementBackend
    {
        public nint PanelWindow { get; init; }
        public nint ForegroundWindow { get; set; }
        public PixelPoint Cursor { get; init; }
        public MonitorSnapshot Monitor { get; init; }
        public bool Configured { get; private set; }
        public PanelPlacementResult? LastPlacement { get; private set; }
        public int ShowCalls { get; private set; }
        public int HideCalls { get; private set; }

        public nint PanelWindowHandle => PanelWindow;

        public nint GetForegroundWindow() => ForegroundWindow;

        public PixelPoint GetCursorPosition() => Cursor;

        public MonitorSnapshot GetMonitor(PixelPoint point) => Monitor;

        public void ConfigureToolWindow() => Configured = true;

        public void Show(PanelPlacementResult placement)
        {
            LastPlacement = placement;
            ShowCalls++;
        }

        public void Hide() => HideCalls++;
    }
}
