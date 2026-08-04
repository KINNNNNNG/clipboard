using Clipboard.Windows.Platform;
using Xunit;

namespace Clipboard.Windows.Tests.Platform;

public sealed class ShellIntegrationTests
{
    [Fact]
    public async Task Startup_service_writes_only_the_quoted_executable_path_and_can_disable_it()
    {
        var registry = new FakeStartupRegistry();
        var service = new StartupService(
            registry,
            @"C:\Program Files\Clipboard\Clipboard.Windows.exe");

        await service.SetEnabledAsync(true);
        Assert.Equal(
            "\"C:\\Program Files\\Clipboard\\Clipboard.Windows.exe\"",
            registry.Value);

        await service.SetEnabledAsync(false);
        Assert.Null(registry.Value);
        Assert.Equal(1, registry.DeleteCalls);
    }

    [Fact]
    public void Tray_commands_route_to_open_settings_and_exit_actions()
    {
        var backend = new FakeTrayIconBackend();
        int open = 0;
        int settings = 0;
        int exit = 0;
        using var service = new TrayIconService(
            backend,
            () => open++,
            () => settings++,
            () => exit++);
        service.Start();

        backend.Emit(TrayCommand.OpenClipboard);
        backend.Emit(TrayCommand.Settings);
        backend.Emit(TrayCommand.Exit);

        Assert.Equal(1, open);
        Assert.Equal(1, settings);
        Assert.Equal(1, exit);
    }

    private sealed class FakeStartupRegistry : IStartupRegistry
    {
        public string? Value { get; private set; }
        public int DeleteCalls { get; private set; }

        public void Set(string value) => Value = value;

        public void Delete()
        {
            Value = null;
            DeleteCalls++;
        }
    }

    private sealed class FakeTrayIconBackend : ITrayIconBackend
    {
        private Action<TrayCommand>? _handler;

        public void Start(Action<TrayCommand> handler) => _handler = handler;

        public void Emit(TrayCommand command) => _handler?.Invoke(command);

        public void Dispose()
        {
        }
    }
}
