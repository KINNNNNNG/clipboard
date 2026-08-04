using Microsoft.Win32;

namespace Clipboard.Windows.Platform;

internal sealed class WindowsStartupRegistry : IStartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clipboard";

    public void Set(string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new UnauthorizedAccessException("Unable to open the current-user startup key.");
        key.SetValue(ValueName, value, RegistryValueKind.String);
    }

    public void Delete()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
