using System;
using System.Globalization;
using System.Windows.Data;

namespace BagaGame.Converters;

/// <summary>
/// Chooses how many achievement tiles fit into the available width. The tile
/// grid uses it so the tiles fill the pane instead of leaving a gap on the
/// right.
/// </summary>
public sealed class WidthToColumnsConverter : IValueConverter
{
    private const double MinTileWidth = 220;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double width && width > 0)
        {
            var columns = (int)Math.Floor(width / MinTileWidth);
            return Math.Max(1, Math.Min(columns, 5));
        }

        return 2;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
