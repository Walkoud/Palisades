using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Palisades.Converters
{
    public class FilePathToImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp" && ext != ".gif" && ext != ".ico")
                {
                    return null!;
                }

                try
                {
                    var uri = new Uri("file:///" + path.Replace('\\', '/'));
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = uri;
                    img.EndInit();
                    img.Freeze();
                    return img;
                }
                catch
                {
                    // Safe fallback if path is not a valid decodeable image file (e.g. .lnk or .exe)
                }
            }
            return null!;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
