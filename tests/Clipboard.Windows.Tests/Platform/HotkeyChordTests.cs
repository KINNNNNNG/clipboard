using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class HotkeyChordTests
{
    [Fact]
    public void Win_v_suppresses_only_v_and_marks_the_windows_chord()
    {
        var interceptor = new WinVKeyInterceptor();

        Assert.Equal(WinVKeyAction.Pass, interceptor.Handle(NativeMethods.VirtualKey.LeftWindows, keyDown: true, keyUp: false));
        Assert.Equal(WinVKeyAction.SuppressAndMarkChord, interceptor.Handle(NativeMethods.VirtualKey.V, keyDown: true, keyUp: false));
        Assert.Equal(WinVKeyAction.Suppress, interceptor.Handle(NativeMethods.VirtualKey.V, keyDown: false, keyUp: true));
        Assert.Equal(WinVKeyAction.Pass, interceptor.Handle(NativeMethods.VirtualKey.LeftWindows, keyDown: false, keyUp: true));
    }
    [Fact]
    public void Parses_alt_v_as_a_valid_fallback_chord()
    {
        HotkeyChord chord = HotkeyChord.Parse("Alt+V");

        Assert.Equal(HotkeyModifiers.Alt, chord.Modifiers);
        Assert.Equal((ushort)'V', chord.VirtualKey);
        Assert.Equal("Alt+V", chord.ToString());
    }

    [Fact]
    public void Parses_ctrl_shift_v_with_canonical_modifier_order()
    {
        HotkeyChord chord = HotkeyChord.Parse("shift + ctrl + v");

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, chord.Modifiers);
        Assert.Equal((ushort)'V', chord.VirtualKey);
        Assert.Equal("Ctrl+Shift+V", chord.ToString());
    }

    [Theory]
    [InlineData("V")]
    [InlineData("None+V")]
    [InlineData("Alt+V+X")]
    [InlineData("Alt++V")]
    [InlineData("Alt+文")]
    [InlineData("Ctrl+Unknown")]
    public void Rejects_missing_or_malformed_modifier_chords(string value)
    {
        Assert.Throws<FormatException>(() => HotkeyChord.Parse(value));
    }

    [Theory]
    [InlineData("Alt+Tab")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Win+V")]
    [InlineData("Ctrl+Escape")]
    public void Rejects_system_reserved_combinations(string value)
    {
        Assert.Throws<FormatException>(() => HotkeyChord.Parse(value));
    }

    [Fact]
    public void Shortcut_service_reports_intercepted_when_win_v_hook_succeeds()
    {
        var backend = new FakeShortcutBackend { HookSucceeds = true };
        using var service = new GlobalShortcutService(backend);

        GlobalShortcutState state = service.Configure(
            interceptWinV: true,
            HotkeyChord.Parse("Alt+V"));

        Assert.Equal(GlobalShortcutState.Intercepted, state);
        Assert.Equal(1, backend.HookAttempts);
        Assert.Equal(0, backend.FallbackAttempts);
    }

    [Fact]
    public void Shortcut_service_registers_fallback_when_interception_fails_or_is_disabled()
    {
        var failedHook = new FakeShortcutBackend { FallbackSucceeds = true };
        using var first = new GlobalShortcutService(failedHook);

        Assert.Equal(
            GlobalShortcutState.Fallback,
            first.Configure(true, HotkeyChord.Parse("Alt+V")));
        Assert.Equal(1, failedHook.HookAttempts);
        Assert.Equal(1, failedHook.FallbackAttempts);

        var disabledHook = new FakeShortcutBackend { FallbackSucceeds = true };
        using var second = new GlobalShortcutService(disabledHook);
        Assert.Equal(
            GlobalShortcutState.Fallback,
            second.Configure(false, HotkeyChord.Parse("Ctrl+Shift+V")));
        Assert.Equal(0, disabledHook.HookAttempts);
        Assert.Equal(1, disabledHook.FallbackAttempts);
    }

    [Fact]
    public void Shortcut_service_reports_unavailable_when_both_paths_fail()
    {
        var backend = new FakeShortcutBackend();
        using var service = new GlobalShortcutService(backend);

        GlobalShortcutState state = service.Configure(true, HotkeyChord.Parse("Alt+V"));

        Assert.Equal(GlobalShortcutState.Unavailable, state);
        Assert.Equal(GlobalShortcutState.Unavailable, service.State);
    }

    private sealed class FakeShortcutBackend : IGlobalShortcutBackend
    {
        public bool HookSucceeds { get; init; }
        public bool FallbackSucceeds { get; init; }
        public int HookAttempts { get; private set; }
        public int FallbackAttempts { get; private set; }

        public bool TryInstallWinVHook()
        {
            HookAttempts++;
            return HookSucceeds;
        }

        public bool TryRegisterFallback(HotkeyChord chord)
        {
            FallbackAttempts++;
            return FallbackSucceeds;
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
