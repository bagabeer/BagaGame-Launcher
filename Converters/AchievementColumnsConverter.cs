using System;
using System.Globalization;
using System.Windows.Data;

namespace BagaGame.Converters;

/// <summary>
/// Number of achievement tiles per row, derived from the window width. The
/// window width is known from the start, unlike the scroll viewer width which
/// can still be stale during the first layout pass.
/// </summary>
public sealed class AchievementColumnsConverter : IValueConverter
{
    // Sidebar (252) + page margins (36 + 22) + column gap (20).
    private const double ChromeWidth = 330;
    private const double MinTileWidth = 220;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double width && width > ChromeWidth)
        {
            var columnWidth = (width - ChromeWidth) / 2;
            var columns = (int)Math.Floor(columnWidth / MinTileWidth);
            return Math.Max(1, Math.Min(columns, 5));
        }

        return 1;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
