namespace Clipboard.Windows.Platform;

internal sealed record ClientSettings(
    int? MaxRegularItems,
    uint? MaxAgeDays,
    ulong? MaxImageBytes,
    bool InterceptWinV,
    string FallbackHotkey,
    bool StartWithWindows,
    string Theme,
    ulong? MaxFavoriteFileCacheBytes = 5UL * 1024 * 1024 * 1024,
    SyncSettings? Sync = null)
{
    public const ulong BytesPerGiB = 1024UL * 1024 * 1024;
    public const ulong DefaultMaxFavoriteFileCacheBytes = 5UL * BytesPerGiB;

    public static ClientSettings Default { get; } = new(
        1000,
        30,
        BytesPerGiB,
        true,
        "Alt+V",
        false,
        "system",
        DefaultMaxFavoriteFileCacheBytes);

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
        if (MaxFavoriteFileCacheBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFavoriteFileCacheBytes));
        }
        _ = HotkeyChord.Parse(FallbackHotkey);
        if (Theme is not ("system" or "light" or "dark"))
        {
            throw new ArgumentOutOfRangeException(nameof(Theme));
        }
        Sync?.Validate();
    }
}

internal interface IFavoriteFileCachePolicyProvider
{
    ulong? MaxFavoriteFileCacheBytes { get; }

    void Update(ClientSettings settings);
}

internal sealed class FavoriteFileCachePolicyProvider : IFavoriteFileCachePolicyProvider
{
    private readonly object _sync = new();
    private ulong? _maxFavoriteFileCacheBytes = ClientSettings.DefaultMaxFavoriteFileCacheBytes;

    public ulong? MaxFavoriteFileCacheBytes
    {
        get
        {
            lock (_sync)
            {
                return _maxFavoriteFileCacheBytes;
            }
        }
    }

    public void Update(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_sync)
        {
            _maxFavoriteFileCacheBytes = settings.MaxFavoriteFileCacheBytes;
        }
    }
}

internal sealed record SyncSettings(
    bool Enabled,
    string Provider,
    string Endpoint,
    string? RootPath,
    string? Bucket,
    string? Region,
    string? Prefix,
    string DeviceId,
    string? CredentialProfileId)
{
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }
        if (Provider is not ("webdav" or "oss")
            || !Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !Guid.TryParse(DeviceId, out _)
            || string.IsNullOrWhiteSpace(CredentialProfileId))
        {
            throw new ArgumentOutOfRangeException(nameof(SyncSettings));
        }
        if (Provider == "webdav" && string.IsNullOrWhiteSpace(RootPath))
        {
            throw new ArgumentOutOfRangeException(nameof(RootPath));
        }
        if (Provider == "oss" && (string.IsNullOrWhiteSpace(Bucket) || string.IsNullOrWhiteSpace(Region)))
        {
            throw new ArgumentOutOfRangeException(nameof(Bucket));
        }
    }
}
