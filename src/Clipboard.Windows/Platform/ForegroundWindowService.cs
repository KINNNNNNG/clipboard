using System.Diagnostics;

namespace Clipboard.Windows.Platform;

internal sealed class ForegroundWindowService : IForegroundWindowService
{
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    public async Task<bool> RestoreAsync(
        nint originalHwnd,
        CancellationToken cancellationToken = default)
    {
        if (originalHwnd == nint.Zero || !NativeMethods.IsWindow(originalHwnd))
        {
            return false;
        }
        if (!NativeMethods.SetForegroundWindow(originalHwnd))
        {
            return false;
        }

        long deadline = Stopwatch.GetTimestamp()
            + (long)(RestoreTimeout.TotalSeconds * Stopwatch.Frequency);
        do
        {
            if (NativeMethods.GetForegroundWindow() == originalHwnd)
            {
                return true;
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
        while (Stopwatch.GetTimestamp() < deadline);

        return NativeMethods.GetForegroundWindow() == originalHwnd;
    }

    public unsafe uint SendPasteInput()
    {
        NativeMethods.INPUT* inputs = stackalloc NativeMethods.INPUT[4];
        inputs[0] = Key(NativeMethods.VirtualKey.Control, keyUp: false);
        inputs[1] = Key(NativeMethods.VirtualKey.V, keyUp: false);
        inputs[2] = Key(NativeMethods.VirtualKey.V, keyUp: true);
        inputs[3] = Key(NativeMethods.VirtualKey.Control, keyUp: true);
        return NativeMethods.SendInput(
            4,
            inputs,
            sizeof(NativeMethods.INPUT));
    }

    private static NativeMethods.INPUT Key(ushort virtualKey, bool keyUp) =>
        new()
        {
            Type = NativeMethods.InputKeyboard,
            Union = new NativeMethods.INPUTUNION
            {
                Keyboard = new NativeMethods.KEYBDINPUT
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? NativeMethods.KeyEventKeyUp : 0,
                },
            },
        };
}
