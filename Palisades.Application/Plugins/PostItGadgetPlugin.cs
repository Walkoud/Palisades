using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Palisades.Models;
using Palisades.Views;
using Palisades.Views.Controls;

namespace Palisades.Plugins
{
    public class PostItGadgetPlugin : IPlugin
    {
        public string Name => "Post-it Note Gadget";
        public string Id => "com.palisades.plugin.postit";
        public string Version => "1.0.0";
        public string Author => "Palisades Team";
        public string Description => "A premium sticky note gadget supporting rich text formatting, alignment, custom text and background colors.";

        public void OnLoad(PluginContext context)
        {
            context.RegisterGadget(
                gadgetType: "PostIt",
                name: "Post-it Note",
                viewFactory: () => new PostItGadgetView(),
                defaultWidth: 280,
                defaultHeight: 280
            );
        }

        public void OnUnload()
        {
        }
    }

    public class PostItGadgetView : Border, ICustomizableGadgetView
    {
        private readonly RichTextBox _richTextBox;
        private readonly Border _toolbar;
        private readonly Grid _headerGrid;
        private readonly Border _backgroundBorder;
        private readonly DispatcherTimer _saveDebounceTimer;
        private readonly DispatcherTimer _hideToolbarTimer;
        private string _backgroundColor = "#FFE39C";
        private string _textColor = "#000000";
        private double _fontSize = 14;
        private string _fontFamily = "Segoe UI";
        private PluginGadgetItem? _gadgetItem;

        public class PostItSettings
        {
            public string XamlText { get; set; } = "";
            public string BackgroundColor { get; set; } = "#FFE39C";
            public string TextColor { get; set; } = "#000000";
            public double FontSize { get; set; } = 14;
            public string FontFamily { get; set; } = "Segoe UI";
        }

        public PostItGadgetView()
        {
            DataContextChanged += (s, e) =>
            {
                if (DataContext is PluginGadgetItem item)
                {
                    _gadgetItem = item;
                }
            };

            // Make outer Border container transparent and non-interfering
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            CornerRadius = new CornerRadius(0);

            var outerGrid = new Grid();

            // Create background border separate from control visual tree to keep text sharp
            _backgroundBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1)
            };
            outerGrid.Children.Add(_backgroundBorder);

            ApplyBackground(_backgroundColor);

            var mainGrid = new Grid();

