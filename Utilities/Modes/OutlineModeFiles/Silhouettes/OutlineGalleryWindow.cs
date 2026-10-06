using DinoLino.Utilities;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DinoLino
{
    /// <summary>
    /// Batch Workshop ▸ 2D Outlines ▸ Edit. A folder of the silhouettes stored this
    /// session: one thumbnail each, with an editable name, and Duplicate/Delete on
    /// the right-click menu. What survives here is exactly what Export 2D Outlines
    /// writes; an outline's measurements are kept either way, since they live in the
    /// operation history rather than in this store.
    /// </summary>
    internal class OutlineGalleryWindow : Window
    {
        /// One point of one stored outline, as a row of the Coordinates tab.
        private sealed class CoordinateRow
        {
            public string Specimen { get; set; }
            public string Outline { get; set; }
            public int Point { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
        }

        private readonly TabControl _tabs = new TabControl();
        private TabItem _silhouettesTab;

        private readonly DataGrid _coordinates = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE2))
        };

        private Button _exportCoordinates;

        /// Pixel size the silhouettes are rendered at. Larger than the tile so the
        /// preview stays sharp on a high-DPI display.
        private const int ThumbnailPixels = 192;

        /// Side of a tile's picture, in device-independent pixels.
        private const double TileImageSize = 128;

        private readonly WrapPanel _tiles = new WrapPanel();
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

        // Rendered once per outline: renaming does not change the shape, so a rebuild
        // after a delete or a duplicate should not redraw everything.
        private readonly Dictionary<CommittedOutline, BitmapSource> _thumbnails =
            new Dictionary<CommittedOutline, BitmapSource>();

        // Tiles by outline, so selecting one repaints two borders rather than
        // rebuilding the panel and dropping the caret out of a name being typed.
        private readonly Dictionary<CommittedOutline, Border> _tileFor =
            new Dictionary<CommittedOutline, Border>();

        private CommittedOutline _selected;

        // Guards the rename path against re-entering itself: the warning box below
        // moves focus, which raises LostFocus on the very box being validated.
        private bool _renaming;

        private static readonly Brush PictureBorder = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        private static readonly Brush SelectedFill = new SolidColorBrush(Color.FromRgb(0xDC, 0xE8, 0xF7));

        public OutlineGalleryWindow()
        {
            Title = "Stored 2D Outlines";
            Width = 720;
            Height = 560;
            MinWidth = 420;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(12) };
            var gallery = new DockPanel { Margin = new Thickness(8) };

            var note = new TextBlock
            {
                Text = "Only the outlines kept here are written by Export 2D Outlines. " +
                       "Click a thumbnail and press Delete to remove one, or right-click it to " +
                       "duplicate or delete it. Click a name to rename it.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(note, Dock.Top);
            gallery.Children.Add(note);

            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };

            var close = new Button
            {
                Content = "Close",
                MinWidth = 84,
                Padding = new Thickness(12, 4, 12, 4),
                IsCancel = true
            };
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            footer.Children.Add(close);
            footer.Children.Add(_status);

            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            gallery.Children.Add(new ScrollViewer
            {
                Content = _tiles,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });

            _silhouettesTab = new TabItem { Header = "Silhouettes", Content = gallery };
            _tabs.Items.Add(_silhouettesTab);
            _tabs.Items.Add(new TabItem { Header = "Coordinates", Content = BuildCoordinatesTab() });

            root.Children.Add(_tabs);

            Content = root;

            // Delete is handled at the window so it works wherever the focus landed,
            // except inside a name box, where it belongs to the text.
            PreviewKeyDown += Gallery_PreviewKeyDown;

            Rebuild();
        }

        // ---- Layout ----

        // The panel is rebuilt wholesale after a delete or a duplicate: the wrap panel
        // positions the tiles, so inserting one in the middle reflows the rest anyway.
        private void Rebuild()
        {
            _tiles.Children.Clear();
            _tileFor.Clear();

            RefreshCoordinates();

            var stored = CommittedOutlineStore.Outlines;

            if (stored.Count == 0)
            {
                _selected = null;

                _tiles.Children.Add(new TextBlock
                {
                    Text = "No outlines have been stored yet.\n\n" +
                           "Draw an outline, generate its metadata, then use " +
                           "\"Commit Outline to History\" in the Outline panel to keep it.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.6,
                    Margin = new Thickness(4, 10, 4, 0)
                });

                UpdateStatus();
                return;
            }

            foreach (CommittedOutline outline in stored)
                _tiles.Children.Add(BuildTile(outline));

            // A deleted outline must not stay selected.
            if (_selected != null && !_tileFor.ContainsKey(_selected)) _selected = null;

            ApplySelectionVisuals();
            UpdateStatus();
        }

        // ---- Coordinates ----

        // The points of every stored outline, one to a row: the outlines in the order
        // the gallery shows them, and each outline's points in the order they run round
        // it.
        private UIElement BuildCoordinatesTab()
        {
            _coordinates.Columns.Add(TextColumn("specimen", nameof(CoordinateRow.Specimen), null));
            _coordinates.Columns.Add(TextColumn("outline", nameof(CoordinateRow.Outline), null));
            _coordinates.Columns.Add(TextColumn("point", nameof(CoordinateRow.Point), null));
            _coordinates.Columns.Add(TextColumn("x", nameof(CoordinateRow.X), "0.###"));
            _coordinates.Columns.Add(TextColumn("y", nameof(CoordinateRow.Y), "0.###"));

            var panel = new DockPanel { Margin = new Thickness(8) };

            var note = new TextBlock
            {
                Text = "Every point of every stored outline, in the order the points run round it. " +
                       "X runs to the right and Y runs downwards, in the units the outline was " +
                       "drawn in on screen. Select rows and press Ctrl+C to copy them.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(note, Dock.Top);
            panel.Children.Add(note);

            _exportCoordinates = new Button
            {
                Content = "Export CSV…",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Write these coordinates to a CSV file"
            };
            _exportCoordinates.Click += (s, e) => ExportCoordinates();
            DockPanel.SetDock(_exportCoordinates, Dock.Bottom);
            panel.Children.Add(_exportCoordinates);

            panel.Children.Add(_coordinates);
            return panel;
        }

        private static DataGridTextColumn TextColumn(string header, string property, string format)
        {
            var binding = new System.Windows.Data.Binding(property);

            if (format != null)
            {
                binding.StringFormat = format;
                binding.ConverterCulture = System.Globalization.CultureInfo.InvariantCulture;
            }

            return new DataGridTextColumn { Header = header, Binding = binding };
        }

        private static List<CoordinateRow> CoordinateRows()
        {
            var rows = new List<CoordinateRow>();

            foreach (CommittedOutline outline in CommittedOutlineStore.Outlines)
            {
                if (outline.Points == null) continue;

                for (int i = 0; i < outline.Points.Count; i++)
                {
                    rows.Add(new CoordinateRow
                    {
                        Specimen = outline.SpecimenName ?? "",
                        Outline = outline.Name ?? "",
                        Point = i + 1,
                        X = outline.Points[i].X,
                        Y = outline.Points[i].Y
                    });
                }
            }

            return rows;
        }

        // Brought up to date with every change the gallery makes, so the two tabs
        // never disagree about which outlines there are or what they are called.
        private void RefreshCoordinates()
        {
            var rows = CoordinateRows();

            _coordinates.ItemsSource = rows;
            if (_exportCoordinates != null) _exportCoordinates.IsEnabled = rows.Count > 0;
        }

        private void ExportCoordinates()
        {
            var rows = CoordinateRows();
            if (rows.Count == 0) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Outline Coordinates",
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = ".csv",
                FileName = "outline_coordinates.csv",
                AddExtension = true
            };

            if (dialog.ShowDialog(this) != true) return;

            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var text = new System.Text.StringBuilder();
            text.AppendLine("specimen,outline,point,x,y");

            foreach (var row in rows)
            {
                text.Append(CsvField(row.Specimen)).Append(',')
                    .Append(CsvField(row.Outline)).Append(',')
                    .Append(row.Point.ToString(invariant)).Append(',')
                    .Append(row.X.ToString("0.###", invariant)).Append(',')
                    .AppendLine(row.Y.ToString("0.###", invariant));
            }

            try
            {
                // With a byte order mark, so Excel reads names outside ASCII correctly.
                System.IO.File.WriteAllText(
                    dialog.FileName, text.ToString(), new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "The coordinates could not be saved:\n" + ex.Message,
                    "Export Outline Coordinates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static string CsvField(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
                ? value
                : "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private UIElement BuildTile(CommittedOutline outline)
        {
            var picture = new Border
            {
                BorderBrush = PictureBorder,
                BorderThickness = new Thickness(1),
                Width = TileImageSize,
                Height = TileImageSize,
                Child = new Image
                {
                    Source = Thumbnail(outline),
                    Stretch = Stretch.Uniform
                }
            };

            var nameBox = new TextBox
            {
                Text = outline.Name,
                MaxLength = CommittedOutlineStore.MaxNameLength,
                Width = TileImageSize,
                Margin = new Thickness(0, 6, 0, 0),
                TextAlignment = TextAlignment.Center,
                ToolTip = "Click to rename this silhouette"
            };

            nameBox.LostFocus += (s, e) => CommitRename(outline, nameBox);

            nameBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    CommitRename(outline, nameBox);
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    // Abandons the edit. Handling it here also keeps Esc from reaching
                    // the Close button, which would shut the window mid-rename.
                    nameBox.Text = outline.Name;
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
            };

            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(picture);
            panel.Children.Add(nameBox);

            var tile = new Border
            {
                Child = panel,
                Focusable = true,
                Padding = new Thickness(8),
                Margin = new Thickness(4),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                ToolTip = "Specimen: " + (string.IsNullOrWhiteSpace(outline.SpecimenName)
                    ? "(unnamed specimen)"
                    : outline.SpecimenName)
            };

            // Preview, so the click still selects when it lands on the name box, which
            // handles the bubbling event itself. The box takes the focus back on the
            // way up, so clicking a name still starts an edit.
            tile.PreviewMouseLeftButtonDown += (s, e) => Select(outline);
            tile.PreviewMouseRightButtonDown += (s, e) => Select(outline);
            tile.ContextMenu = BuildContextMenu(outline);

            _tileFor[outline] = tile;
            return tile;
        }

        private ContextMenu BuildContextMenu(CommittedOutline outline)
        {
            var menu = new ContextMenu();

            var duplicate = new MenuItem
            {
                Header = "Duplicate",
                ToolTip = "Add an identical silhouette under the next free letter"
            };
            duplicate.Click += (s, e) => DuplicateOutline(outline);
            menu.Items.Add(duplicate);

            var delete = new MenuItem
            {
                Header = "Delete",
                ToolTip = "Remove this silhouette from the session"
            };
            delete.Click += (s, e) => DeleteOutline(outline);
            menu.Items.Add(delete);

            return menu;
        }

        private BitmapSource Thumbnail(CommittedOutline outline)
        {
            BitmapSource cached;
            if (_thumbnails.TryGetValue(outline, out cached)) return cached;

            BitmapSource rendered = OutlineShapeExporter.RenderThumbnail(outline, ThumbnailPixels);
            _thumbnails[outline] = rendered;
            return rendered;
        }

        private void UpdateStatus()
        {
            int n = CommittedOutlineStore.Count;
            _status.Text = n == 1 ? "1 outline stored" : $"{n} outlines stored";
        }

        // ---- Selection ----

        private void Select(CommittedOutline outline)
        {
            _selected = outline;

            Border tile;
            if (outline != null && _tileFor.TryGetValue(outline, out tile))
                tile.Focus();

            ApplySelectionVisuals();
        }

        private void ApplySelectionVisuals()
        {
            foreach (var pair in _tileFor)
            {
                bool chosen = ReferenceEquals(pair.Key, _selected);
                pair.Value.BorderBrush = chosen ? SystemColors.HighlightBrush : Brushes.Transparent;
                pair.Value.Background = chosen ? SelectedFill : Brushes.Transparent;
            }
        }

        // ---- Renaming ----

        /// Applies what was typed, or explains why it could not be applied and puts
        /// the stored name back.
        private void CommitRename(CommittedOutline outline, TextBox box)
        {
            if (_renaming) return;

            string typed = CommittedOutlineStore.CleanName(box.Text);
            if (string.Equals(typed, outline.Name, StringComparison.Ordinal))
            {
                box.Text = outline.Name;   // normalizes surrounding whitespace
                return;
            }

            _renaming = true;
            try
            {
                if (CommittedOutlineStore.Rename(outline, typed))
                {
                    box.Text = outline.Name;   // shows the trimmed and capped form
                    RefreshCoordinates();
                    return;
                }

                MessageBox.Show(
                    this,
                    typed.Length == 0
                        ? "Please enter a name for this outline."
                        : $"\"{typed}\" is already used by another stored outline.",
                    "Rename outline",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                box.Text = outline.Name;
            }
            finally
            {
                _renaming = false;
            }
        }

        // ---- Deleting and duplicating ----

        private void Gallery_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;

            // Delete removes a silhouette only from the tab that shows them.
            if (!ReferenceEquals(_tabs.SelectedItem, _silhouettesTab)) return;

            // Inside a name box Delete edits the text.
            if (Keyboard.FocusedElement is TextBox) return;
            if (_selected == null) return;

            DeleteOutline(_selected);
            e.Handled = true;
        }

        private void DeleteOutline(CommittedOutline outline)
        {
            if (outline == null) return;

            var confirm = MessageBox.Show(
                this,
                $"Delete the stored silhouette \"{outline.Name}\"?\n\n" +
                "It will no longer be written by Export 2D Outlines. The outline's " +
                "measurements are kept.",
                "Delete outline",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            CommittedOutlineStore.Remove(outline);
            _thumbnails.Remove(outline);

            if (ReferenceEquals(outline, _selected)) _selected = null;

            Rebuild();
        }

        private void DuplicateOutline(CommittedOutline outline)
        {
            CommittedOutline copy = CommittedOutlineStore.Duplicate(outline);
            if (copy == null) return;

            // Same shape, so the copy can share the picture already rendered for it.
            BitmapSource existing;
            if (_thumbnails.TryGetValue(outline, out existing))
                _thumbnails[copy] = existing;

            // Select the copy: it is the thing the user just made, and it is the one
            // whose name they are most likely to change next.
            _selected = copy;

            Rebuild();
        }
    }
}