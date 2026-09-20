using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PkgSender.Views;

public sealed class Ps5CardBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Brushes.Transparent : new SolidColorBrush(Color.Parse("#2A2A2A"));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class Ps5ImageBackgroundConverter : IValueConverter
{
    // Opaque for both: rounded image corners are only visible on a solid bg.
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new SolidColorBrush(Color.Parse("#101010"));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SelectedBorderConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new SolidColorBrush(Color.Parse("#4F8EF7")) : new SolidColorBrush(Color.Parse("#333333"));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Image format badge colors, mirrored from pkg-viewer's
/// per-format icons (dominant hues): pkg blue, exfat teal,
/// ffpfsc gold, ffpkg purple.</summary>
public sealed class FormatBadgeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string)?.ToLowerInvariant() switch
        {
            "exfat" => new SolidColorBrush(Color.Parse("#34B595")),
            "ffpfsc" => new SolidColorBrush(Color.Parse("#CE9C40")),
            "ffpkg" => new SolidColorBrush(Color.Parse("#A27AD8")),
            _ => new SolidColorBrush(Color.Parse("#6498F0")),
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
public sealed class Ps5CardPaddingConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new Thickness(0) : new Thickness(10);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Finished queue rows get a green tint so they read as a group,
/// fully separated from the active rows above.</summary>
public sealed class SentRowBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new SolidColorBrush(Color.Parse("#1E3A2B")) : new SolidColorBrush(Color.Parse("#2A2A2A"));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>State text of finished rows turns green, active stays blue.</summary>
public sealed class SentStateTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new SolidColorBrush(Color.Parse("#6FCF7B")) : new SolidColorBrush(Color.Parse("#7AA5FF"));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
