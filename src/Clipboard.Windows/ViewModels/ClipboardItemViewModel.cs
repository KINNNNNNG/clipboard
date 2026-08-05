using Clipboard.Windows.Core;
using Clipboard.Windows.Views;

namespace Clipboard.Windows.ViewModels;

internal sealed class ClipboardItemViewModel : ObservableObject
{
    private bool _favorite;
    private bool _isSourceUnavailable;

    public ClipboardItemViewModel(ClipboardItemDto item)
    {
        Item = item;
        _favorite = item.Favorite;
    }

    internal ClipboardItemDto Item { get; }

    public Guid Id => Item.Id;

    public string Kind => Item.Kind;

    public string Preview => Item.Preview;

    public string DisplayPreview => ClipboardDisplayFormatter.LimitPreview(Preview);

    public string SourceApp => Item.SourceApp;

    public string DisplaySourceApp
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Item.SourceAppDisplayName))
            {
                return Item.SourceAppDisplayName.Trim();
            }
            string identifier = SourceApp.Trim();
            if (identifier.Length == 0
                || string.Equals(identifier, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return "未知应用";
            }
            return identifier.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? identifier[..^4]
                : identifier;
        }
    }

    public long LastUsedMs => Item.LastUsedMs;

    public uint? Width => Item.Width;

    public uint? Height => Item.Height;

    public ulong? Bytes => Item.Bytes;

    public bool IsImage => string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase);

    public bool IsFileBundle =>
        string.Equals(Kind, "file_bundle", StringComparison.OrdinalIgnoreCase);

    public string FileIconGlyph =>
        string.Equals(Item.RepresentativeKind, "directory", StringComparison.OrdinalIgnoreCase)
            ? "\uE8B7"
            : "\uE8A5";

    public string FileNameSummary => !string.IsNullOrWhiteSpace(Item.RepresentativeName)
        ? Item.RepresentativeName.Trim()
        : DisplayPreview;

    public string FileCountLabel => Item.FileCount is > 1
        ? $"共 {Item.FileCount.Value} 项"
        : string.Empty;

    public bool IsSourceUnavailable
    {
        get => _isSourceUnavailable;
        private set => SetProperty(ref _isSourceUnavailable, value);
    }

    public void MarkSourceUnavailable() => IsSourceUnavailable = true;

    public string ImageDimensions => Width.HasValue && Height.HasValue
        ? $"{Width} × {Height}"
        : string.Empty;

    public string ImageBytesLabel => Bytes.HasValue
        ? ClipboardDisplayFormatter.FormatBytes(Bytes.Value)
        : string.Empty;

    public string LastUsedLabel => ClipboardDisplayFormatter.FormatTimestamp(
        LastUsedMs,
        TimeProvider.System.GetUtcNow());

    public string FavoriteGlyph => Favorite ? "\uE735" : "\uE734";

    public bool Favorite
    {
        get => _favorite;
        set
        {
            if (SetProperty(ref _favorite, value))
            {
                OnPropertyChanged(nameof(FavoriteGlyph));
            }
        }
    }
}
