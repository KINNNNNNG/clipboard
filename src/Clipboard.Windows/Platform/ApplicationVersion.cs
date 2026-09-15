using System.Reflection;

namespace Clipboard.Windows.Platform;

/// <summary>
/// The version of the running client, used as the baseline for update checks.
/// </summary>
internal static class ApplicationVersion
{
    public static string Current { get; } = Format(typeof(ApplicationVersion).Assembly);

    internal static string Format(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Version? version = assembly.GetName().Version;
        if (version is null)
        {
            return "0.0.0";
        }
        int build = version.Build < 0 ? 0 : version.Build;
        return $"{version.Major}.{version.Minor}.{build}";
    }
}
