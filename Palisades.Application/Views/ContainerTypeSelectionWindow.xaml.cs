using System.Windows;
using System.Windows.Input;

namespace Palisades.Views
{
    public enum SelectedContainerType
    {
        None,
        Normal,
        SvgButton,
        FolderPortal,
        AndroidFolder
    }

    public partial class ContainerTypeSelectionWindow : Window
    {
        public SelectedContainerType ResultType { get; private set; } = SelectedContainerType.None;

        public ContainerTypeSelectionWindow()
        {
            InitializeComponent();
        }

        private void Title_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Normal_Click(object sender, RoutedEventArgs e)
        {
            ResultType = SelectedContainerType.Normal;
            DialogResult = true;
            Close();
        }

        private void SvgButton_Click(object sender, RoutedEventArgs e)
        {
            ResultType = SelectedContainerType.SvgButton;
            DialogResult = true;
            Close();
        }

        private void FolderPortal_Click(object sender, RoutedEventArgs e)
        {
            ResultType = SelectedContainerType.FolderPortal;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            ResultType = SelectedContainerType.None;
            DialogResult = false;
            Close();
        }
    }
}
