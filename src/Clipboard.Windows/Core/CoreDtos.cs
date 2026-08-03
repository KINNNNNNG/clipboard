namespace Clipboard.Windows.Core;

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

internal sealed record IngestTextRequestDto(string Text, string SourceApp, long CapturedMs);

internal sealed record IngestImageRequestDto(
    uint Width,
    uint Height,
    string SourceApp,
    long CapturedMs);

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
    ulong? Bytes);

internal sealed record SearchResponseDto(IReadOnlyList<ClipboardItemDto> Items);

internal sealed record MutationResponseDto(Guid ItemId);

internal sealed record RetentionResponseDto(int DeletedLocal, int TombstonesCreated);

internal sealed record CommandEnvelope<T>(int ApiVersion, string Type, T Payload);

internal sealed record CommandWithoutPayload(int ApiVersion, string Type);

internal sealed record ImageMetadataEnvelope(
    int ApiVersion,
    uint Width,
    uint Height,
    string SourceApp,
    long CapturedMs);
