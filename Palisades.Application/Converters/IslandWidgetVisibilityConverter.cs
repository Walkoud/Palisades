using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Palisades.Converters
{
    /// <summary>Shows the stacked island view when it is the selected widget.
    /// NOTE : IsExpanded (values[2]) volontairement ignoré. Le panneau parent
    /// gère déjà le masquage (fade + collapse). Le replier ici aussi réduisait
    /// le layout en pleine fermeture = visible en 2 fois. Vues toujours
    /// chargées (pas de lag), masquées par le parent quand replié.</summary>
    public class IslandWidgetVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && ReferenceEquals(values[0], values[1]))
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
