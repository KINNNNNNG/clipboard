using Clipboard.Windows.Core;

namespace Clipboard.Windows.Platform;

internal interface IRetentionPolicyProvider
{
    RetentionPolicyDto Current { get; }

    void Update(ClientSettings settings);
}

internal sealed class RetentionPolicyProvider : IRetentionPolicyProvider
{
    private RetentionPolicyDto _current = ToPolicy(ClientSettings.Default);

    public RetentionPolicyDto Current => Volatile.Read(ref _current);

    public void Update(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Volatile.Write(ref _current, ToPolicy(settings));
    }

    private static RetentionPolicyDto ToPolicy(ClientSettings settings) => new(
        settings.MaxRegularItems,
        settings.MaxAgeDays,
        settings.MaxImageBytes);
}
