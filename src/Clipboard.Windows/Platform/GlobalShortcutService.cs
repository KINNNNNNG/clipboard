using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Clipboard.Windows.Platform;

internal enum GlobalShortcutState
{
    Intercepted,
    Fallback,
    Unavailable,
}

internal interface IGlobalShortcutBackend : IDisposable
{
    bool TryInstallWinVHook();

    bool TryRegisterFallback(HotkeyChord chord);

    void Stop();
}

internal interface IGlobalShortcutConfigurator
{
    GlobalShortcutState Configure(bool interceptWinV, HotkeyChord fallback);
}

internal enum WinVKeyAction
{
    Pass,
    Suppress,
    SuppressAndMarkChord,
    PassAndOpen,
}

internal sealed class WinVKeyInterceptor
{
    private bool _winPressed;
    private bool _suppressV;
    private bool _openPending;

    public WinVKeyAction Handle(uint virtualKey, bool keyDown, bool keyUp)
    {
        if (virtualKey is NativeMethods.VirtualKey.LeftWindows or NativeMethods.VirtualKey.RightWindows)
        {
            if (keyDown)
            {
                _winPressed = true;
            }
            else if (keyUp)
            {
                _winPressed = false;
                _suppressV = false;
                if (_openPending)
                {
                    _openPending = false;
                    return WinVKeyAction.PassAndOpen;
                }
            }
            return WinVKeyAction.Pass;
        }

        if (virtualKey != NativeMethods.VirtualKey.V)
        {
            return WinVKeyAction.Pass;
        }
        if (keyDown && _winPressed)
        {
            if (_suppressV)
            {
                return WinVKeyAction.Suppress;
            }
            _suppressV = true;
            _openPending = true;
            return WinVKeyAction.SuppressAndMarkChord;
        }
        if (keyUp && _suppressV)
        {
            _suppressV = false;
            return WinVKeyAction.Suppress;
        }
        return WinVKeyAction.Pass;
    }

    public void Reset()
    {
        _winPressed = false;
        _suppressV = false;
        _openPending = false;
    }
}

internal sealed class GlobalShortcutService : IDisposable, IGlobalShortcutConfigurator
{
    private readonly IGlobalShortcutBackend _backend;
    private int _disposed;

    public GlobalShortcutService(DispatcherQueue dispatcherQueue, Action openPanel)
        : this(new WindowsGlobalShortcutBackend(dispatcherQueue, openPanel))
    {
    }

    internal GlobalShortcutService(IGlobalShortcutBackend backend)
    {
        _backend = backend;
    }

    public GlobalShortcutState State { get; private set; } = GlobalShortcutState.Unavailable;

    public GlobalShortcutState Configure(bool interceptWinV, HotkeyChord fallback)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _backend.Stop();
        if (interceptWinV && _backend.TryInstallWinVHook())
        {
            State = GlobalShortcutState.Intercepted;
        }
        else if (_backend.TryRegisterFallback(fallback))
        {
            State = GlobalShortcutState.Fallback;
        }
        else
        {
            State = GlobalShortcutState.Unavailable;
        }
        return State;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _backend.Dispose();
    }
}

internal sealed class WindowsGlobalShortcutBackend : IGlobalShortcutBackend
{
    private const int HookStartupTimeoutMs = 2000;
    private const int HotkeyId = 0x434C;
    private const uint ModifierNoRepeat = 0x4000;
    internal const ushort WindowsChordMarkerKey = NativeMethods.VirtualKey.Control;

    private readonly object _sync = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action _openPanel;
    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;
    private Thread? _worker;
    private uint _workerThreadId;
    private nint _hook;
    private bool _hotkeyRegistered;
    private readonly WinVKeyInterceptor _interceptor = new();
    private int _disposed;

    public WindowsGlobalShortcutBackend(DispatcherQueue dispatcherQueue, Action openPanel)
    {
        _dispatcherQueue = dispatcherQueue;
        _openPanel = openPanel;
        _hookProc = HookCallback;
    }

    public bool TryInstallWinVHook() => StartWorker(WorkerMode.WinVHook, default);

    public bool TryRegisterFallback(HotkeyChord chord) =>
        StartWorker(WorkerMode.FallbackHotkey, chord);

