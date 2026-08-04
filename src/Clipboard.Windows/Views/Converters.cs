using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Clipboard.Windows.Views;

internal static class ClipboardDisplayFormatter
{
    private const int MaxPreviewLines = 6;
    private const double Kibi = 1024;
    private const double Mebi = Kibi * 1024;
    private const double Gibi = Mebi * 1024;

    public static string LimitPreview(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        string[] lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        return string.Join('\n', lines.Take(MaxPreviewLines));
    }

    public static string FormatBytes(ulong bytes)
    {
        if (bytes < Kibi)
        {
            return $"{bytes} B";
        }
        if (bytes < Mebi)
        {
            return FormatUnit(bytes / Kibi, "KB");
        }
        if (bytes < Gibi)
        {
            return FormatUnit(bytes / Mebi, "MB");
        }
        return FormatUnit(bytes / Gibi, "GB");
    }

    public static string FormatTimestamp(long timestampMs, DateTimeOffset now)
    {
        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        TimeSpan age = now - timestamp;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }
        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)} 分钟前";
        }
        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours} 小时前";
        }
        if (age < TimeSpan.FromDays(2))
        {
            return "昨天";
        }
        if (age < TimeSpan.FromDays(7))
        {
            return $"{(int)age.TotalDays} 天前";
        }
        return timestamp.ToLocalTime().ToString("M月d日 HH:mm", CultureInfo.CurrentCulture);
    }

    private static string FormatUnit(double value, string unit) =>
        $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {unit}";
}

public sealed class ClipboardKindVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string expected = parameter?.ToString() ?? string.Empty;
        return string.Equals(value?.ToString(), expected, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool flag && !flag;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is bool flag && !flag;
}

public sealed class BooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility visibility && visibility == Visibility.Visible;
}
