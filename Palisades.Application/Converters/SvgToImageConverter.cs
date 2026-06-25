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
            string? svgContent = null;
            if (value is ShortcutItem item)
                svgContent = item.SvgContent;
            else if (value is string str)
                svgContent = str;

            if (string.IsNullOrWhiteSpace(svgContent))
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

            return SvgRenderer.RenderSvg(svgContent, foreground);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