    public void Stop()
    {
        Thread? worker;
        uint threadId;
        lock (_sync)
        {
            worker = _worker;
            threadId = _workerThreadId;
        }
        if (worker is null)
        {
            return;
        }
        if (threadId != 0)
        {
            NativeMethods.PostThreadMessage(
                threadId,
                NativeMethods.WmQuit,
                0,
                0);
        }
        if (worker != Thread.CurrentThread)
        {
            worker.Join(HookStartupTimeoutMs);
        }
        lock (_sync)
        {
            if (_worker == worker)
            {
                _worker = null;
                _workerThreadId = 0;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Stop();
    }

    private bool StartWorker(WorkerMode mode, HotkeyChord chord)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        Stop();
        var startup = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() => RunMessageLoop(mode, chord, startup))
        {
            IsBackground = true,
            Name = "Clipboard global shortcut",
        };
        worker.SetApartmentState(ApartmentState.STA);
        lock (_sync)
        {
            _worker = worker;
        }
        worker.Start();
        if (!startup.Task.Wait(HookStartupTimeoutMs) || !startup.Task.Result)
        {
            Stop();
            return false;
        }
        return true;
    }

    private void RunMessageLoop(
        WorkerMode mode,
        HotkeyChord chord,
        TaskCompletionSource<bool> startup)
    {
        try
        {
            uint threadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(out _, 0, 0, 0, NativeMethods.PeekMessageNoRemove);
            lock (_sync)
            {
                _workerThreadId = threadId;
            }

            bool success = mode switch
            {
                WorkerMode.WinVHook => InstallHook(),
                WorkerMode.FallbackHotkey => RegisterFallback(chord),
                _ => false,
            };
            startup.TrySetResult(success);
            if (!success)
            {
                return;
            }

            while (NativeMethods.GetMessage(out NativeMethods.MSG message, 0, 0, 0) > 0)
            {
                if (message.Message == NativeMethods.WmHotkey
                    && message.WParam == HotkeyId)
                {
                    RequestPanel();
                }
                NativeMethods.TranslateMessage(in message);
                NativeMethods.DispatchMessage(in message);
            }
        }
        catch
        {
            startup.TrySetResult(false);
        }
        finally
        {
            if (_hook != 0)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = 0;
            }
            if (_hotkeyRegistered)
            {
                NativeMethods.UnregisterHotKey(0, HotkeyId);
                _hotkeyRegistered = false;
            }
            _interceptor.Reset();
        }
    }

    private bool InstallHook()
    {
        nint module = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLowLevel,
            _hookProc,
            module,
            0);
        return _hook != 0;
    }

    private bool RegisterFallback(HotkeyChord chord)
    {
        uint modifiers = (uint)chord.Modifiers | ModifierNoRepeat;
        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            0,
            HotkeyId,
            modifiers,
            chord.VirtualKey);
        return _hotkeyRegistered;
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }

        uint message = unchecked((uint)wParam);
        bool keyDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
        bool keyUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
        NativeMethods.KBDLLHOOKSTRUCT keyboard =
            Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        WinVKeyAction action = _interceptor.Handle(keyboard.VirtualKey, keyDown, keyUp);
        if (action == WinVKeyAction.SuppressAndMarkChord)
        {
            if (!TryMarkWindowsChord())
            {
                _interceptor.Reset();
                return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
            }
            return 1;
        }
        if (action == WinVKeyAction.Suppress)
        {
            return 1;
        }
        if (action == WinVKeyAction.PassAndOpen)
        {
            nint next = NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
            RequestPanel();
            return next;
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static unsafe bool TryMarkWindowsChord()
    {
        NativeMethods.INPUT* inputs = stackalloc NativeMethods.INPUT[2];
        inputs[0] = new NativeMethods.INPUT
        {
            Type = NativeMethods.InputKeyboard,
            Union = new NativeMethods.INPUTUNION
            {
                Keyboard = new NativeMethods.KEYBDINPUT
                {
                    VirtualKey = WindowsChordMarkerKey,
                },
            },
        };
        inputs[1] = inputs[0];
        inputs[1].Union.Keyboard.Flags = NativeMethods.KeyEventKeyUp;
        return NativeMethods.SendInput(2, inputs, sizeof(NativeMethods.INPUT)) == 2;
    }

    private void RequestPanel() =>
        _dispatcherQueue.TryEnqueue(() => _openPanel());

    private enum WorkerMode
    {
        WinVHook,
        FallbackHotkey,
    }
}
