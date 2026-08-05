using System;
using System.Globalization;
using System.Windows.Data;

namespace Palisades.Converters
{
    /// <summary>
    /// Returns half the panel width (minus margins) so WrapPanel items
    /// always fit exactly two per row with their natural height.
    /// </summary>
    public class HalfWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double d && d > 0)
                return Math.Floor(d / 2.0) - 22.0;
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
