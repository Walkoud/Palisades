using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Palisades.Converters
{
    public class EqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool result = value?.ToString() == parameter?.ToString();
            if (targetType == typeof(Visibility))
                return result ? Visibility.Visible : Visibility.Collapsed;
            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
