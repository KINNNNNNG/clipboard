using Clipboard.Windows.Core;

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

    public string SourceApp => Item.SourceApp;

    public long LastUsedMs => Item.LastUsedMs;

    public uint? Width => Item.Width;

    public uint? Height => Item.Height;

    public ulong? Bytes => Item.Bytes;

    public bool Favorite
    {
        get => _favorite;
        set => SetProperty(ref _favorite, value);
    }
}
