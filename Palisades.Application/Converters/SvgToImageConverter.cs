using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Palisades.Models;
using Palisades.Helpers;

namespace Palisades.Converters
{
    public class SvgToImageConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not ShortcutItem item || string.IsNullOrWhiteSpace(item.SvgContent))
                return null;

            Brush foreground = Brushes.White;
            if (parameter is Brush brush)
            {
                foreground = brush;
            }
            else if (parameter is string colorStr)
            {
                try
                {
                    var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(colorStr);
                    foreground = new SolidColorBrush(color);
                }
                catch { }
            }

            return SvgRenderer.RenderSvg(item.SvgContent, foreground);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
