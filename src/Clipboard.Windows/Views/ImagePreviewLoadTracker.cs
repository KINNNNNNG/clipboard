namespace Clipboard.Windows.Views;

internal sealed class ImagePreviewLoadTracker
{
    private Guid? _itemId;

    public bool Begin(Guid itemId)
    {
        if (_itemId == itemId)
        {
            return false;
        }
        _itemId = itemId;
        return true;
    }

    public bool IsCurrent(Guid itemId) => _itemId == itemId;
}
