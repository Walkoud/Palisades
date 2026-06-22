using System;
using System.Globalization;
using System.Windows.Data;

namespace Palisades.Converters
{
    public class StringToTextAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string align)
            {
                return align switch
                {
                    "Center" => System.Windows.TextAlignment.Center,
                    "Right" => System.Windows.TextAlignment.Right,
                    _ => System.Windows.TextAlignment.Left
                };
            }
            return System.Windows.TextAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