            // Create RichTextBox
            _richTextBox = new RichTextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(10, 24, 10, 42), // Pad top for menu, bottom for floating toolbar
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily(_fontFamily),
                FontSize = _fontSize,
                SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x3B, 0x82, 0xF6)),
                IsDocumentEnabled = true // Enable checkbox interactivity!
            };
            _richTextBox.PreviewMouseLeftButtonDown += RichTextBox_PreviewMouseLeftButtonDown;
            _richTextBox.LostFocus += RichTextBox_LostFocus;
            _richTextBox.TextChanged += RichTextBox_TextChanged;
            Unloaded += (s, e) => SaveSettings();

            // CheckBox minimal styling - applied directly so FlowDocument XAML serializes cleanly
            var cbStyle = new Style(typeof(CheckBox));
            cbStyle.Setters.Add(new Setter(CheckBox.MarginProperty, new Thickness(0, 1, 6, 0)));
            cbStyle.Setters.Add(new Setter(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center));
            cbStyle.Setters.Add(new Setter(CheckBox.HorizontalAlignmentProperty, HorizontalAlignment.Left));
            cbStyle.Setters.Add(new Setter(CheckBox.FocusableProperty, false));
            _richTextBox.Resources.Add(typeof(CheckBox), cbStyle);

            // Handle checking/unchecking checkboxes
            _richTextBox.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (e.OriginalSource is CheckBox)
                {
                    TriggerSave();
                }
            }));

            // Handle checklist navigation and smart Enter key
            _richTextBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    var caret = _richTextBox.CaretPosition;
                    var p = caret.Paragraph;
                    if (p != null)
                    {
                        bool hasCb = false;
                        foreach (var inline in p.Inlines)
                        {
                            if (inline is InlineUIContainer uic && uic.Child is CheckBox)
                            {
                                hasCb = true;
                                break;
                            }
                        }

                        if (hasCb)
                        {
                            // Check if current paragraph has no text (only the checkbox inline)
                            var textRange = new TextRange(p.ContentStart, p.ContentEnd);
                            bool isEmpty = string.IsNullOrWhiteSpace(textRange.Text);

                            if (isEmpty)
                            {
                                // Remove the checkbox and clear the list item on empty Enter
                                InlineUIContainer? uicToRemove = null;
                                foreach (var inline in p.Inlines)
                                {
                                    if (inline is InlineUIContainer uic && uic.Child is CheckBox)
                                    {
                                        uicToRemove = uic;
                                        break;
                                    }
                                }
                                if (uicToRemove != null)
                                {
                                    p.Inlines.Remove(uicToRemove);
                                    e.Handled = true;
                                    TriggerSave();
                                    return;
                                }
                            }
                            else
                            {
                                // User pressed Enter in a checklist item with content; auto-spawn checkbox in the next line
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    var newCaret = _richTextBox.CaretPosition;
                                    var newP = newCaret.Paragraph;
                                    if (newP != null && newP != p)
                                    {
                                        bool alreadyHas = false;
                                        foreach (var inline in newP.Inlines)
                                        {
                                            if (inline is InlineUIContainer uic && uic.Child is CheckBox)
                                            {
                                                alreadyHas = true;
                                                break;
                                            }
                                        }
                                        if (!alreadyHas)
                                        {
                                            var cb = new CheckBox
                                            {
                                                Margin = new Thickness(0, 1, 6, 0),
                                                VerticalAlignment = VerticalAlignment.Center,
                                                Focusable = false
                                            };
                                            var container = new InlineUIContainer(cb);
                                            if (newP.Inlines.FirstInline != null)
                                                newP.Inlines.InsertBefore(newP.Inlines.FirstInline, container);
                                            else
                                                newP.Inlines.Add(container);

                                            TriggerSave();
                                        }
                                    }
                                }), DispatcherPriority.Input);
                            }
                        }
                    }
                }
            };

            // --- Hamburger menu for background colors (top right) ---
            _headerGrid = new Grid
            {
                Height = 24,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
                Opacity = 0.0
            };

            // Setup a premium style for our toolbar/hamburger buttons
            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.Transparent));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            btnStyle.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(0)));
            btnStyle.Setters.Add(new Setter(Button.WidthProperty, 26.0));
            btnStyle.Setters.Add(new Setter(Button.HeightProperty, 26.0));
            btnStyle.Setters.Add(new Setter(Button.MinWidthProperty, 0.0));
            btnStyle.Setters.Add(new Setter(Button.MinHeightProperty, 0.0));
            btnStyle.Setters.Add(new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            btnStyle.Setters.Add(new Setter(Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.FocusableProperty, false));

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(0));

            var contentPresenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenterFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenterFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentPresenterFactory);

            template.VisualTree = borderFactory;
            btnStyle.Setters.Add(new Setter(Button.TemplateProperty, template));

            var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))));
            btnStyle.Triggers.Add(hoverTrigger);

            var bgMenuButton = new Button
            {
                Content = "☰",
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)),
                Cursor = Cursors.Hand,
                FontSize = 12,
                ToolTip = "Note Color",
                Style = btnStyle,
                Margin = new Thickness(0, 2, 6, 0)
            };

            var bgContextMenu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1E, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };
            string[] bgColors = { "#FFE39C", "#FFCCD5", "#D4ECD5", "#CFE8FC", "#ECD4FC" };
            string[] bgTooltips = { "Yellow Notes", "Pink Notes", "Green Notes", "Blue Notes", "Purple Notes" };
            for (int i = 0; i < bgColors.Length; i++)
            {
                string col = bgColors[i];
                var mi = new MenuItem
                {
                    Header = bgTooltips[i],
                    Foreground = Brushes.White,
                    Height = 28,
                    Focusable = false
                };
                var preview = new Border
                {
                    Width = 14, Height = 14,
                    CornerRadius = new CornerRadius(7),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(col)),
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                mi.Icon = preview;
                mi.Click += (s, e) =>
                {
                    _backgroundColor = col;
                    ApplyBackground(col);
                    TriggerSave();
                };
                bgContextMenu.Items.Add(mi);
            }
            bgMenuButton.Click += (s, e) =>
            {
                bgContextMenu.PlacementTarget = bgMenuButton;
                bgContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                bgContextMenu.IsOpen = true;
            };
            _headerGrid.Children.Add(bgMenuButton);

            // --- Floating Glassmorphic Toolbar (bottom) ---
            _toolbar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Height = 32,
                Margin = new Thickness(8, 0, 8, 8),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.0,
                Visibility = Visibility.Collapsed,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 8,
                    ShadowDepth = 1,
                    Opacity = 0.3
                }
            };

            var toolbarPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 2, 4, 2)
            };
            _toolbar.Child = toolbarPanel;

            // Bold
            var btnBold = new Button { Content = "B", FontWeight = FontWeights.Bold, ToolTip = "Bold", Style = btnStyle };
            btnBold.Click += (s, e) =>
            {
                var curWeight = _richTextBox.Selection.GetPropertyValue(TextElement.FontWeightProperty);
                var newWeight = (curWeight is FontWeight w && w == FontWeights.Bold) ? FontWeights.Normal : FontWeights.Bold;
                _richTextBox.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, newWeight);
                _richTextBox.Focus();
                TriggerSave();
            };
            toolbarPanel.Children.Add(btnBold);

            // Italic
            var btnItalic = new Button { Content = "I", FontStyle = FontStyles.Italic, ToolTip = "Italic", Style = btnStyle };
            btnItalic.Click += (s, e) =>
            {
                var curStyle = _richTextBox.Selection.GetPropertyValue(TextElement.FontStyleProperty);
                var newStyle = (curStyle is FontStyle st && st == FontStyles.Italic) ? FontStyles.Normal : FontStyles.Italic;
                _richTextBox.Selection.ApplyPropertyValue(TextElement.FontStyleProperty, newStyle);
                _richTextBox.Focus();
                TriggerSave();
            };
            toolbarPanel.Children.Add(btnItalic);

            // Underline
            var btnUnderline = new Button { ToolTip = "Underline", Style = btnStyle };
            btnUnderline.Content = new TextBlock
            {
                Text = "U",
                TextDecorations = TextDecorations.Underline,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            btnUnderline.Click += (s, e) =>
            {
                var curDecor = _richTextBox.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
                bool hasUnderline = false;
                if (curDecor is TextDecorationCollection decors && decors.Count > 0)
                {
                    hasUnderline = true;
                }
                var newDecor = hasUnderline ? null : TextDecorations.Underline;
                _richTextBox.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, newDecor);
                _richTextBox.Focus();
                TriggerSave();
            };
            toolbarPanel.Children.Add(btnUnderline);

            toolbarPanel.Children.Add(CreateSeparator());

            // Bullet List
            var btnBullet = new Button { Content = "•", ToolTip = "Bullet List", FontWeight = FontWeights.Bold, Style = btnStyle };
            btnBullet.Click += (s, e) =>
            {
                _richTextBox.Focus();
                if (EditingCommands.ToggleBullets.CanExecute(null, _richTextBox))
                    EditingCommands.ToggleBullets.Execute(null, _richTextBox);
                TriggerSave();
            };
            toolbarPanel.Children.Add(btnBullet);

            // Numbered List
            var btnNumbered = new Button { Content = "1.", ToolTip = "Numbered List", FontWeight = FontWeights.Bold, Style = btnStyle };
            btnNumbered.Click += (s, e) =>
            {
                _richTextBox.Focus();
                if (EditingCommands.ToggleNumbering.CanExecute(null, _richTextBox))
                    EditingCommands.ToggleNumbering.Execute(null, _richTextBox);
                TriggerSave();
            };
            toolbarPanel.Children.Add(btnNumbered);

            // Task List (checkbox)
            var btnTask = new Button { Content = "☑", ToolTip = "Task List", FontWeight = FontWeights.Bold, Style = btnStyle };
            btnTask.Click += (s, e) => ToggleTaskList();
            toolbarPanel.Children.Add(btnTask);

            toolbarPanel.Children.Add(CreateSeparator());

            // Increase Font Size
            var btnIncSize = new Button { Content = "A+", ToolTip = "Increase Size", Style = btnStyle };
            btnIncSize.Click += (s, e) => ChangeFontSize(2);
            toolbarPanel.Children.Add(btnIncSize);

            // Decrease Font Size
            var btnDecSize = new Button { Content = "A-", ToolTip = "Decrease Size", Style = btnStyle };
            btnDecSize.Click += (s, e) => ChangeFontSize(-2);
            toolbarPanel.Children.Add(btnDecSize);

            toolbarPanel.Children.Add(CreateSeparator());

            // Text Color buttons
            string[] textColors = { "#000000", "#EF4444", "#3B82F6", "#10B981" };
            string[] textColorTooltips = { "Black", "Red", "Blue", "Green" };
            for (int i = 0; i < textColors.Length; i++)
            {
                string col = textColors[i];
                var colBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(col));
                var btnCol = new Button
                {
                    Width = 16, Height = 16,
                    Margin = new Thickness(3, 5, 3, 5),
                    ToolTip = textColorTooltips[i],
                    Focusable = false
                };

                var colTemplate = new ControlTemplate(typeof(Button));
                var ellipseFactory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
                ellipseFactory.SetValue(System.Windows.Shapes.Ellipse.FillProperty, colBrush);
                ellipseFactory.SetValue(System.Windows.Shapes.Ellipse.StrokeProperty, Brushes.White);
                ellipseFactory.SetValue(System.Windows.Shapes.Ellipse.StrokeThicknessProperty, 1.0);
                colTemplate.VisualTree = ellipseFactory;

                btnCol.Template = colTemplate;
                btnCol.Click += (s, e) => SetSelectionTextColor((Color)ColorConverter.ConvertFromString(col));
                toolbarPanel.Children.Add(btnCol);
            }

            // Mouse hover show/hide toolbar with 2s delay
            _hideToolbarTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _hideToolbarTimer.Tick += (s, e) =>
            {
                _hideToolbarTimer.Stop();
                _toolbar.Opacity = 0.0;
                _toolbar.Visibility = Visibility.Collapsed;
                _headerGrid.Opacity = 0.0;
                _headerGrid.Visibility = Visibility.Collapsed;
            };

            MouseEnter += (s, e) =>
            {
                _hideToolbarTimer.Stop();
                _toolbar.Visibility = Visibility.Visible;
                _toolbar.Opacity = 1.0;
                _headerGrid.Visibility = Visibility.Visible;
                _headerGrid.Opacity = 1.0;
            };
            MouseLeave += (s, e) =>
            {
                _hideToolbarTimer.Interval = TimeSpan.FromMilliseconds(2000);
                _hideToolbarTimer.Start();
            };

            mainGrid.Children.Add(_richTextBox);
            mainGrid.Children.Add(_headerGrid);
            mainGrid.Children.Add(_toolbar);
            outerGrid.Children.Add(mainGrid);
            Child = outerGrid;

            _saveDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _saveDebounceTimer.Tick += SaveDebounceTimer_Tick;
        }

        private Border CreateSeparator()
        {
            return new Border
            {
                Width = 1,
                Height = 16,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private void ToggleTaskList()
        {
            _richTextBox.Focus();
            var selection = _richTextBox.Selection;
            if (selection == null) return;

            var startPos = selection.Start;
            var endPos = selection.End;

            var pointer = startPos;
            var paragraphs = new System.Collections.Generic.List<Paragraph>();

            while (pointer != null && pointer.CompareTo(endPos) <= 0)
            {
                if (pointer.Parent is Paragraph p && !paragraphs.Contains(p))
                {
                    paragraphs.Add(p);
                }
                else if (pointer.Parent is ListItem li && li.Blocks.FirstBlock is Paragraph lp && !paragraphs.Contains(lp))
                {
                    paragraphs.Add(lp);
                }
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }

            if (paragraphs.Count == 0 && _richTextBox.CaretPosition.Paragraph != null)
            {
                paragraphs.Add(_richTextBox.CaretPosition.Paragraph);
            }

            foreach (var p in paragraphs)
            {
                InlineUIContainer? existingUic = null;
                foreach (var inline in p.Inlines)
                {
                    if (inline is InlineUIContainer uic && uic.Child is CheckBox)
                    {
                        existingUic = uic;
                        break;
                    }
                }

                if (existingUic != null)
                {
                    p.Inlines.Remove(existingUic);
                }
                else
                {
                    var cb = new CheckBox
                    {
                        Margin = new Thickness(0, 1, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Focusable = false
                    };
                    var container = new InlineUIContainer(cb);

                    if (p.Inlines.FirstInline != null)
                        p.Inlines.InsertBefore(p.Inlines.FirstInline, container);
                    else
                        p.Inlines.Add(container);
                }
            }

            TriggerSave();
        }

        private void ApplyBackground(string hexColor)
        {
            var baseColor = (Color)ColorConverter.ConvertFromString(hexColor);
            
            var lightColor = Color.FromArgb(
                255,
                (byte)Math.Min(255, baseColor.R + 15),
                (byte)Math.Min(255, baseColor.G + 15),
                (byte)Math.Min(255, baseColor.B + 15)
            );
            var darkColor = Color.FromArgb(
                255,
                (byte)Math.Max(0, baseColor.R - 15),
                (byte)Math.Max(0, baseColor.G - 15),
                (byte)Math.Max(0, baseColor.B - 15)
            );

            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            brush.GradientStops.Add(new GradientStop(lightColor, 0.0));
            brush.GradientStops.Add(new GradientStop(baseColor, 0.5));
            brush.GradientStops.Add(new GradientStop(darkColor, 1.0));
            
            _backgroundBorder.Background = brush;

            var borderColor = Color.FromArgb(
                0x90,
                (byte)Math.Max(0, baseColor.R - 40),
                (byte)Math.Max(0, baseColor.G - 40),
                (byte)Math.Max(0, baseColor.B - 40)
            );
            _backgroundBorder.BorderBrush = new SolidColorBrush(borderColor);
            
            _backgroundBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 12,
                ShadowDepth = 3,
                Opacity = 0.18,
                Direction = 270
            };
        }

        private void SetSelectionTextColor(Color color)
        {
            _richTextBox.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color));
            _richTextBox.Focus();
            TriggerSave();
        }

        public void ApplyCustomSettings(string customData)
        {
            try
            {
                if (string.IsNullOrEmpty(customData)) return;
                var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<PostItSettings>(customData);
                if (settings != null)
                {
                    _backgroundColor = settings.BackgroundColor;
                    _textColor = settings.TextColor;
                    _fontSize = settings.FontSize;
                    _fontFamily = settings.FontFamily;

                    ApplyBackground(_backgroundColor);

                    string currentXaml = GetXamlText();
                    if (!_richTextBox.IsFocused && settings.XamlText != currentXaml)
                    {
                        _richTextBox.TextChanged -= RichTextBox_TextChanged;
                        SetXamlText(settings.XamlText);
                        _richTextBox.TextChanged += RichTextBox_TextChanged;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PostIt] ApplyCustomSettings failed: {ex.Message}");
                App.Log(ex, "[PostIt] ApplyCustomSettings");
            }
        }

        private string GetXamlText()
        {
            try
            {
                return System.Windows.Markup.XamlWriter.Save(_richTextBox.Document);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PostIt] GetXamlText failed: {ex.Message}");
                App.Log(ex, "[PostIt] GetXamlText");
                return "";
            }
        }

        private void SetXamlText(string xamlText)
        {
            if (string.IsNullOrEmpty(xamlText))
            {
                _richTextBox.Document = new FlowDocument();
                return;
            }
            try
            {
                if (xamlText.StartsWith("PKG:"))
                {
                    var bytes = Convert.FromBase64String(xamlText.Substring(4));
                    var range = new TextRange(_richTextBox.Document.ContentStart, _richTextBox.Document.ContentEnd);
                    using (var ms = new MemoryStream(bytes))
                    {
                        range.Load(ms, DataFormats.XamlPackage);
                    }
                }
                else if (xamlText.StartsWith("<FlowDocument"))
                {
                    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(xamlText)))
                    {
                        var doc = (FlowDocument)System.Windows.Markup.XamlReader.Load(ms);
                        _richTextBox.Document = doc;
                    }
                }
                else
                {
                    var range = new TextRange(_richTextBox.Document.ContentStart, _richTextBox.Document.ContentEnd);
                    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(xamlText)))
                    {
                        range.Load(ms, DataFormats.Xaml);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PostIt] SetXamlText failed: {ex.Message}");
                App.Log(ex, "[PostIt] SetXamlText");
                _richTextBox.Document = new FlowDocument();
            }
        }

        private void RichTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TriggerSave();
        }

        private void TriggerSave()
        {
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }

        private void SaveDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _saveDebounceTimer.Stop();
            SaveSettings();
        }

        private void SaveSettings()
        {
            try
            {
                var item = GetGadgetItem();
                if (item != null)
                {
                    var settings = new PostItSettings
                    {
                        XamlText = GetXamlText(),
                        BackgroundColor = _backgroundColor,
                        TextColor = _textColor,
                        FontSize = _fontSize,
                        FontFamily = _fontFamily
                    };
                    item.CustomData = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
                    var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
                    overlay?.SaveGadgetsToDisk();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PostIt] SaveSettings failed: {ex.Message}");
                App.Log(ex, "[PostIt] SaveSettings");
            }
        }

        private PluginGadgetItem? GetGadgetItem()
        {
            if (_gadgetItem != null)
                return _gadgetItem;

            if (DataContext is PluginGadgetItem item)
            {
                _gadgetItem = item;
                return item;
            }

            DependencyObject parent = VisualTreeHelper.GetParent(this);
            while (parent != null)
            {
                if (parent is PluginGadgetWrapper wrapper)
                {
                    _gadgetItem = wrapper.GadgetItem;
                    return _gadgetItem;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
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

        private void RichTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            RemoveNoActivate();
            _hideToolbarTimer.Stop();
            _toolbar.Visibility = Visibility.Visible;
            _toolbar.Opacity = 1.0;
            _headerGrid.Visibility = Visibility.Visible;
            _headerGrid.Opacity = 1.0;
        }

        private void RichTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            RestoreNoActivate();
            if (!IsMouseOver)
            {
                _hideToolbarTimer.Stop();
                _hideToolbarTimer.Interval = TimeSpan.FromMilliseconds(150);
                _hideToolbarTimer.Start();
            }
            SaveSettings();
        }

        private IntPtr GetOverlayHwnd()
        {
            var wnd = Window.GetWindow(this);
            return wnd != null ? new WindowInteropHelper(wnd).Handle : IntPtr.Zero;
        }

        private void ChangeFontSize(double delta)
        {
            var curSize = _richTextBox.Selection.GetPropertyValue(TextElement.FontSizeProperty);
            double size = 14;
            if (curSize is double d)
            {
                size = d;
            }
            else if (curSize == DependencyProperty.UnsetValue)
            {
                var start = _richTextBox.Selection.Start;
                var startSize = start.Parent?.GetValue(TextElement.FontSizeProperty);
                if (startSize is double sd) size = sd;
            }
            size = Math.Clamp(size + delta, 8, 72);
            _richTextBox.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
            _richTextBox.Focus();
            TriggerSave();
        }

        #endregion
    }
}
