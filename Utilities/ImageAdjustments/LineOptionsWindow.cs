using DinoLino.Utilities.Modes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities
{
    /// <summary>One color the Line Options window offers, and the name it is kept under.</summary>
    public sealed class LineColorChoice
    {
        public LineColorChoice(string name, string tag)
        {
            Name = name;
            Tag = tag;
            Brush = (Brush)new BrushConverter().ConvertFromString(tag);
        }

        /// <summary>What the window shows, which is not always what the tag says.</summary>
        public string Name { get; }

        /// <summary>The color's own name, which is what a settings file keeps.</summary>
        public string Tag { get; }

        public Brush Brush { get; }
    }

    /// <summary>
    /// The colors on offer and the way a stored one is found again. Kept apart from
    /// the window so a settings file can name a color without one being open.
    /// </summary>
    public static class LinePalette
    {
        public static readonly IReadOnlyList<LineColorChoice> Colors = new List<LineColorChoice>
        {
            new LineColorChoice("Red", "Red"),
            new LineColorChoice("Orange", "Orange"),
            new LineColorChoice("Yellow", "Yellow"),
            new LineColorChoice("Green", "Green"),

            new LineColorChoice("Cyan", "Cyan"),
            new LineColorChoice("Blue", "Blue"),
            new LineColorChoice("Purple", "Purple"),
            new LineColorChoice("Pink", "Pink"),

            new LineColorChoice("Black", "Black"),
            new LineColorChoice("Grey", "Gray"),
            new LineColorChoice("White", "White"),
            new LineColorChoice("Brown", "Brown")
        };

        /// <summary>The color a stored name means, or null when it names none of them.</summary>
        public static LineColorChoice Find(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;

            foreach (var choice in Colors)
            {
                if (string.Equals(choice.Tag, tag, StringComparison.OrdinalIgnoreCase)) return choice;
            }

            return null;
        }
    }

    /// <summary>
    /// Settings ▸ Line Options: the color and the thickness every work mode draws at.
    /// Both take effect as they are chosen, so the window can be left open while the
    /// user looks at what a change does to the image underneath.
    /// </summary>
    public sealed class LineOptionsWindow : Window
    {
        private const double SwatchSize = 26;

        /// Colors to a row. Twelve colors over four columns gives three rows: the
        /// eight chromatic ones, then black, grey, white and brown.
        private const int ColorColumns = 4;

        private const double PreviewWidth = 288;
        private const double PreviewHeight = 48;

        /// <summary>Called with the chosen color as soon as it is chosen.</summary>
        public Action<LineColorChoice> OnColorChanged;

        /// <summary>Called with the chosen thickness as the slider moves.</summary>
        public Action<double> OnThicknessChanged;

        private readonly ListBox _colors;
        private readonly Slider _thickness;
        private readonly TextBlock _thicknessLabel;
        private readonly Line _preview;

        // True while the window is being filled in, when the controls are moving
        // because of the values handed in rather than because the user moved them.
        private bool _settingUp;

        public LineOptionsWindow(string colorTag, double thickness)
        {
            Title = "Line Options";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _settingUp = true;

            _colors = BuildColorList();
            _thickness = BuildThicknessSlider();
            _thicknessLabel = new TextBlock { MinWidth = 34, VerticalAlignment = VerticalAlignment.Center };
            _preview = new Line { X1 = 12, Y1 = PreviewHeight / 2, X2 = PreviewWidth - 12, Y2 = PreviewHeight / 2 };

            Content = BuildBody();

            var chosen = LinePalette.Find(colorTag) ?? LinePalette.Colors[0];
            _colors.SelectedItem = chosen;
            _thickness.Value = WorkMode.ClipThickness(thickness);

            _settingUp = false;

            RefreshPreview();
        }

        // =====================
        // Building
        // =====================

        private ListBox BuildColorList()
        {
            var list = new ListBox
            {
                SelectionMode = SelectionMode.Single,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Width = PreviewWidth
            };

            list.ItemsPanel = BuildGridTemplate();

            foreach (var choice in LinePalette.Colors)
                list.Items.Add(choice);

            list.ItemTemplate = BuildSwatchTemplate();
            list.ItemContainerStyle = BuildContainerStyle();
            list.SelectionChanged += (s, e) => ColorChosen();

            // Without this the list measures itself against infinite width, and the
            // grid stretches into one row instead of breaking into three.
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

            return list;
        }

        // Each cell fills its square of the grid and holds its content at the top,
        // so a swatch sits in the same place in every one of them. Left to itself a
        // cell shrinks to fit its name and sits to one side, which is what puts the
        // colors of one row out of line with the row above.
        private static Style BuildContainerStyle()
        {
            var style = new Style(typeof(ListBoxItem));

            style.Setters.Add(new Setter(
                Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

            style.Setters.Add(new Setter(
                Control.VerticalContentAlignmentProperty, VerticalAlignment.Top));

            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));

            return style;
        }

        // A swatch above its name, so a color is picked by looking rather than reading.
        private static DataTemplate BuildSwatchTemplate()
        {
            var swatch = new FrameworkElementFactory(typeof(Border));
            swatch.SetValue(Border.WidthProperty, SwatchSize);
            swatch.SetValue(Border.HeightProperty, SwatchSize);
            swatch.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            swatch.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Top);
            swatch.SetValue(Border.BorderBrushProperty, Brushes.Gray);
            swatch.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            swatch.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            swatch.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Brush"));

            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
            name.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            name.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);

            // A long name at a large font wraps inside its own column rather than
            // running over the color beside it.
            name.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            name.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));

            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(StackPanel.MarginProperty, new Thickness(3));
            stack.SetValue(StackPanel.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            stack.AppendChild(swatch);
            stack.AppendChild(name);

            return new DataTemplate { VisualTree = stack };
        }

        // A uniform grid rather than a wrapping one, so the rows break where they
        // are meant to whatever the names measure at.
        private static ItemsPanelTemplate BuildGridTemplate()
        {
            var panel = new FrameworkElementFactory(typeof(UniformGrid));
            panel.SetValue(UniformGrid.ColumnsProperty, ColorColumns);

            return new ItemsPanelTemplate { VisualTree = panel };
        }

        private Slider BuildThicknessSlider()
        {
            var slider = new Slider
            {
                Minimum = WorkMode.MinLineThickness,
                Maximum = WorkMode.MaxLineThickness,
                TickFrequency = 0.5,
                IsSnapToTickEnabled = true,
                Width = PreviewWidth - 44,
                VerticalAlignment = VerticalAlignment.Center
            };

            slider.ValueChanged += (s, e) => ThicknessChosen();
            return slider;
        }

        private UIElement BuildBody()
        {
            var thicknessRow = new StackPanel { Orientation = Orientation.Horizontal };
            thicknessRow.Children.Add(_thickness);
            thicknessRow.Children.Add(_thicknessLabel);

            var previewFrame = new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 4, 0, 0),
                Child = new Canvas
                {
                    Width = PreviewWidth,
                    Height = PreviewHeight,
                    Background = Brushes.White,
                    Children = { _preview }
                }
            };

            var accept = new Button
            {
                Content = "Accept",
                Padding = new Thickness(16, 4, 16, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            accept.Click += (s, e) => Close();

            var body = new StackPanel { Margin = new Thickness(14) };
            body.Children.Add(Heading("Color"));
            body.Children.Add(_colors);
            body.Children.Add(Heading("Thickness"));
            body.Children.Add(thicknessRow);
            body.Children.Add(previewFrame);
            body.Children.Add(accept);

            return body;
        }

        private static TextBlock Heading(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 4)
        };

        // =====================
        // Choosing
        // =====================

        private void ColorChosen()
        {
            RefreshPreview();

            if (_settingUp) return;

            var chosen = _colors.SelectedItem as LineColorChoice;
            if (chosen != null && OnColorChanged != null) OnColorChanged(chosen);
        }

        private void ThicknessChosen()
        {
            RefreshPreview();

            if (_settingUp) return;
            if (OnThicknessChanged != null) OnThicknessChanged(_thickness.Value);
        }

        private void RefreshPreview()
        {
            var chosen = _colors.SelectedItem as LineColorChoice;

            _preview.Stroke = chosen != null ? chosen.Brush : Brushes.Black;
            _preview.StrokeThickness = _thickness.Value;

            _thicknessLabel.Text = _thickness.Value.ToString("0.#", CultureInfo.CurrentCulture);
        }
    }
}
