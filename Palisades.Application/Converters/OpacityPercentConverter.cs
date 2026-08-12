using System;
using System.Globalization;
using System.Windows.Data;

namespace Palisades.Converters
{
    /// <summary>
    /// Converts an opacity expressed as a percent (0..100) into a double (0.0..1.0).
    /// </summary>
    public class OpacityPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int i)
                return Math.Clamp(i, 0, 100) / 100.0;
            if (value is double d)
                return Math.Clamp(d, 0, 100) / 100.0;
            return 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double d)
                return (int)Math.Round(Math.Clamp(d, 0.0, 1.0) * 100);
            return 100;
        }
    }
}
