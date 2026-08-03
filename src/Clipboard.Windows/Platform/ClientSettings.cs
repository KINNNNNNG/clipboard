namespace Clipboard.Windows.Platform;

internal sealed record ClientSettings(
    int? MaxRegularItems,
    uint? MaxAgeDays,
    ulong? MaxImageBytes,
    bool InterceptWinV,
    string FallbackHotkey,
    bool StartWithWindows,
    string Theme)
{
    public const ulong BytesPerGiB = 1024UL * 1024 * 1024;

    public static ClientSettings Default { get; } = new(
        1000,
        30,
        BytesPerGiB,
        true,
        "Alt+V",
        false,
        "system");

    public void Validate()
    {
        if (MaxRegularItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRegularItems));
        }
        if (MaxAgeDays == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAgeDays));
        }
        if (MaxImageBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxImageBytes));
        }
        _ = HotkeyChord.Parse(FallbackHotkey);
        if (Theme is not ("system" or "light" or "dark"))
        {
            throw new ArgumentOutOfRangeException(nameof(Theme));
        }
    }
}
