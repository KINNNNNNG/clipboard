namespace Clipboard.Windows.Views;

internal sealed class SingleWindowLifetime<TWindow>
    where TWindow : class
{
    public TWindow? Current { get; private set; }

    public TWindow GetOrCreate(Func<TWindow> factory, out bool created)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (Current is not null)
        {
            created = false;
            return Current;
        }

        Current = factory();
        created = true;
        return Current;
    }

    public void Release(TWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (ReferenceEquals(Current, window))
        {
            Current = null;
        }
    }
}
