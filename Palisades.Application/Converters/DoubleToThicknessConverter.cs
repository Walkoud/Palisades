using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Palisades.Converters
{
    public class DoubleToThicknessConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double v = value is double d ? d : 8.0;
            if (double.IsNaN(v)) v = 8.0;
            return new Thickness(v);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
