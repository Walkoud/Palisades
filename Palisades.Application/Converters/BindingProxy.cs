using System.Windows;
using System.Windows.Media;

namespace Palisades.Converters
{
    /// <summary>
    /// Freezable proxy that inherits its parent's DataContext and exposes it as a DependencyProperty.
    /// Used to bridge DataContext from a UserControl/Window into a DataTemplate
    /// where RelativeSource/ElementName bindings fail.
    /// </summary>
    public class BindingProxy : Freezable
    {
        protected override Freezable CreateInstanceCore() => new BindingProxy();

        public object Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), null);
    }
}
