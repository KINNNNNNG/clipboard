namespace Clipboard.Windows.Platform;

internal readonly record struct PixelPoint(int X, int Y);

internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

internal readonly record struct PanelSize(int Width, int Height);

internal readonly record struct PanelPlacementResult(int Left, int Top, int Width, int Height);

internal static class PanelPlacement
{
    private const int DefaultDpi = 96;
    private const int LogicalGap = 8;
    private const double MaximumWorkAreaFraction = 0.70;

    public static PanelPlacementResult Calculate(
        PixelPoint cursor,
        PixelRect workArea,
        uint dpi,
        PanelSize logicalSize)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentException("Work area must have a positive size.", nameof(workArea));
        }
        if (logicalSize.Width <= 0 || logicalSize.Height <= 0)
        {
            throw new ArgumentException("Panel size must have a positive size.", nameof(logicalSize));
        }

        int effectiveDpi = dpi == 0 ? DefaultDpi : checked((int)dpi);
        int width = Scale(logicalSize.Width, effectiveDpi);
        int requestedHeight = Scale(logicalSize.Height, effectiveDpi);
        int maximumHeight = Math.Max(1, (int)Math.Floor(workArea.Height * MaximumWorkAreaFraction));
        int height = Math.Min(requestedHeight, maximumHeight);
        width = Math.Min(width, workArea.Width);
        int gap = Math.Max(1, Scale(LogicalGap, effectiveDpi));

        int left = cursor.X + gap;
        if (left + width > workArea.Right)
        {
            left = cursor.X - gap - width;
        }
        int top = cursor.Y + gap;
        if (top + height > workArea.Bottom)
        {
            top = cursor.Y - gap - height;
        }

        left = Math.Clamp(left, workArea.Left, workArea.Right - width);
        top = Math.Clamp(top, workArea.Top, workArea.Bottom - height);
        return new PanelPlacementResult(left, top, width, height);
    }

    private static int Scale(int logicalPixels, int dpi) =>
        checked((int)Math.Round(logicalPixels * dpi / (double)DefaultDpi, MidpointRounding.AwayFromZero));
}
