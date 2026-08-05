using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Clipboard.Windows.Platform;

internal readonly record struct MonitorSnapshot(PixelRect WorkArea, uint Dpi);

internal interface IWindowPlacementBackend
{
    nint PanelWindowHandle { get; }

    nint GetForegroundWindow();

    PixelPoint GetCursorPosition();

    MonitorSnapshot GetMonitor(PixelPoint point);

    void ConfigureToolWindow(
        bool alwaysOnTop,
        bool hasBorder,
        bool hasTitleBar,
        bool extendsContentIntoTitleBar);

    void Show(PanelPlacementResult placement);

    void BringToForeground();

    void Hide();
}

internal sealed class WindowPresenter
{
    private static readonly PanelSize DefaultPanelSize = new(386, 500);
    private readonly IWindowPlacementBackend _backend;
    private readonly PanelSize _logicalSize;
    private bool _configured;

    public WindowPresenter(Window window)
        : this(new WinUiWindowPlacementBackend(window), DefaultPanelSize)
    {
    }

    internal WindowPresenter(IWindowPlacementBackend backend, PanelSize logicalSize)
    {
        _backend = backend;
        _logicalSize = logicalSize;
    }

    public nint OriginalForegroundWindow { get; private set; }

    public bool IsSettingsWindowOpen { get; set; }

    public void Show()
    {
        nint foreground = _backend.GetForegroundWindow();
        if (foreground != 0 && foreground != _backend.PanelWindowHandle)
        {
            OriginalForegroundWindow = foreground;
        }
        PixelPoint cursor = _backend.GetCursorPosition();
        MonitorSnapshot monitor = _backend.GetMonitor(cursor);
        PanelPlacementResult placement = PanelPlacement.Calculate(
            cursor,
            monitor.WorkArea,
            monitor.Dpi,
            _logicalSize);
        if (!_configured)
        {
            _backend.ConfigureToolWindow(
                alwaysOnTop: false,
                hasBorder: true,
                hasTitleBar: false,
                extendsContentIntoTitleBar: true);
            _configured = true;
        }
        _backend.Show(placement);
        _backend.BringToForeground();
    }

    public void Hide() => _backend.Hide();

    public void HandleEscape() => Hide();

    public void HandleDeactivated()
    {
        if (!IsSettingsWindowOpen)
        {
            Hide();
        }
    }
}

internal sealed class WinUiWindowPlacementBackend : IWindowPlacementBackend
{
    private const uint MonitorDefaultToNearest = 2;
    private const int EffectiveDpi = 0;
    private readonly Window _window;
    private readonly AppWindow _appWindow;

    public WinUiWindowPlacementBackend(Window window)
    {
        _window = window;
        PanelWindowHandle = WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(PanelWindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
    }

    public nint PanelWindowHandle { get; }

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public PixelPoint GetCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT point))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return new PixelPoint(point.X, point.Y);
    }

    public MonitorSnapshot GetMonitor(PixelPoint point)
    {
        var nativePoint = new NativeMethods.POINT { X = point.X, Y = point.Y };
        nint monitor = NativeMethods.MonitorFromPoint(nativePoint, MonitorDefaultToNearest);
        var info = new NativeMethods.MONITORINFO
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        uint dpi = 96;
        if (NativeMethods.GetDpiForMonitor(monitor, EffectiveDpi, out uint dpiX, out _) == 0
            && dpiX > 0)
        {
            dpi = dpiX;
        }
        return new MonitorSnapshot(
            new PixelRect(
                info.WorkArea.Left,
                info.WorkArea.Top,
                info.WorkArea.Right,
                info.WorkArea.Bottom),
            dpi);
    }

    public void ConfigureToolWindow(
        bool alwaysOnTop,
        bool hasBorder,
        bool hasTitleBar,
        bool extendsContentIntoTitleBar)
    {
        _appWindow.IsShownInSwitchers = false;
        // PowerToys' Advanced Paste uses the normal overlapped presenter. The
        // tool-window presenter keeps WS_EX_WINDOWEDGE and re-applies it after
        // activation, which produces the asymmetric DWM edge on WinUI 3.
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = alwaysOnTop;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        _appWindow.SetPresenter(presenter);
        _window.ExtendsContentIntoTitleBar = extendsContentIntoTitleBar;
        presenter.SetBorderAndTitleBar(hasBorder, hasTitleBar);
        uint color = NativeMethods.DwmColorDefault;
        NativeMethods.DwmSetWindowAttribute(
            PanelWindowHandle,
            NativeMethods.DwmwaBorderColor,
            ref color,
            sizeof(uint));
        uint corner = NativeMethods.DwmWindowCornerRound;
        NativeMethods.DwmSetWindowAttribute(
            PanelWindowHandle,
            NativeMethods.DwmwaWindowCornerPreference,
            ref corner,
            sizeof(uint));
    }

    public void Show(PanelPlacementResult placement)
    {
        _appWindow.MoveAndResize(new RectInt32(
            placement.Left,
            placement.Top,
            placement.Width,
            placement.Height));
        _window.Activate();
    }

    public void BringToForeground() => NativeMethods.SetForegroundWindow(PanelWindowHandle);

    public void Hide() => _appWindow.Hide();
}
