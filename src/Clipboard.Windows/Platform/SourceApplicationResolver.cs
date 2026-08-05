using System.Diagnostics;

namespace Clipboard.Windows.Platform;

internal sealed class SourceApplicationResolver : ISourceApplicationResolver
{
    private const int MaxDisplayNameLength = 128;
    private static readonly SourceApplicationInfo Unknown = new("unknown", "unknown");

    public SourceApplicationInfo Resolve()
    {
        try
        {
            nint owner = NativeMethods.GetClipboardOwner();
            if (owner == 0)
            {
                return Unknown;
            }
            NativeMethods.GetWindowThreadProcessId(owner, out uint processId);
            if (processId == 0)
            {
                return Unknown;
            }
            using Process process = Process.GetProcessById(checked((int)processId));
            string identifier = NormalizeIdentifier(process.ProcessName);
            if (identifier == "unknown")
            {
                return Unknown;
            }

            FileVersionInfo? versionInfo = TryReadVersionInfo(process);
            return new SourceApplicationInfo(
                identifier,
                FormatDisplayName(
                    identifier,
                    versionInfo?.FileDescription,
                    versionInfo?.ProductName));
        }
        catch
        {
            return Unknown;
        }
    }

    internal static string FormatDisplayName(
        string identifier,
        string? fileDescription,
        string? productName)
    {
        foreach (string? candidate in new[] { fileDescription, productName })
        {
            string? normalized = NormalizeDisplayText(candidate);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        string normalizedIdentifier = NormalizeIdentifier(identifier);
        string mapped = normalizedIdentifier.ToLowerInvariant() switch
        {
            "explorer.exe" => "文件资源管理器",
            "notepad.exe" => "记事本",
            "mspaint.exe" => "画图",
            _ => normalizedIdentifier.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? normalizedIdentifier[..^4]
                : normalizedIdentifier,
        };
        return Truncate(mapped);
    }

    private static FileVersionInfo? TryReadVersionInfo(Process process)
    {
        try
        {
            string? executablePath = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(executablePath)
                ? null
                : FileVersionInfo.GetVersionInfo(executablePath);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeIdentifier(string? processName)
    {
        string name = processName?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.IndexOfAny(['\\', '/']) >= 0)
        {
            return "unknown";
        }
        string baseName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
        if (baseName.Length == 0)
        {
            return "unknown";
        }
        return $"{Truncate(baseName, MaxDisplayNameLength - 4)}.exe";
    }

    private static string? NormalizeDisplayText(string? value)
    {
        string candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0
            || candidate.IndexOfAny(['\\', '/', '\r', '\n']) >= 0)
        {
            return null;
        }
        return Truncate(candidate, MaxDisplayNameLength);
    }

    private static string Truncate(string value) => Truncate(value, MaxDisplayNameLength);

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }
        int length = maxLength;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }
        return value[..length];
    }
}

internal sealed record SourceApplicationInfo(string Identifier, string DisplayName);
