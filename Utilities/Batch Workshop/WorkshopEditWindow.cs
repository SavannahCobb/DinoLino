using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Edit window for one Batch Workshop category. Shows the category's whole wide
    /// table — every variable of that mode as a column, every attempt as a row, with
    /// the specimen group columns between Attempt and the measurements — and allows
    /// deleting rows, hiding columns, and deleting specimens.
    /// </summary>
    public class WorkshopEditWindow : Window
    {
        private readonly WorkshopCategory _category;
        private readonly UndoRedoManager _undoRedo;
        private readonly string _currentName;
        private readonly ScaleCalibration _scale;

        private readonly StackPanel _body = new StackPanel();
        private readonly List<WorkOperation> _removed = new List<WorkOperation>();

        // Grid appearance.
        private static readonly Brush GridLine = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
        private static readonly Brush SpecimenFill = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));

        /// Operations deleted while the window was open, so the caller can drop their
        /// visuals from the workspace.
        public IReadOnlyList<WorkOperation> RemovedOperations => _removed;

        public WorkshopEditWindow(
            WorkshopCategory category, UndoRedoManager undoRedo, string currentName, ScaleCalibration scale)
        {
            _category = category;
            _undoRedo = undoRedo;
            _currentName = currentName;
            _scale = scale;

            Title = "Edit " + WorkshopTables.TitleFor(category);
            Width = 1000;
            Height = 620;
            MinWidth = 560;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(12) };

            var note = new TextBlock
            {
                Text = "Deleting a row or specimen is permanent and cannot be undone with " +
                       "Ctrl+Z. Hiding a column only removes it from the table and its export. " +
                       "Group columns are set from the Sample tab and cannot be hidden here. " +
                       "Add column makes a variable of your own from a formula; it is recalculated " +
                       "every time the table is built, and the ✎ on its header edits it.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var addColumn = new Button
            {
                Content = "Add column",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Make a new variable from a formula over this table's columns"
            };
            addColumn.Click += (s, e) => EditFormulaColumn(null);
            footer.Children.Add(addColumn);

            var restore = new Button
            {
                Content = "Restore hidden columns",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            restore.Click += (s, e) =>
            {
                WorkshopColumnFilter.Restore(WorkshopTables.KeyFor(_category));
                Rebuild();
            };
            footer.Children.Add(restore);

            var close = new Button
            {
                Content = "Close",
                MinWidth = 84,
                Padding = new Thickness(12, 4, 12, 4),
                IsCancel = true
            };
            close.Click += (s, e) => Close();
            footer.Children.Add(close);

            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            root.Children.Add(new ScrollViewer
            {
                Content = _body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            Content = root;
            Rebuild();

            // The wide tables scroll sideways under a two-finger gesture, a tilt
            // wheel, or Shift+wheel.
            MainWindow.AttachHorizontalWheel(this);
        }

        // The table is rebuilt wholesale after every edit: deletions change how the
        // attempts line up, so patching in place would be more fragile than redrawing.
        private void Rebuild()
        {
            _body.Children.Clear();

            var table = WorkshopTables.Build(_category, _undoRedo, _currentName, _scale);

            if (table.IsEmpty)
            {
                _body.Children.Add(new TextBlock
                {
                    Text = "No measurements of this kind have been recorded yet.",
                    Opacity = 0.6,
                    Margin = new Thickness(0, 10, 0, 0)
                });
            }
            else
            {
                _body.Children.Add(BuildGrid(table));
            }

            var missing = MissingFormulaColumns(table);
            if (missing.Count > 0) _body.Children.Add(MissingPanel(missing));
        }

        // ---- Formula columns ----

        // This table's formula columns, ready to look up by header.
        private Dictionary<string, WorkshopFormulaColumn> FormulaColumns()
        {
            var columns = new Dictionary<string, WorkshopFormulaColumn>(StringComparer.OrdinalIgnoreCase);

            foreach (var column in WorkshopFormulas.ColumnsFor(WorkshopTables.KeyFor(_category)))
                columns[column.Name] = column;

            return columns;
        }

        // Formula columns this table cannot calculate, because a column their formula
        // names is not one of its own. They are listed under the table so they can
        // still be corrected or removed.
        private List<WorkshopFormulaColumn> MissingFormulaColumns(WorkshopTable table)
        {
            var shown = new HashSet<string>(table.MeasurementHeaders, StringComparer.OrdinalIgnoreCase);

            return WorkshopFormulas
                .ColumnsFor(WorkshopTables.KeyFor(_category))
                .Where(c => !shown.Contains(c.Name))
                .ToList();
        }

        private UIElement MissingPanel(List<WorkshopFormulaColumn> columns)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

            panel.Children.Add(new TextBlock
            {
                Text = "Not calculated in this table, because a column the formula names is missing:",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });

            foreach (var column in columns)
            {
                var line = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 4, 0, 0)
                };

                line.Children.Add(new TextBlock
                {
                    Text = column.Name + "  =  " + column.Text,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var fix = SmallButton("\u270E", "Edit or delete this formula column");
                var captured = column;
                fix.Click += (s, e) => EditFormulaColumn(captured);
                line.Children.Add(fix);

                panel.Children.Add(line);
            }

            return panel;
        }

        /// Opens the formula dialog for a new column, or for one the table already has.
        private void EditFormulaColumn(WorkshopFormulaColumn existing)
        {
            string key = WorkshopTables.KeyFor(_category);

            // The dialog offers this table's columns and previews the rows the formula
            // would produce, so it is given the table as it currently stands.
            var table = WorkshopTables.Build(_category, _undoRedo, _currentName, _scale);

            var window = new WorkshopFormulaWindow(table, existing)
            {
                Owner = this,
                FontSize = FontSize,
                FontFamily = FontFamily
            };

            if (window.ShowDialog() != true) return;

            if (window.DeleteRequested)
            {
                WorkshopFormulas.Remove(existing);
                Rebuild();
                return;
            }

            WorkshopFormulas.Save(
                existing,
                key,
                WorkshopFormulaWindow.KeptName(this, existing, window.ColumnName),
                window.Result);

            Rebuild();
        }

        // ---- Grid rendering ----

        private UIElement BuildGrid(WorkshopTable table)
        {
            var visible = table.VisibleColumnIndexes();
            var formulaColumns = FormulaColumns();
            var groupColumns = SpecimenGroups.Columns;
            int groupCount = groupColumns.Count;

            // Where the measurement columns begin, once Specimen, Attempt and the
            // group columns have taken their places.
            int firstMeasurement = 2 + groupCount;

            var grid = new Grid();

            // Specimen, Attempt, the group columns, one per visible measurement, then
            // the row-delete button.
            int columnCount = firstMeasurement + visible.Count + 1;
            for (int i = 0; i < columnCount; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            int row = 0;

            // ---- Header row ----
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Add(grid, HeaderCell("Specimen"), row, 0);
            Add(grid, HeaderCell("Attempt"), row, 1);

            // A group column belongs to the specimen rather than to this table, so it
            // carries no hide button.
            for (int g = 0; g < groupCount; g++)
                Add(grid, HeaderCell(groupColumns[g]), row, 2 + g);

            for (int i = 0; i < visible.Count; i++)
            {
                int sourceIndex = visible[i];
                string header = table.MeasurementHeaders[sourceIndex];

                Button hide = null;
                if (table.AllowColumnHiding)
                {
                    hide = SmallButton("\u2715", "Hide this column (removes it from the export too)");
                    hide.Click += (s, e) =>
                    {
                        WorkshopColumnFilter.Hide(table.Key, header);
                        Rebuild();
                    };
                }

                // A column the user made carries the formula behind it, which this
                // button opens for editing or deletion.
                Button edit = null;
                WorkshopFormulaColumn formula;

                if (formulaColumns.TryGetValue(header, out formula))
                {
                    var captured = formula;
                    edit = SmallButton("\u270E", "Edit this formula column:  = " + formula.Text);
                    edit.Click += (s, e) => EditFormulaColumn(captured);
                }

                Add(grid, HeaderCell(header, hide, edit), row, firstMeasurement + i);
            }

            Add(grid, HeaderCell(""), row, columnCount - 1);
            row++;

            // ---- Specimen blocks ----
            foreach (var block in table.Blocks)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var banner = new StackPanel { Orientation = Orientation.Horizontal };
                banner.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(block.Name) ? "(unnamed specimen)" : block.Name,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (block.IsActive)
                {
                    banner.Children.Add(new TextBlock
                    {
                        Text = "  (loaded)",
                        Opacity = 0.6,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                var wipe = new Button
                {
                    Content = "Delete specimen",
                    Margin = new Thickness(12, 0, 0, 0),
                    Padding = new Thickness(8, 1, 8, 1),
                    ToolTip = "Remove every measurement recorded for this specimen"
                };

                var capturedBlock = block;
                wipe.Click += (s, e) => DeleteSpecimen(capturedBlock);
                banner.Children.Add(wipe);

                Add(grid, Cell(banner, SpecimenFill), row, 0, columnCount);
                row++;

                // The specimen's groups are the same on all of its rows, the way its
                // name is.
                var groupValues = SpecimenGroups.ValuesFor(block.Name);

                foreach (var tableRow in block.Rows)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    Add(grid, Cell(Text(block.Name), null), row, 0);
                    Add(grid, Cell(Text(tableRow.Attempt > 0 ? tableRow.Attempt.ToString() : ""), null), row, 1);

                    for (int g = 0; g < groupCount; g++)
                        Add(grid, Cell(Text(groupValues[g]), null), row, 2 + g);

                    for (int i = 0; i < visible.Count; i++)
                        Add(grid, Cell(Text(tableRow.Cells[visible[i]]), null), row, firstMeasurement + i);

                    // A placeholder row for a specimen with no measurements has nothing
                    // to delete.
                    UIElement action;
                    if (tableRow.Operations.Count == 0)
                    {
                        action = Text("");
                    }
                    else
                    {
                        var kill = SmallButton("\u2715", DeleteRowTip(tableRow));
                        var capturedRow = tableRow;
                        kill.Click += (s, e) => DeleteRow(capturedRow);
                        action = kill;
                    }

                    Add(grid, Cell(action, null), row, columnCount - 1);
                    row++;
                }
            }

            // Cells draw their right and bottom edges, so the outer border supplies the
            // remaining top and left lines to close the grid.
            return new Border
            {
                Child = grid,
                BorderBrush = GridLine,
                BorderThickness = new Thickness(1, 1, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        private static string DeleteRowTip(WorkshopRow row) =>
            row.Operations.Count == 1
                ? "Delete this measurement"
                : $"Delete this row ({row.Operations.Count} measurements share it)";

        // ---- Deletion ----

        private void DeleteRow(WorkshopRow row)
        {
            if (row.Operations.Count == 0) return;

            // One row can hold several operations, since attempt n of each kind shares
            // a row; say so plainly rather than deleting more than the user expects.
            string message = row.Operations.Count == 1
                ? "Delete this measurement?\n\nIt is removed from the session permanently and cannot be restored with Undo."
                : $"Delete all {row.Operations.Count} measurements on this row?\n\n" +
                  "This row holds one attempt from each operation kind shown. They are removed " +
                  "permanently and cannot be restored with Undo.";

            var confirm = MessageBox.Show(this, message, "Delete row",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            foreach (var op in row.Operations)
            {
                if (_undoRedo.RemoveOperation(op))
                    _removed.Add(op);
            }

            Rebuild();
        }

        private void DeleteSpecimen(WorkshopBlock block)
        {
            int count = block.AllOperations.Count;
            if (count == 0) return;

            var confirm = MessageBox.Show(
                this,
                $"Delete all {count} measurement(s) recorded for \"{block.Name}\"?\n\n" +
                "This removes every kind of measurement for that specimen, not just the ones shown here, " +
                "and cannot be restored with Undo. The specimen's image, name and groups are kept.",
                "Delete specimen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            // Snapshot first: the lists are emptied by the calls below.
            _removed.AddRange(block.AllOperations);

            if (block.IsActive)
                _undoRedo.RemoveActiveSpecimenOperations();
            else
                _undoRedo.RemoveArchivedSpecimen(block.Record);

            Rebuild();
        }

        // ---- Cell helpers ----

        // Every cell carries its own right and bottom border; the outer grid supplies
        // the top and left edges, so the lines meet without doubling up.
        private static Border Cell(UIElement content, Brush background) => new Border
        {
            Child = content,
            Background = background,
            BorderBrush = GridLine,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(6, 3, 6, 3)
        };

        private static Border HeaderCell(string text, params Button[] buttons)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (buttons != null)
            {
                foreach (var button in buttons)
                {
                    if (button != null) panel.Children.Add(button);
                }
            }

            return Cell(panel, HeaderFill);
        }

        private static TextBlock Text(string text) => new TextBlock
        {
            Text = text ?? "",
            VerticalAlignment = VerticalAlignment.Center
        };

        private static Button SmallButton(string glyph, string tip) => new Button
        {
            Content = glyph,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 9,
            ToolTip = tip,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static void Add(Grid grid, UIElement element, int row, int column, int span = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            if (span > 1) Grid.SetColumnSpan(element, span);
            grid.Children.Add(element);
        }
    }
}