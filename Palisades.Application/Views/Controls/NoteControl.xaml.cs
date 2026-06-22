using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Palisades.Models;
using Palisades.Services;

namespace Palisades.Views.Controls
{
    public partial class NoteControl : UserControl
    {
        private Point _captureStart;
        private double _startLeft, _startTop, _startW, _startH;
        private bool _isDragging, _isResizing;

        private static readonly string[] NoteColors = { "#FFFDE272", "#FFA8D8A8", "#FF8FC5E9", "#FFFFB3BA" };
        private int _colorIndex;

        public NoteItem Note { get; }

        public NoteControl(NoteItem note)
        {
            InitializeComponent();
            Note = note;
            DataContext = note;
            SetBackground(note.Color);
            ContentBox.FontSize = note.FontSize;
        }

        private void SetBackground(string hex)
        {
            try
            {
                var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                MainBorder.Background = new SolidColorBrush(color);
            }
            catch
            {
                MainBorder.Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xE2, 0x72));
            }
        }

        private void UpdateNotePosition()
        {
            Note.X = Canvas.GetLeft(this);
            Note.Y = Canvas.GetTop(this);
        }

        private void UpdateNoteSize()
        {
            Note.Width = Width;
            Note.Height = Height;
        }

        #region Preview-level Drag/Resize

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);

            var pos = e.GetPosition(this);

            // Resize handle hit test (always allow resizing, only gripper is visually hidden)
            double rhLeft = Width - 12, rhTop = Height - 12;
            if (pos.X >= rhLeft && pos.Y >= rhTop)
            {
                _isResizing = true;
                _isDragging = false;
                _startW = Width;
                _startH = Height;
                _captureStart = pos;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            // Header hit test (skip right side buttons, skip double-click for title edit)
            if (pos.Y <= 26 && pos.X < Width - 85 && e.ClickCount == 1)
            {
                _isDragging = true;
                _isResizing = false;
                var canvas = FindParentCanvas();
                if (canvas != null)
                {
                    _captureStart = e.GetPosition(canvas);
                    _startLeft = Canvas.GetLeft(this);
                    _startTop = Canvas.GetTop(this);
                }
                CaptureMouse();
                e.Handled = false;
                return;
            }
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);

            if (_isDragging)
            {
                var canvas = FindParentCanvas();
                if (canvas == null) return;
                var pt = e.GetPosition(canvas);
                double dx = pt.X - _captureStart.X;
                double dy = pt.Y - _captureStart.Y;
                Canvas.SetLeft(this, Math.Max(0, _startLeft + dx));
                Canvas.SetTop(this, Math.Max(0, _startTop + dy));
                UpdateNotePosition();
            }
            else if (_isResizing)
            {
                var pt = e.GetPosition(this);
                double dw = pt.X - _captureStart.X;
                double dh = pt.Y - _captureStart.Y;
                Width = Math.Max(100, _startW + dw);
                Height = Math.Max(80, _startH + dh);
                UpdateNoteSize();
            }
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);

            if (_isDragging)
            {
                _isDragging = false;
                ReleaseMouseCapture();
                UpdateNotePosition();
            }
            else if (_isResizing)
            {
                _isResizing = false;
                ReleaseMouseCapture();
                UpdateNoteSize();
            }
        }

        #endregion

        private bool IsResizeHandleVisible()
        {
            var def = ContainerManager.Instance.LoadDefaults();
            return def?.ShowResizeHandle ?? true;
        }

        #region Focus hack for WS_EX_NOACTIVATE

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private bool _noActivateRemoved;
        private int _noActivateRefCount;

        private void RemoveNoActivate()
        {
            var overlayHwnd = GetOverlayHwnd();
            if (overlayHwnd == IntPtr.Zero) return;

            int exStyle = GetWindowLong(overlayHwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_NOACTIVATE) != 0)
            {
                SetWindowLong(overlayHwnd, GWL_EXSTYLE, exStyle & ~WS_EX_NOACTIVATE);
                _noActivateRemoved = true;
            }
            _noActivateRefCount++;
        }

        private void RestoreNoActivate()
        {
            _noActivateRefCount--;
            if (_noActivateRefCount > 0) return;
            if (!_noActivateRemoved) return;

            var overlayHwnd = GetOverlayHwnd();
            if (overlayHwnd == IntPtr.Zero) return;

            int exStyle = GetWindowLong(overlayHwnd, GWL_EXSTYLE);
            SetWindowLong(overlayHwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            _noActivateRemoved = false;
        }

        private void ContentBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            RemoveNoActivate();
        }

        private void ContentBox_LostFocus(object sender, RoutedEventArgs e)
        {
            RestoreNoActivate();
        }

        private void TitleEditBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            RemoveNoActivate();
        }

        private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
        {
            RestoreNoActivate();
            CommitTitleEdit();
        }

        private IntPtr GetOverlayHwnd()
        {
            var wnd = Window.GetWindow(this);
            return wnd != null ? new WindowInteropHelper(wnd).Handle : IntPtr.Zero;
        }

        #endregion

        #region Title Editing

        private void TitleBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                TitleEditBox.Text = Note.Title;
                TitleBlock.Visibility = Visibility.Collapsed;
                TitleEditBox.Visibility = Visibility.Visible;
                RemoveNoActivate();
                TitleEditBox.Focus();
                TitleEditBox.SelectAll();
                e.Handled = true;
            }
        }

        private void TitleEditBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitTitleEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTitleEdit();
                e.Handled = true;
            }
        }

        private void CommitTitleEdit()
        {
            if (!string.IsNullOrWhiteSpace(TitleEditBox.Text))
            {
                Note.Title = TitleEditBox.Text.Trim();
                TitleBlock.Text = Note.Title;
            }
            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleBlock.Visibility = Visibility.Visible;
        }

        private void CancelTitleEdit()
        {
            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleBlock.Visibility = Visibility.Visible;
        }

        #endregion

        #region Hamburger Menu

        private void HamburgerButton_Click(object sender, RoutedEventArgs e)
        {
            HamburgerPopup.IsOpen = !HamburgerPopup.IsOpen;
        }

        private void FontSize_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && double.TryParse(btn.Tag?.ToString(), out double size))
            {
                Note.FontSize = size;
                ContentBox.FontSize = size;
                HamburgerPopup.IsOpen = false;
            }
        }

        #endregion

        #region Color & Delete

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            _colorIndex = (_colorIndex + 1) % NoteColors.Length;
            Note.Color = NoteColors[_colorIndex];
            SetBackground(Note.Color);
            var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
            overlay?.SaveNotesToDisk();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
            overlay?.RemoveNote(Note);
        }

        #endregion

        #region Helpers

        private Canvas? FindParentCanvas()
        {
            var p = VisualTreeHelper.GetParent(this);
            while (p != null && p is not Canvas)
                p = VisualTreeHelper.GetParent(p);
            return p as Canvas;
        }

        #endregion
    }
}
