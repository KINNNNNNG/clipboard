namespace Clipboard.Windows.Views;

internal sealed class FileIconLoadTracker
{
    private Guid? _itemId;
    private string? _cacheKey;

    public bool Begin(Guid itemId, string cacheKey)
    {
        if (_itemId == itemId && string.Equals(_cacheKey, cacheKey, StringComparison.Ordinal))
        {
            return false;
        }

        _itemId = itemId;
        _cacheKey = cacheKey;
        return true;
    }

    public bool IsCurrent(Guid itemId, string cacheKey) =>
        _itemId == itemId
        && string.Equals(_cacheKey, cacheKey, StringComparison.Ordinal);
}
