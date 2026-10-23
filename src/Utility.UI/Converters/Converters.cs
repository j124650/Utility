using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Utility.UI.Converters;

/// <summary>true -> Visible, false -> Collapsed.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>true -> Collapsed (used to hide controls while busy).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>Inverts a boolean — handy for IsEnabled bindings.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Null-or-empty string -> Collapsed.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// true -> "&#xE8B7;" (folder glyph), false -> "&#xE8A5;" (document glyph).
/// Font-family is supplied by the caller, so this only picks the code point.
/// </summary>
public sealed class BoolToIconConverter : IValueConverter
{
    private const string FolderGlyph = "\uE8B7";
    private const string FileGlyph = "\uE8A5";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FolderGlyph : FileGlyph;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// true -> a folder brush, false -> a muted file brush. The folder brush is passed in
/// through the converter parameter (a hex string) so the palette lives in one place.
/// </summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isTrue = value is true;

        if (parameter is not string hex || hex.Length == 0)
        {
            return isTrue ? Brushes.DarkGoldenrod : Brushes.SlateGray;
        }

        try
        {
            var colour = (Color)ColorConverter.ConvertFromString(hex)!;
            return new SolidColorBrush(colour);
        }
        catch (FormatException)
        {
            // A malformed resource colour must not take the page down.
            return isTrue ? Brushes.DarkGoldenrod : Brushes.SlateGray;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true -> SemiBold, false -> Normal. Used to make folders read stronger than files.</summary>
public sealed class BoolToWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}