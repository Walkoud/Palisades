using System.Windows;

namespace Palisades.Views
{
    public static class ContainerVisuals
    {
        public static readonly DependencyProperty ShowShortcutArrowProperty =
            DependencyProperty.RegisterAttached(
                "ShowShortcutArrow",
                typeof(bool),
                typeof(ContainerVisuals),
                new FrameworkPropertyMetadata(
                    true,
                    FrameworkPropertyMetadataOptions.Inherits));

        public static void SetShowShortcutArrow(DependencyObject element, bool value)
            => element.SetValue(ShowShortcutArrowProperty, value);

        public static bool GetShowShortcutArrow(DependencyObject element)
            => (bool)element.GetValue(ShowShortcutArrowProperty);
    }
}
