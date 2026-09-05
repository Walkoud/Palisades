using System;
using System.Globalization;
using System.Windows.Data;

namespace Palisades.Converters
{
    public class DoubleMulConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double v = value is double d ? d : 0;
            double factor = 1.0;
            if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                factor = p;
            return v * factor;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
