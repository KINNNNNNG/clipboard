namespace Clipboard.Windows.Platform;

internal interface IStartupSettingsService
{
    Task SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);
}

internal interface IStartupRegistry
{
    void Set(string value);

    void Delete();
}

internal sealed class StartupService : IStartupSettingsService
{
    private readonly IStartupRegistry _registry;
    private readonly string _executablePath;

    public StartupService()
        : this(
            new WindowsStartupRegistry(),
            Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve the executable path."))
    {
    }

    internal StartupService(IStartupRegistry registry, string executablePath)
    {
        _registry = registry;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(executablePath));
        }
        if (executablePath.IndexOf('"') >= 0)
        {
            throw new ArgumentException("Executable path cannot contain quotes.", nameof(executablePath));
        }
        _executablePath = executablePath;
    }

    public Task SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
        {
            _registry.Set($"\"{_executablePath}\"");
        }
        else
        {
            _registry.Delete();
        }
        return Task.CompletedTask;
    }
}
