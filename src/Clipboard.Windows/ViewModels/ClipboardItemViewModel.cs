using Clipboard.Windows.Core;
using Clipboard.Windows.Views;

namespace Clipboard.Windows.ViewModels;

internal sealed class ClipboardItemViewModel : ObservableObject
{
    private bool _favorite;

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

    public long LastUsedMs => Item.LastUsedMs;

    public uint? Width => Item.Width;

    public uint? Height => Item.Height;

    public ulong? Bytes => Item.Bytes;

    public bool IsImage => string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase);

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
