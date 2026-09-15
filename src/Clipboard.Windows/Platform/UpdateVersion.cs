using System.Text.RegularExpressions;

namespace Clipboard.Windows.Platform;

/// <summary>
/// Strict <c>MAJOR.MINOR.PATCH</c> validation for update metadata.
/// </summary>
/// <remarks>
/// Version comparison and download verification happen in the clipboard core; the client only
/// validates the value it stores as a skipped release so the settings file stays well formed.
/// </remarks>
internal static partial class UpdateVersion
{
    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Pattern().IsMatch(value);

    [GeneratedRegex(@"^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$")]
    private static partial Regex Pattern();
}
