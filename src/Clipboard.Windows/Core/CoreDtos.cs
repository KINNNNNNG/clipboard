namespace Clipboard.Windows.Core;

internal static class ClipboardCoreLimits
{
    public const int MaxImageBytes = 50 * 1024 * 1024;
    public const ulong DefaultMaxFavoriteFileCacheBytes = 5UL * 1024 * 1024 * 1024;
}

internal interface IClipboardCaptureSink : ISettingsRetentionService
{
    Task<MutationResponseDto> IngestTextAsync(
        IngestTextRequestDto request,
        CancellationToken cancellationToken = default);

    Task<MutationResponseDto> IngestImageAsync(
        IngestImageRequestDto request,
        ReadOnlyMemory<byte> png,
        CancellationToken cancellationToken = default);

    Task<MutationResponseDto> IngestFileBundleAsync(
        IngestFileBundleRequestDto request,
        CancellationToken cancellationToken = default);
}

internal interface IClipboardPanelCore
{
    Task<SearchResponseDto> SearchAsync(
        SearchRequestDto request,
        CancellationToken cancellationToken = default);

    Task<MutationResponseDto> SetFavoriteAsync(
        SetFavoriteRequestDto request,
        CancellationToken cancellationToken = default);

    Task<MutationResponseDto> CacheFileBundleAsync(
        Guid itemId,
        ulong maxBytes,
        CancellationToken cancellationToken = default);

    Task<MutationResponseDto> UncacheFileBundleAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task<MutationResponseDto> DeleteAsync(
        DeleteRequestDto request,
        CancellationToken cancellationToken = default);

    Task<RetentionResponseDto> ClearUnfavoriteAsync(
        CancellationToken cancellationToken = default);
}

internal interface ISettingsRetentionService
{
    Task<RetentionResponseDto> ApplyRetentionAsync(
        ApplyRetentionRequestDto request,
        CancellationToken cancellationToken = default);
}

internal enum SearchModeDto
{
    Substring,
    Regex,
}

internal sealed record SearchFiltersDto(
    long? CreatedAfterMs,
    long? CreatedBeforeMs,
    IReadOnlyList<string> SourceApps,
    IReadOnlyList<string> Kinds)
{
    public static SearchFiltersDto Empty { get; } = new(null, null, [], []);
}

internal sealed record SearchRequestDto(
    string Pattern,
    SearchModeDto Mode,
    SearchFiltersDto Filters);

internal sealed record IngestTextRequestDto(
    string Text,
    string SourceApp,
    long CapturedMs,
    string? SourceAppDisplayName = null);

internal sealed record IngestImageRequestDto(
    uint Width,
    uint Height,
    string SourceApp,
    long CapturedMs,
    string? SourceAppDisplayName = null);

internal enum FileEntryKindDto
{
    File,
    Directory,
}

internal sealed record FileEntryDto(
    string Path,
    FileEntryKindDto Kind,
    ulong Size,
    long ModifiedMs);

internal sealed record IngestFileBundleRequestDto(
    IReadOnlyList<FileEntryDto> Entries,
    string SourceApp,
    long CapturedMs,
    string? SourceAppDisplayName = null);

internal sealed record ReadFileBundleRequestDto(Guid ItemId);

internal sealed record CacheFileBundleRequestDto(Guid ItemId, ulong MaxBytes);

internal sealed record UncacheFileBundleRequestDto(Guid ItemId);

internal sealed record FileBundleResponseDto(
    Guid ItemId,
    IReadOnlyList<FileEntryDto> Entries);

internal sealed record HlcDto(long PhysicalMs, uint Logical, Guid NodeId);

internal sealed record SetFavoriteRequestDto(
    Guid ItemId,
    bool Favorite,
    HlcDto Updated);

internal sealed record DeleteRequestDto(Guid ItemId, HlcDto Updated);

internal sealed record RetentionPolicyDto(
    int? MaxRegularItems,
    uint? MaxAgeDays,
    ulong? MaxImageBytes);

internal sealed record ApplyRetentionRequestDto(long NowMs, RetentionPolicyDto Policy);

internal sealed record ClipboardItemDto(
    Guid Id,
    string Kind,
    string Preview,
    string SourceApp,
    long LastUsedMs,
    bool Favorite,
    uint? Width,
    uint? Height,
    ulong? Bytes,
    string? SourceAppDisplayName = null,
    int? FileCount = null,
    string? RepresentativeName = null,
    string? RepresentativeKind = null);

internal sealed record SearchResponseDto(IReadOnlyList<ClipboardItemDto> Items);

internal sealed record MutationResponseDto(Guid ItemId);

internal sealed record RetentionResponseDto(int DeletedLocal, int TombstonesCreated);

internal sealed record SyncResponseDto(int Pulled, int Merged, int Uploaded, int RejectedLocalOnly);

internal sealed record RemoteProbeResponseDto(bool Available);

internal sealed record RemoteConfigDto(
    string Provider,
    int Version,
    string Endpoint,
    string? Username = null,
    string? Password = null,
    string? Region = null,
    string? Bucket = null,
    string? Prefix = null,
    string? AccessKeyId = null,
    string? AccessKeySecret = null);

internal sealed record SyncRemoteRequestDto(Guid DeviceId, RemoteConfigDto Remote);

internal sealed record ProbeRemoteRequestDto(RemoteConfigDto Remote);

internal sealed record CommandEnvelope<T>(int ApiVersion, string Type, T Payload);

internal sealed record CommandWithoutPayload(int ApiVersion, string Type);

internal sealed record ImageMetadataEnvelope(
    int ApiVersion,
    uint Width,
    uint Height,
    string SourceApp,
    long CapturedMs,
    string? SourceAppDisplayName = null);
