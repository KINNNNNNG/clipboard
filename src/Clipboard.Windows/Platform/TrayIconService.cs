using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Clipboard.Windows.Platform;

internal enum TrayCommand
{
    OpenClipboard,
    Settings,
    Exit,
}

internal interface ITrayIconBackend : IDisposable
{
    void Start(Action<TrayCommand> handler);
}

internal sealed class TrayIconService : IDisposable
{
    private readonly ITrayIconBackend _backend;
    private readonly Action _openClipboard;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private int _started;

    public TrayIconService(
        nint window,
        Action openClipboard,
        Action openSettings,
        Action exit)
        : this(
            new WindowsTrayIconBackend(window),
            openClipboard,
            openSettings,
            exit)
    {
    }

    internal TrayIconService(
        ITrayIconBackend backend,
        Action openClipboard,
        Action openSettings,
        Action exit)
    {
        _backend = backend;
        _openClipboard = openClipboard;
        _openSettings = openSettings;
        _exit = exit;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        _backend.Start(HandleCommand);
    }

    public void Dispose() => _backend.Dispose();

    private void HandleCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.OpenClipboard:
                _openClipboard();
                break;
            case TrayCommand.Settings:
                _openSettings();
                break;
            case TrayCommand.Exit:
                _exit();
                break;
        }
    }
}

internal sealed class WindowsTrayIconBackend : ITrayIconBackend
{
    private const uint IconId = 1;
    private const uint OpenCommand = 1;
    private const uint SettingsCommand = 2;
    private const uint ExitCommand = 3;
    private readonly nint _window;
    private readonly NativeMethods.SubclassProc _subclassProc;
    private Action<TrayCommand>? _handler;
    private nint _icon;
    private bool _started;

    public WindowsTrayIconBackend(nint window)
    {
        if (window == 0)
        {
            throw new ArgumentException("Tray icon requires a window handle.", nameof(window));
        }
        _window = window;
        _subclassProc = WindowSubclass;
    }

    public void Start(Action<TrayCommand> handler)
    {
        if (_started)
        {
            return;
        }
        _handler = handler;
        _icon = LoadTrayIcon();
        if (!NativeMethods.SetWindowSubclass(_window, _subclassProc, IconId, 0))
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = 0;
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        NativeMethods.NOTIFYICONDATA data = CreateIconData();
        if (!NativeMethods.ShellNotifyIcon(NativeMethods.NotifyIconAdd, ref data))
        {
            NativeMethods.RemoveWindowSubclass(_window, _subclassProc, IconId);
            NativeMethods.DestroyIcon(_icon);
            _icon = 0;
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        data.TimeoutOrVersion = NativeMethods.NotifyIconVersion4;
        NativeMethods.ShellNotifyIcon(NativeMethods.NotifyIconSetVersion, ref data);
        _started = true;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }
        NativeMethods.NOTIFYICONDATA data = CreateIconData();
        NativeMethods.ShellNotifyIcon(NativeMethods.NotifyIconDelete, ref data);
        NativeMethods.RemoveWindowSubclass(_window, _subclassProc, IconId);
        NativeMethods.DestroyIcon(_icon);
        _icon = 0;
        _handler = null;
        _started = false;
    }

    private nint WindowSubclass(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == NativeMethods.TrayCallbackMessage)
        {
            uint trayMessage = unchecked((uint)((long)lParam & 0xffff));
            if (trayMessage == NativeMethods.WmLeftButtonDoubleClick)
            {
                _handler?.Invoke(TrayCommand.OpenClipboard);
            }
            else if (trayMessage is NativeMethods.WmContextMenu or NativeMethods.WmRightButtonUp)
            {
                ShowMenu();
            }
            return 0;
        }
        return NativeMethods.DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = NativeMethods.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }
        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MenuString, OpenCommand, "打开剪贴板");
            NativeMethods.AppendMenu(menu, NativeMethods.MenuString, SettingsCommand, "设置");
            NativeMethods.AppendMenu(menu, NativeMethods.MenuSeparator, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MenuString, ExitCommand, "退出");
            if (!NativeMethods.GetCursorPos(out NativeMethods.POINT point))
            {
                return;
            }
            NativeMethods.SetForegroundWindow(_window);
            uint command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TrackPopupReturnCommand | NativeMethods.TrackPopupRightButton,
                point.X,
                point.Y,
                0,
                _window,
                0);
            _handler?.Invoke(command switch
            {
                OpenCommand => TrayCommand.OpenClipboard,
                SettingsCommand => TrayCommand.Settings,
                ExitCommand => TrayCommand.Exit,
                _ => (TrayCommand)(-1),
            });
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private NativeMethods.NOTIFYICONDATA CreateIconData() =>
        new()
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            Window = _window,
            Id = IconId,
            Flags = NativeMethods.NotifyIconMessage
                | NativeMethods.NotifyIconIcon
                | NativeMethods.NotifyIconTip,
            CallbackMessage = NativeMethods.TrayCallbackMessage,
            Icon = _icon,
            Tip = "剪贴板",
            Info = string.Empty,
            InfoTitle = string.Empty,
        };

    private static nint LoadTrayIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Clipboard.ico");
        nint icon = NativeMethods.LoadImage(
            0,
            path,
            NativeMethods.ImageIcon,
            0,
            0,
            NativeMethods.LoadImageFromFile);
        if (icon == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return icon;
    }
}
