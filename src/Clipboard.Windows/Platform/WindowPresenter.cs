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

    void HideDwmBorder();

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
                hasBorder: false,
                hasTitleBar: false,
                extendsContentIntoTitleBar: true);
            _configured = true;
        }
        _backend.Show(placement);
        _backend.BringToForeground();
        // AppWindow.Show can recreate the non-client frame; apply this after it is visible.
        _backend.HideDwmBorder();
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
    private const nuint ChromeSubclassId = 0x43484D;
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly NativeMethods.SubclassProc _subclassProc;

    public WinUiWindowPlacementBackend(Window window)
    {
        _window = window;
        PanelWindowHandle = WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(PanelWindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _subclassProc = WindowSubclassProc;
        NativeMethods.SetWindowSubclass(
            PanelWindowHandle,
            _subclassProc,
            ChromeSubclassId,
            0);
        _window.Activated += Window_Activated;
        _window.Closed += Window_Closed;
    }

    public nint PanelWindowHandle { get; }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            HideDwmBorder();
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        NativeMethods.RemoveWindowSubclass(
            PanelWindowHandle,
            _subclassProc,
            ChromeSubclassId);
    }

    private nint WindowSubclassProc(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == NativeMethods.WmStyleChanging && lParam != 0)
        {
            int index = wParam.ToInt32();
            int style = Marshal.ReadInt32(lParam, sizeof(int));
            int filteredStyle = index switch
            {
                NativeMethods.WindowStyleIndex => NativeMethods.BuildBorderlessWindowStyle(style),
                NativeMethods.ExtendedWindowStyleIndex => NativeMethods.BuildBorderlessExtendedStyle(style),
                _ => style,
            };
            if (filteredStyle != style)
            {
                Marshal.WriteInt32(lParam, sizeof(int), filteredStyle);
            }
        }

        return NativeMethods.DefSubclassProc(window, message, wParam, lParam);
    }

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
        HideDwmBorder();
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

    public void HideDwmBorder()
    {
        int style = unchecked((int)NativeMethods.GetWindowLongPtr(
            PanelWindowHandle,
            NativeMethods.WindowStyleIndex).ToInt64());
        int borderlessStyle = NativeMethods.BuildBorderlessWindowStyle(style);
        if (borderlessStyle != style)
        {
            NativeMethods.SetWindowLongPtr(
                PanelWindowHandle,
                NativeMethods.WindowStyleIndex,
                new nint(borderlessStyle));
        }

        int extendedStyle = unchecked((int)NativeMethods.GetWindowLongPtr(
            PanelWindowHandle,
            NativeMethods.ExtendedWindowStyleIndex).ToInt64());
        int borderlessExtendedStyle = NativeMethods.BuildBorderlessExtendedStyle(extendedStyle);
        if (borderlessExtendedStyle != extendedStyle)
        {
            NativeMethods.SetWindowLongPtr(
                PanelWindowHandle,
                NativeMethods.ExtendedWindowStyleIndex,
                new nint(borderlessExtendedStyle));
        }

        NativeMethods.SetLayeredWindowAttributes(
            PanelWindowHandle,
            0,
            byte.MaxValue,
            NativeMethods.LayeredAlpha);
        NativeMethods.SetWindowPos(
            PanelWindowHandle,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.SetWindowPosNoSize
                | NativeMethods.SetWindowPosNoMove
                | NativeMethods.SetWindowPosNoZOrder
                | NativeMethods.SetWindowPosNoOwnerZOrder
                | NativeMethods.SetWindowPosNoActivate
                | NativeMethods.SetWindowPosFrameChanged
                | NativeMethods.SetWindowPosShowWindow);

        // FrameChanged can make WinUI rebuild the non-client frame. Reapply the
        // DWM attributes after the refresh so the system border stays hidden.
        uint color = NativeMethods.DwmColorNone;
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
        ApplyWindowRegion();
    }

    private void ApplyWindowRegion()
    {
        if (!NativeMethods.GetWindowRect(PanelWindowHandle, out NativeMethods.RECT rect))
        {
            return;
        }
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        uint dpi = NativeMethods.GetDpiForWindow(PanelWindowHandle);
        int inset = NativeMethods.CalculateWindowRegionInset(dpi);
        int cornerDiameter = (int)Math.Ceiling(
            NativeMethods.WindowRegionCornerDiameter * (dpi == 0 ? 1d : dpi / 96d));
        nint region = NativeMethods.CreateRoundRectRgn(
            inset,
            inset,
            NativeMethods.CalculateWindowRegionExtent(width),
            NativeMethods.CalculateWindowRegionExtent(height),
            cornerDiameter,
            cornerDiameter);
        if (region != 0 && !NativeMethods.SetWindowRgn(PanelWindowHandle, region, true))
        {
            NativeMethods.DeleteObject(region);
        }
    }

    public void BringToForeground() => NativeMethods.SetForegroundWindow(PanelWindowHandle);

    public void Hide() => _appWindow.Hide();
}
