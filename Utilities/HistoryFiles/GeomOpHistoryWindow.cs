using DinoLino.Utilities.Operations;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace DinoLino.Utilities
{
    // Per-session data window: one tab per mode, each showing that mode's whole wide
    // table — every variable of the mode as a column, every attempt as a row, with the
    // specimen group columns between Attempt and the measurements. Rows, columns and
    // specimens are deleted here, a variable of your own is added here, and each tab
    // exports on its own or is staged for the workbook. Specimen group columns appear
    // in every grid, CSV and workbook sheet.
    //
    // The mode is the unit throughout: a row joins every measurement made on one
    // attempt of that mode, which is the thing a narrower per-tool table cannot say.
    public class GeomOpHistoryWindow : Window
    {
        #region Fields and tab definitions

        // Sheet names staged for the workbook. Static so the selection outlives the
        // window and the Batch Workshop's All Geometric Data export can reuse it.
        private static readonly HashSet<string> _selectedSheets = new();

        private readonly TabControl _tabs = new TabControl();

        // The custom tab's grids, replaced on their own when a variable is ticked so
        // the list of variables keeps its place.
        private ContentControl _customData;

        // Whether the footer exports one long CSV instead of a workbook. Static so the
        // choice outlives one window, the way the staged sheets do: it is a way of working
        // rather than a property of any one table. The per-tab Export to CSV buttons are
        // unaffected — a single table read on its own is what the wide shape is good at.
        private static bool _longCsv;

        private TextBlock _workbookStatus;
        private Button _exportWorkbookButton;

        // Kept so the workbook is rebuilt from live history at export time rather
        // than from the rows captured when the tabs were drawn.
        private readonly UndoRedoManager _undoRedo;
        private readonly ScaleSource _scale;

        // Which specimen is loaded, read when it is needed rather than kept. This window
        // is not modal, so the user can navigate, rename, clear or open another project
        // while it is open; a name captured at construction would label one specimen's
        // rows with another's name, and the loaded block's Delete specimen would then be
        // offering to delete something other than what the banner says.
        private readonly Func<string> _nameSource;

        private string CurrentName => _nameSource != null ? _nameSource() : "";

        /// Called after every change made here, with the operations a deletion removed —
        /// empty when the change removed nothing, such as adding or hiding a column. The
        /// window is not modal, so the host is told as it happens rather than at close.
        private readonly Action<IReadOnlyList<WorkOperation>> _onChanged;

        /// The host's own clear for the loaded specimen, which asks the one question the
        /// sidebar button and Ctrl+Shift+C ask, honours the same "do not ask again", and
        /// does the same work — including the half-finished capture and the outline
        /// preview, which this window has no way to reach. Returns whether it cleared
        /// anything. Null falls back to the warning below.
        private readonly Func<bool> _clearLoadedSpecimen;

        // Set while a handler is making several changes at once, so the window redraws
        // once at the end instead of after each one.
        private bool _suspendRebuild;

        private static readonly WorkOperation[] NothingRemoved = new WorkOperation[0];

        // Where each tab was scrolled to. Every edit redraws the whole tab, and without
        // this a row deleted near the bottom of a long table sends the view back to the
        // top, so deleting several in a row means scrolling back each time.
        private readonly Dictionary<string, Point> _scrollOffsets =
            new Dictionary<string, Point>(StringComparer.Ordinal);

        // How to build each tab's contents, held rather than run. A tab is built when it
        // is first looked at and not before: the window follows the session now, so it
        // redraws on every measurement, and building all six tables each time — the EFA
        // one can be a hundred columns wide — would make measuring stutter.
        private readonly Dictionary<TabItem, Func<UIElement>> _tabContent =
            new Dictionary<TabItem, Func<UIElement>>();

        // Grid appearance for the editable tables.
        private static readonly Brush GridLine = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        private static readonly Brush HeaderFill = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
        private static readonly Brush SpecimenFill = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));

        // One flattened table staged for export: sheet name, column headers, and
        // rows already formatted as display strings.
        private class WorkbookSheet
        {
            public string Name;
            public string[] Headers;
            public List<string[]> Rows;
        }

        // Backs the editable "Attempt" column header.
        private class AttemptHeader : INotifyPropertyChanged
        {
            private string _text = "Attempt";
            public string Text
            {
                get => _text;
                set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        // One grid row: the attempt label plus the cells, where the cell array is the
        // specimen's group values followed by the measurement values.
        private class HistoryRow
        {
            public string Attempt { get; set; }
            public string[] Cells { get; set; }
        }

        // One tab: the Batch Workshop category whose whole wide table it shows.
        private sealed class TabSpec
        {
            public string Name;
            public string FileName;
            public WorkshopCategory Category;
        }

        // Tab order also fixes sheet order in the exported workbooks. One entry per
        // mode, so a tab, its CSV and its workbook sheet are the same table under the
        // same name, and the key its hidden and formula columns are filed under.
        private static readonly TabSpec[] Tabs =
        {
            new TabSpec
            {
                Name = "Curvature",
                FileName = "curvature_data.csv",
                Category = WorkshopCategory.Curvature
            },
            new TabSpec
            {
                Name = "Angle",
                FileName = "angle_data.csv",
                Category = WorkshopCategory.Angle
            },
            new TabSpec
            {
                // Draw, not Shape: the mode's name, like every other tab, and the mode
                // measures lines as well as shapes. The table key stays Shape, since a
                // saved project files its hidden and formula columns under that.
                Name = "Draw",
                FileName = "draw_data.csv",
                Category = WorkshopCategory.Shape
            },
            new TabSpec
            {
                Name = "Outline",
                FileName = "outline_metadata.csv",
                Category = WorkshopCategory.OutlineMetadata
            },
            new TabSpec
            {
                Name = "EFA",
                FileName = "efa_data.csv",
                Category = WorkshopCategory.Efa
            }
        };

        // What the per-tool tabs of earlier versions were called, so a project saved
        // then still finds its staged sheets. Several tools collapsed into one mode,
        // which is why this maps many names onto one.
        private static readonly Dictionary<string, string> LegacySheetNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Circular Arc", "Curvature" },
                { "Parabolic Arc", "Curvature" },
                { "n-Point Spline", "Curvature" },
                { "Shapes", "Draw" },
                { "Lines", "Draw" },
                { "Shape", "Draw" }
            };

        public GeomOpHistoryWindow(
            UndoRedoManager undoRedo, Func<string> nameSource, ScaleSource scale,
            Func<bool> clearLoadedSpecimen = null,
            Action<IReadOnlyList<WorkOperation>> onChanged = null)
        {
            _undoRedo = undoRedo;
            _nameSource = nameSource;
            _scale = scale;
            _clearLoadedSpecimen = clearLoadedSpecimen;
            _onChanged = onChanged;

            Title = "Operation history";

            // Wide enough for a mode's whole table rather than one tool's few columns.
            Width = 1000;
            Height = 620;
            MinWidth = 560;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var footer = BuildWorkbookFooter();
            UpdateFooter();

            BuildTabs();

            var root = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(_tabs);
            Content = root;

            // The wide tables scroll sideways under a two-finger gesture, a tilt
            // wheel, or Shift+wheel.
            MainWindow.AttachHorizontalWheel(this);

            // Measuring, undoing, clearing a specimen, moving to the next one and opening
            // a project all raise this, so the tables follow the session instead of
            // showing what it looked like when the window opened. Without it the window
            // would keep offering to delete rows that no longer exist.
            if (_undoRedo != null) _undoRedo.PropertyChanged += Session_Changed;
        }

        private void Session_Changed(object sender, PropertyChangedEventArgs e)
        {
            // Every mutator raises CanUndo and CanRedo together, so following one of them
            // redraws once per change rather than twice.
            if (e.PropertyName == nameof(UndoRedoManager.CanUndo)) Rebuild();
        }

        /// Redraws from live state at the host's request, for the changes that do not pass
        /// through the undo history at all: a scale measured, a group assigned, a specimen
        /// renamed, an outline's metrics generated, a project opened or reset.
        internal void RefreshTables() => Rebuild();

        protected override void OnClosed(EventArgs e)
        {
            if (_undoRedo != null) _undoRedo.PropertyChanged -= Session_Changed;
            base.OnClosed(e);
        }

        /// Redraws the tables and the workbook footer from live state. Everything that
        /// changes anything comes through here, so there is one place that decides what
        /// the window is showing.
        private void Rebuild()
        {
            if (_suspendRebuild) return;

            BuildTabs();
            UpdateFooter();
        }

        // Draws the tab strip, keeping the user on the tab they were reading. Only that
        // tab's table is built; the rest are built if and when they are opened.
        private void BuildTabs()
        {
            int selected = _tabs.SelectedIndex;

            _tabs.SelectionChanged -= Tabs_SelectionChanged;
            _tabs.Items.Clear();
            _tabContent.Clear();
            _customData = null;

            foreach (var spec in Tabs)
            {
                var captured = spec;
                var item = new TabItem { Header = spec.Name };
                _tabContent[item] = () => BuildTab(captured);
                _tabs.Items.Add(item);
            }

            var custom = new TabItem { Header = CustomTable.Key };
            _tabContent[custom] = BuildCustomTab;
            _tabs.Items.Add(custom);

            if (selected >= 0 && selected < _tabs.Items.Count) _tabs.SelectedIndex = selected;

            _tabs.SelectionChanged += Tabs_SelectionChanged;
            FillSelectedTab();
        }

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Only the strip's own change, not one bubbling up from a control on a tab.
            if (!ReferenceEquals(e.OriginalSource, _tabs)) return;

            FillSelectedTab();
        }

        // Builds the selected tab's contents if they have not been built since the last
        // redraw. Content is never set to null, so a non-null Content means built.
        private void FillSelectedTab()
        {
            var item = _tabs.SelectedItem as TabItem;
            if (item == null || item.Content != null) return;

            Func<UIElement> build;
            if (_tabContent.TryGetValue(item, out build)) item.Content = build();
        }

        // Every specimen, oldest first: archived records then the live one.
        private static IEnumerable<(string Name, IReadOnlyList<WorkOperation> Ops)> Blocks(
            UndoRedoManager ur, string currentName)
        {
            foreach (var rec in ur.Archive)
                yield return (rec.SpecimenName, rec.Operations);
            yield return (currentName, ur.History);
        }

        #endregion

        #region Tab building

        // A tab's table is its mode's whole table, built under that mode's own filter
        // key. So a column hidden on this tab is hidden in its CSV and its workbook
        // sheet as well, and the three can no longer disagree about what was exported.
        private static WorkshopTable BuildTable(
            TabSpec spec, UndoRedoManager ur, string currentName, ScaleSource scale) =>
            WorkshopTables.Build(spec.Category, ur, currentName, scale);

        // The same table with nothing hidden, for the one export that promises the whole
        // record. Hiding a column is a choice about a table being arranged for a purpose;
        // File then Export Operation History is not that, so it writes everything and
        // cannot quietly hand over a file short of a column.
        private static WorkshopTable BuildUnfilteredTable(
            TabSpec spec, UndoRedoManager ur, string currentName, ScaleSource scale) =>
            WorkshopTables.BuildFromGroups(
                null,
                WorkshopTables.ColumnGroups(spec.Category, ur, scale),
                ur, currentName, scale,
                WorkshopTables.KeyFor(spec.Category));

        // One tab: the mode's table with its row, column and specimen controls, and
        // under it any formula column the table cannot currently calculate. The tab's
        // CSV comes from the same table, so the file matches what is on screen.
        private UIElement BuildTab(TabSpec spec)
        {
            var table = BuildTable(spec, _undoRedo, CurrentName, _scale);

            var body = new StackPanel();

            // On the tab rather than above the strip: none of it is true of the Custom
            // tab, which joins several modes and so has no row of its own to delete.
            body.Children.Add(new TextBlock
            {
                Text = "Deleting a row or specimen is permanent and cannot be undone with " +
                       "Ctrl+Z. Hiding a column removes it from this table and from its " +
                       "exports. Group columns are set from the Sample tab and cannot be " +
                       "hidden here. Add column makes a variable of your own from a formula; " +
                       "it is recalculated every time the table is built, and the \u270E on " +
                       "its header edits it.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(10, 8, 10, 6)
            });

            if (table.IsEmpty)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "No measurements of this kind have been recorded yet.",
                    Opacity = 0.6,
                    Margin = new Thickness(12)
                });
            }
            else
            {
                body.Children.Add(BuildEditableGrid(spec, table));
            }

            var hidden = WorkshopColumnFilter.HiddenColumns(table.Key).ToList();
            if (hidden.Count > 0) body.Children.Add(HiddenPanel(table.Key, hidden));

            var missing = MissingFormulaColumns(spec, table);
            if (missing.Count > 0) body.Children.Add(MissingPanel(spec, missing));

            return WrapTab(spec, table, body);
        }

        // What has been hidden, named under the table it was hidden from. Without this a
        // hidden column is invisible in both senses: gone from the table and from its
        // export, with nothing on screen saying the export is short of a column. A hidden
        // formula column would also take its edit button out of reach.
        private UIElement HiddenPanel(string key, List<string> hidden)
        {
            var panel = new StackPanel { Margin = new Thickness(10, 4, 10, 0) };

            panel.Children.Add(new TextBlock
            {
                Text = hidden.Count == 1
                    ? "1 column is hidden, so it is missing from this table's exports. Click to bring it back:"
                    : hidden.Count + " columns are hidden, so they are missing from this table's exports. Click to bring one back:",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0)
            };

            foreach (var column in hidden)
            {
                var captured = column;
                var button = new Button
                {
                    Content = column,
                    Padding = new Thickness(8, 1, 8, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = "Show " + column + " again"
                };
                button.Click += (s, e) =>
                {
                    WorkshopColumnFilter.Show(key, captured);
                    Changed(NothingRemoved);
                };
                row.Children.Add(button);
            }

            panel.Children.Add(row);
            return panel;
        }

        // One grid per specimen, with the group columns before the measurements.
        private static StackPanel BuildTableGrids(WorkshopTable table)
        {
            var groupColumns = SpecimenGroups.Columns;

            var panel = new StackPanel();
            var attemptHeader = new AttemptHeader();

            foreach (var block in table.Blocks)
            {
                panel.Children.Add(SpecimenHeader(block.Name));

                var grid = MakeGrid();
                AddAttemptColumn(grid, MakeAttemptHeaderBox(attemptHeader), nameof(HistoryRow.Attempt), 70);

                for (int g = 0; g < groupColumns.Count; g++)
                    AddColumn(grid, groupColumns[g], $"{nameof(HistoryRow.Cells)}[{g}]");

                for (int i = 0; i < table.MeasurementHeaders.Length; i++)
                    AddColumn(grid, table.MeasurementHeaders[i],
                        $"{nameof(HistoryRow.Cells)}[{groupColumns.Count + i}]");

                var groupValues = SpecimenGroups.ValuesFor(block.Name);

                // Attempt 0 marks the placeholder row a specimen with no operations of
                // this kind gets; it belongs in the export but not on screen.
                grid.ItemsSource = block.Rows
                    .Where(r => r.Attempt > 0)
                    .Select(r => new HistoryRow
                    {
                        Attempt = r.Attempt.ToString(),
                        Cells = groupValues.Concat(r.Cells).ToArray()
                    })
                    .ToList();

                panel.Children.Add(grid);
            }

            return panel;
        }

        #endregion

        #region Editable table

        // ---- Formula columns ----

        // A mode's formula columns, ready to look up by header.
        private static Dictionary<string, WorkshopFormulaColumn> FormulaColumns(TabSpec spec)
        {
            var columns = new Dictionary<string, WorkshopFormulaColumn>(StringComparer.OrdinalIgnoreCase);

            foreach (var column in WorkshopFormulas.ColumnsFor(WorkshopTables.KeyFor(spec.Category)))
                columns[column.Name] = column;

            return columns;
        }

        // Formula columns this table cannot calculate, because a column their formula
        // names is not one of its own. They are listed under the table so they can
        // still be corrected or removed.
        private static List<WorkshopFormulaColumn> MissingFormulaColumns(
            TabSpec spec, WorkshopTable table)
        {
            var shown = new HashSet<string>(table.MeasurementHeaders, StringComparer.OrdinalIgnoreCase);

            return WorkshopFormulas
                .ColumnsFor(WorkshopTables.KeyFor(spec.Category))
                .Where(c => !shown.Contains(c.Name))
                .ToList();
        }

        private UIElement MissingPanel(TabSpec spec, List<WorkshopFormulaColumn> columns)
        {
            var panel = new StackPanel { Margin = new Thickness(10, 12, 10, 0) };

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

                var fix = GlyphButton("\u270E", "Edit or delete this formula column");
                var captured = column;
                fix.Click += (s, e) => EditFormulaColumn(spec, captured);
                line.Children.Add(fix);

                panel.Children.Add(line);
            }

            return panel;
        }

        /// Opens the formula dialog for a new column, or for one the table already has.
        private void EditFormulaColumn(TabSpec spec, WorkshopFormulaColumn existing)
        {
            string key = WorkshopTables.KeyFor(spec.Category);

            // The dialog offers this table's columns and previews the rows the formula
            // would produce, so it is given the table as it currently stands.
            var table = BuildTable(spec, _undoRedo, CurrentName, _scale);

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
                Changed(NothingRemoved);
                return;
            }

            WorkshopFormulas.Save(
                existing,
                key,
                WorkshopFormulaWindow.KeptName(this, existing, window.ColumnName),
                window.Result);

            Changed(NothingRemoved);
        }

        // Whether an operation still belongs to an archived specimen.
        private bool InArchive(WorkOperation op)
        {
            foreach (var record in _undoRedo.Archive)
            {
                if (record.Operations.Contains(op)) return true;
            }

            return false;
        }

        // ---- Grid rendering ----

        // The mode's whole table: Specimen, Attempt, the group columns, then one column
        // per visible measurement, with a hide button on each measurement header, a
        // delete button on each row, and a banner per specimen carrying its own.
        private UIElement BuildEditableGrid(TabSpec spec, WorkshopTable table)
        {
            var visible = table.VisibleColumnIndexes();
            var formulaColumns = FormulaColumns(spec);
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

            Place(grid, GridHeaderCell("Specimen"), row, 0);
            Place(grid, GridHeaderCell("Attempt"), row, 1);

            // A group column belongs to the specimen rather than to this table, so it
            // carries no hide button.
            for (int g = 0; g < groupCount; g++)
                Place(grid, GridHeaderCell(groupColumns[g]), row, 2 + g);

            for (int i = 0; i < visible.Count; i++)
            {
                int sourceIndex = visible[i];
                string header = table.MeasurementHeaders[sourceIndex];

                Button hide = null;
                if (table.AllowColumnHiding)
                {
                    hide = GlyphButton("\u2715", "Hide this column (removes it from the export too)");
                    hide.Click += (s, e) =>
                    {
                        WorkshopColumnFilter.Hide(table.Key, header);
                        Changed(NothingRemoved);
                    };
                }

                // A column the user made carries the formula behind it, which this
                // button opens for editing or deletion.
                Button edit = null;
                WorkshopFormulaColumn formula;

                if (formulaColumns.TryGetValue(header, out formula))
                {
                    var captured = formula;
                    edit = GlyphButton("\u270E", "Edit this formula column:  = " + formula.Text);
                    edit.Click += (s, e) => EditFormulaColumn(spec, captured);
                }

                Place(grid, GridHeaderCell(header, hide, edit), row, firstMeasurement + i);
            }

            Place(grid, GridHeaderCell(""), row, columnCount - 1);
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
                    ToolTip = "Remove every measurement recorded for this specimen. "
                            + "This cannot be undone."
                };

                var capturedBlock = block;
                wipe.Click += (s, e) => DeleteSpecimen(capturedBlock);
                banner.Children.Add(wipe);

                Place(grid, GridCell(banner, SpecimenFill), row, 0, columnCount);
                row++;

                // The specimen's groups are the same on all of its rows, the way its
                // name is.
                var groupValues = SpecimenGroups.ValuesFor(block.Name);

                foreach (var tableRow in block.Rows)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    Place(grid, GridCell(GridText(block.Name), null), row, 0);
                    Place(grid, GridCell(GridText(tableRow.Attempt > 0 ? tableRow.Attempt.ToString() : ""), null), row, 1);

                    for (int g = 0; g < groupCount; g++)
                        Place(grid, GridCell(GridText(groupValues[g]), null), row, 2 + g);

                    for (int i = 0; i < visible.Count; i++)
                        Place(grid, GridCell(GridText(tableRow.Cells[visible[i]]), null), row, firstMeasurement + i);

                    // A placeholder row for a specimen with no measurements has nothing
                    // to delete.
                    UIElement action;
                    if (tableRow.Operations.Count == 0)
                    {
                        action = GridText("");
                    }
                    else
                    {
                        var kill = GlyphButton("\u2715", DeleteRowTip(tableRow));
                        var capturedRow = tableRow;
                        kill.Click += (s, e) => DeleteRow(capturedRow);
                        action = kill;
                    }

                    Place(grid, GridCell(action, null), row, columnCount - 1);
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
                Margin = new Thickness(10, 4, 10, 8)
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

            // The row was drawn from history as it stood; an undo or a clear since then
            // may have taken these operations out of it. Asking a permanent-deletion
            // question and then deleting nothing is worse than redrawing quietly.
            if (!row.Operations.Any(op => _undoRedo.History.Contains(op)
                                          || _undoRedo.RedoStack.Contains(op)
                                          || InArchive(op)))
            {
                Rebuild();
                return;
            }

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

            var justRemoved = new List<WorkOperation>();

            // One redraw for the lot: each removal announces itself, and following every
            // one of them would rebuild the table once per measurement deleted.
            _suspendRebuild = true;
            try
            {
                foreach (var op in row.Operations)
                {
                    if (_undoRedo.RemoveOperation(op)) justRemoved.Add(op);
                }
            }
            finally
            {
                _suspendRebuild = false;
            }

            Changed(justRemoved);
        }

        private void DeleteSpecimen(WorkshopBlock block)
        {
            // The loaded specimen belongs to the host: it asks the one question asked
            // everywhere else, and it clears what this window cannot reach — a
            // half-finished capture, the outline preview, the mode on screen. It also
            // reads which specimen is loaded now, so a window left open while the user
            // moved on cannot clear one specimen while naming another.
            //
            // No count is tested first. The block lists the live history, and a specimen
            // whose measurements have all been undone has an empty one while still holding
            // everything the clear would discard; the host counts the redo stack too.
            if (block.IsActive && _clearLoadedSpecimen != null)
            {
                // The host redraws itself and this window follows the history, so there is
                // nothing to report back.
                _clearLoadedSpecimen();
                return;
            }

            int count = block.AllOperations.Count;
            if (count == 0) return;

            // The block was drawn from the archive as it stood. A project opened since
            // then replaced that archive, and removing a record it no longer holds would
            // do nothing while still blanking the mode panels, as though it had. Checked
            // before the question, so nobody is asked about a specimen already gone.
            if (!block.IsActive && !_undoRedo.Archive.Contains(block.Record))
            {
                Rebuild();
                return;
            }

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
            var deleted = block.AllOperations.ToList();

            _suspendRebuild = true;
            try
            {
                if (block.IsActive)
                    _undoRedo.RemoveActiveSpecimenOperations();
                else
                    _undoRedo.RemoveArchivedSpecimen(block.Record);
            }
            finally
            {
                _suspendRebuild = false;
            }

            Changed(deleted);
        }

        /// Redraws this window and tells the host what changed. Adding, hiding or showing
        /// a column removes nothing, and still has to reach the host: the plot and the
        /// variable pickers read these tables.
        private void Changed(IReadOnlyList<WorkOperation> removed)
        {
            Rebuild();
            _onChanged?.Invoke(removed ?? NothingRemoved);
        }

        // ---- Cell helpers ----

        // Every cell carries its own right and bottom border; the outer grid supplies
        // the top and left edges, so the lines meet without doubling up.
        private static Border GridCell(UIElement content, Brush background) => new Border
        {
            Child = content,
            Background = background,
            BorderBrush = GridLine,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(6, 3, 6, 3)
        };

        private static Border GridHeaderCell(string text, params Button[] buttons)
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

            return GridCell(panel, HeaderFill);
        }

        private static TextBlock GridText(string text) => new TextBlock
        {
            Text = text ?? "",
            VerticalAlignment = VerticalAlignment.Center
        };

        private static Button GlyphButton(string glyph, string tip) => new Button
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

        private static void Place(Grid grid, UIElement element, int row, int column, int span = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            if (span > 1) Grid.SetColumnSpan(element, span);
            grid.Children.Add(element);
        }

        #endregion

        #region Tab chrome and grid helpers

        // Wraps a tab's specimen panel in a scroll viewer + button row (Add column /
        // Add to workbook / Export to CSV).
        private UIElement WrapTab(TabSpec spec, WorkshopTable table, StackPanel panel)
        {
            var scroll = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            RememberScroll(scroll, spec.Name);

            var formulaButton = new Button
            {
                Content = "Add column",
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 4, 12, 4),
                ToolTip = "Make a new variable from a formula over this table's columns"
            };
            formulaButton.Click += (s, e) => EditFormulaColumn(spec, null);

            var restoreButton = new Button
            {
                Content = "Restore hidden columns",
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 4, 12, 4),
                ToolTip = "Bring back every column hidden from this table",
                IsEnabled = WorkshopColumnFilter.HiddenCount(table.Key) > 0
            };
            restoreButton.Click += (s, e) =>
            {
                WorkshopColumnFilter.Restore(table.Key);
                Changed(NothingRemoved);
            };

            // Built at the moment of the click, not when the tab was drawn: this window
            // stays open while measuring, so a file written from the drawn rows would be
            // short of everything measured since.
            return WrapContent(
                spec.Name, spec.FileName, scroll,
                () => BuildTable(spec, _undoRedo, CurrentName, _scale).ToCsv(),
                formulaButton, restoreButton);
        }

        // Keeps one tab's scroll position across the redraws its own edits cause. The
        // offset is restored once the new content has been measured, since scrolling to a
        // position an empty viewer does not have yet would be ignored.
        private void RememberScroll(ScrollViewer scroll, string key)
        {
            scroll.ScrollChanged += (s, e) =>
            {
                if (e.ExtentHeightChange == 0 && e.ExtentWidthChange == 0)
                    _scrollOffsets[key] = new Point(scroll.HorizontalOffset, scroll.VerticalOffset);
            };

            // A tab control detaches and reattaches its content as tabs are switched, so
            // this fires again every time the tab is returned to. The offset is read now
            // rather than captured, or returning to a tab would undo the scrolling done
            // since it was built.
            bool restored = false;

            scroll.Loaded += (s, e) =>
            {
                if (restored) return;
                restored = true;

                Point saved;
                if (!_scrollOffsets.TryGetValue(key, out saved)) return;
                if (saved.X == 0 && saved.Y == 0) return;

                scroll.ScrollToHorizontalOffset(saved.X);
                scroll.ScrollToVerticalOffset(saved.Y);
            };
        }

        // The chrome every tab shares: its content over a row of buttons. The rows to
        // export are asked for at the moment of the click, so a tab that rebuilds its
        // own content still writes what is on screen.
        private UIElement WrapContent(
            string header, string suggestedFileName, UIElement content,
            Func<(string[] Headers, List<string[]> Rows)> csv, params Button[] extraButtons)
        {
            var addButton = new Button
            {
                Content = WorkbookButtonLabel(IsInWorkbook(header)),
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 4, 12, 4),
                ToolTip = "Include this table in the exported workbook"
            };
            addButton.Click += (s, e) =>
            {
                // Toggle: stage this tab's sheet, or drop it if already staged.
                if (!_selectedSheets.Remove(header))
                    _selectedSheets.Add(header);

                ProjectSession.MarkChanged();
                addButton.Content = WorkbookButtonLabel(IsInWorkbook(header));
                UpdateFooter();
            };

            var csvButton = new Button
            {
                Content = "Export to CSV…",
                Padding = new Thickness(12, 4, 12, 4)
            };
            csvButton.Click += (s, e) =>
            {
                var rows = csv();
                ExportCsv(rows.Headers, rows.Rows, suggestedFileName);
            };

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };

            if (extraButtons != null)
            {
                foreach (var button in extraButtons)
                {
                    if (button != null) buttonRow.Children.Add(button);
                }
            }

            buttonRow.Children.Add(addButton);
            buttonRow.Children.Add(csvButton);

            var dock = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(buttonRow, Dock.Bottom);
            dock.Children.Add(buttonRow);
            dock.Children.Add(content);
            return dock;
        }

        // ---- Custom tab ----

        /// The Custom tab: variables ticked from any mode, side by side in one table.
        private UIElement BuildCustomTab()
        {
            _customData = new ContentControl { Content = BuildCustomGrids() };

            var body = new DockPanel();

            var picker = BuildVariablePicker();
            DockPanel.SetDock(picker, Dock.Left);
            body.Children.Add(picker);

            var scroll = new ScrollViewer
            {
                Content = _customData,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            RememberScroll(scroll, CustomTable.Key);
            body.Children.Add(scroll);

            var addColumn = new Button
            {
                Content = "Add column",
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 4, 12, 4),
                ToolTip = "Make a new variable from a formula over the columns of this table"
            };
            addColumn.Click += (s, e) => EditCustomFormulaColumn(null);

            return WrapContent(CustomTable.Key, CustomTable.FileName, body, CustomCsv, addColumn);
        }

        private (string[] Headers, List<string[]> Rows) CustomCsv() =>
            CustomTable.Build(_undoRedo, CurrentName, _scale).ToCsv();

        private UIElement BuildCustomGrids()
        {
            var table = CustomTable.Build(_undoRedo, CurrentName, _scale);

            if (table.MeasurementHeaders.Length == 0 || table.Blocks.Count == 0)
            {
                return new TextBlock
                {
                    Text = "Tick variables on the left to build a table.",
                    Opacity = 0.6,
                    Margin = new Thickness(12)
                };
            }

            return BuildTableGrids(table);
        }

        // Ticking a variable redraws the grids alone, so the list of variables keeps
        // its scroll position.
        private void RefreshCustomData()
        {
            if (_customData != null) _customData.Content = BuildCustomGrids();
        }

        // Every variable of every mode, ticked into or out of the custom table, with
        // that table's own formula columns underneath.
        // The scroll position of the variable list, kept apart from the table's own.
        private const string VariableScrollKey = "Custom:variables";

        private FrameworkElement BuildVariablePicker()
        {
            var panel = new StackPanel { Margin = new Thickness(4, 4, 8, 4) };

            panel.Children.Add(new TextBlock
            {
                Text = "Variables",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(4, 4, 4, 2)
            });

            var clear = new Button
            {
                Content = "Clear all",
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 0, 4, 6)
            };
            clear.Click += (s, e) =>
            {
                CustomTableSelection.Clear();
                BuildTabs();
            };
            panel.Children.Add(clear);

            var catalog = CustomTable.Catalog(_undoRedo, CurrentName, _scale);

            if (catalog.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No measurements have been recorded yet.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.6,
                    Margin = new Thickness(4)
                });
            }

            foreach (var group in catalog)
            {
                var captured = group;

                // Ticked only while every variable below it is ticked, so it reads as a
                // summary of them rather than a setting of its own.
                var whole = new CheckBox
                {
                    Content = group.Title,
                    FontWeight = FontWeights.Bold,
                    IsChecked = group.IsAllSelected,
                    Margin = new Thickness(4, 8, 4, 2),
                    ToolTip = "Put every variable below into the table, or take them all out"
                };

                whole.Click += (s, e) =>
                {
                    CustomTableSelection.SetGroupSelected(
                        captured.Category, captured.Headers, whole.IsChecked == true);

                    // Every tick below this one has just changed as well as the table, so
                    // the whole tab is redrawn rather than only its grids, the same as
                    // Clear all does.
                    BuildTabs();
                };

                panel.Children.Add(whole);

                foreach (var header in group.Headers)
                {
                    var category = group.Category;
                    string name = header;

                    var tick = new CheckBox
                    {
                        Content = NameLabel(name),
                        IsChecked = CustomTableSelection.IsSelected(name),
                        Margin = new Thickness(8, 1, 4, 1)
                    };
                    tick.Click += (s, e) =>
                    {
                        CustomTableSelection.Toggle(category, name);
                        RefreshCustomData();
                    };

                    panel.Children.Add(tick);
                }
            }

            var formulas = WorkshopFormulas.ColumnsFor(CustomTable.Key);

            if (formulas.Count > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Formula columns",
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(4, 10, 4, 2)
                });

                foreach (var formula in formulas)
                {
                    var column = formula;

                    var line = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(8, 1, 4, 1)
                    };

                    line.Children.Add(new TextBlock
                    {
                        Text = column.Name,
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    var edit = new Button
                    {
                        Content = "\u270E",
                        Width = 18,
                        Height = 18,
                        Padding = new Thickness(0),
                        Margin = new Thickness(6, 0, 0, 0),
                        FontSize = 9,
                        ToolTip = "Edit or delete this formula column:  = " + column.Text
                    };
                    edit.Click += (s, e) => EditCustomFormulaColumn(column);

                    line.Children.Add(edit);
                    panel.Children.Add(line);
                }
            }

            var variables = new ScrollViewer
            {
                Content = panel,
                Width = 210,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            // Ticking a group header redraws the whole tab, and this list is where the
            // ticking happens, so without this the list jumps back to the top on every
            // click of it.
            RememberScroll(variables, VariableScrollKey);

            return new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child = variables
            };
        }

        // The custom table belongs to this window, so its formula columns are written
        // and edited here rather than in a Batch Workshop edit window.
        private void EditCustomFormulaColumn(WorkshopFormulaColumn existing)
        {
            var table = CustomTable.Build(_undoRedo, CurrentName, _scale);

            // A variable that is unticked today can be ticked tomorrow, so no column
            // may take the name of one.
            var reserved = CustomTable
                .Catalog(_undoRedo, CurrentName, _scale)
                .SelectMany(g => g.Headers);

            var window = new WorkshopFormulaWindow(table, existing, reserved)
            {
                Owner = this,
                FontSize = FontSize,
                FontFamily = FontFamily
            };

            if (window.ShowDialog() != true) return;

            if (window.DeleteRequested)
            {
                WorkshopFormulas.Remove(existing);
                BuildTabs();
                return;
            }

            WorkshopFormulas.Save(
                existing,
                CustomTable.Key,
                WorkshopFormulaWindow.KeptName(this, existing, window.ColumnName),
                window.Result);

            Changed(NothingRemoved);
        }

        /// <summary>Sheet names staged for the workbook, for a project file to record.</summary>
        public static IEnumerable<string> StagedSheetNames() => _selectedSheets.ToList();

        /// <summary>Restores the staged sheets a project file recorded.</summary>
        public static void StageSheets(IEnumerable<string> names)
        {
            _selectedSheets.Clear();
            if (names == null) return;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;

                // A project saved when the tabs were per tool names sheets that no tab
                // answers to now. Translating them keeps that project's staging rather
                // than silently exporting nothing.
                string current;
                _selectedSheets.Add(
                    LegacySheetNames.TryGetValue(name, out current) ? current : name);
            }
        }

        private static bool IsInWorkbook(string sheetName) => _selectedSheets.Contains(sheetName);

        private static string WorkbookButtonLabel(bool added) =>
            added ? "\u2713 Added to workbook" : "Add to workbook";

        // A column name shown as a control's content rather than as plain text. A
        // CheckBox and a DataGrid column header both pass their content through
        // access-key handling, which swallows a single underscore: circ_centangle draws
        // as circcentangle, with the c underlined. A TextBlock is content in its own
        // right rather than text to be read for a shortcut, so the name shows as written.
        private static TextBlock NameLabel(string text) => new TextBlock { Text = text };

        private static TextBlock SpecimenHeader(string name) => new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(8, 12, 8, 4)
        };

        // Shared grid style: read-only cells (the Attempt column overrides this),
        // no add/delete/sort/reorder, own scrolling off (the tab's ScrollViewer handles it).
        private static DataGrid MakeGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                CanUserSortColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Auto),
                Margin = new Thickness(8, 0, 8, 12)
            };
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
            return grid;
        }

        // Read-only column bound to one cell of the row array.
        private static void AddColumn(DataGrid grid, string header, string path, double? fixedWidth = null)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = NameLabel(header),
                Binding = new Binding(path),
                IsReadOnly = true,
                Width = fixedWidth.HasValue
                    ? new DataGridLength(fixedWidth.Value)
                    : new DataGridLength(1, DataGridLengthUnitType.Auto)
            });
        }

        // Editable header for the Attempt column: a borderless TextBox two-way bound
        // to the tab's shared AttemptHeader, so typing relabels the column live.
        private static TextBox MakeAttemptHeaderBox(AttemptHeader model)
        {
            var box = new TextBox
            {
                MinWidth = 54,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontWeight = FontWeights.Bold,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Edit this column title (affects this window only)",
                DataContext = model
            };
            box.SetBinding(TextBox.TextProperty, new Binding(nameof(AttemptHeader.Text))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            return box;
        }

        // The Attempt column is user-editable (unlike AddColumn's read-only ones) so
        // attempt numbers can be relabeled; commits on LostFocus to avoid re-rendering
        // mid-edit.
        private static void AddAttemptColumn(DataGrid grid, TextBox headerBox, string path, double fixedWidth)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = headerBox,
                Binding = new Binding(path)
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                },
                IsReadOnly = false,
                Width = new DataGridLength(fixedWidth)
            });
        }

        #endregion

        #region Workbook footer and CSV export

        private FrameworkElement BuildWorkbookFooter()
        {
            _workbookStatus = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };

            _exportWorkbookButton = new Button
            {
                Padding = new Thickness(12, 4, 12, 4)
            };
            _exportWorkbookButton.Click += (s, e) =>
            {
                if (_longCsv) ExportLongCsv();
                else ExportWorkbook();
            };

            var longFormat = new CheckBox
            {
                Content = "Long format CSV",
                IsChecked = _longCsv,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
                ToolTip = "Export one CSV file covering every mode instead of a workbook of "
                        + "sheets: a row for each specimen, replicate, operation and variable, "
                        + "with the unit in a column of its own. The shape ggplot2 and "
                        + "pandas read as they are given it, and the one to pivot from for "
                        + "a package wanting a row per specimen."
            };

            longFormat.Click += (s, e) =>
            {
                _longCsv = longFormat.IsChecked == true;
                UpdateFooter();
            };

            var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
            DockPanel.SetDock(_exportWorkbookButton, Dock.Right);
            bar.Children.Add(_exportWorkbookButton);
            DockPanel.SetDock(longFormat, Dock.Right);
            bar.Children.Add(longFormat);
            bar.Children.Add(_workbookStatus);

            return new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = bar
            };
        }

        /// What the footer's one button writes, and what it says it will write. The long
        /// export covers every mode whatever is staged, so the staged count has nothing to
        /// say about it; the button is live either way, since there is always a file to
        /// write even when the session holds no measurements yet.
        private void UpdateFooter()
        {
            if (_workbookStatus == null || _exportWorkbookButton == null) return;

            if (_longCsv)
            {
                _workbookStatus.Text = "Every mode in one file, one value to a row";
                _exportWorkbookButton.Content = "Export long CSV…";
                _exportWorkbookButton.ToolTip =
                    "Write geometric_data_long.csv: every mode's measurements in one table, "
                    + "a specimen's rows together";
                _exportWorkbookButton.IsEnabled = true;
                return;
            }

            int n = _selectedSheets.Count;
            _workbookStatus.Text = n == 0
                ? "No tables added to workbook"
                : n == 1 ? "1 table added to workbook"
                         : $"{n} tables added to workbook";
            _exportWorkbookButton.Content = "Export workbook…";
            _exportWorkbookButton.ToolTip = "Write the added tables as one sheet each";
            _exportWorkbookButton.IsEnabled = n > 0;
        }

        // Writes one tab's already-formatted rows to a CSV file (UTF-8 with BOM so
        // Excel reads non-ASCII units correctly).
        private static void ExportCsv(string[] headers, List<string[]> rows, string suggestedFileName)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export Table to CSV",
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = ".csv",
                FileName = suggestedFileName,
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(CsvEscape)));
            foreach (var row in rows)
                sb.AppendLine(string.Join(",", row.Select(CsvEscape)));

            try
            {
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the file:\n{ex.Message}", "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Quotes a field only if it contains a delimiter/quote/newline; embedded
        // quotes are doubled per the CSV convention.
        private static string CsvEscape(string field)
        {
            field ??= "";
            if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        /// <summary>
        /// Writes every mode's measurements as one long CSV. A specimen's rows are kept
        /// together — all five modes for the first specimen, then all five for the next —
        /// so the file reads in specimen order rather than in mode order, and a reader
        /// grouping by specimen finds its rows in one place.
        /// </summary>
        /// <remarks>
        /// The Custom tab is left out. It holds no measurements of its own, only variables
        /// borrowed from the modes, so every value it shows is already in the file once
        /// under the operation that produced it.
        /// </remarks>
        private void ExportLongCsv() =>
            ExportCsv(
                WorkshopTable.LongHeaders(),
                BuildLongRows(_undoRedo, CurrentName, _scale),
                "geometric_data_long.csv");

        // Every mode's rows, interleaved by specimen. The tables are built once and then
        // walked a specimen at a time; each holds one block per specimen in the same
        // order, since every table is built from the same walk of history.
        private static List<string[]> BuildLongRows(
            UndoRedoManager ur, string currentName, ScaleSource scale)
        {
            var tables = Tabs.Select(spec => BuildTable(spec, ur, currentName, scale)).ToList();

            int specimens = tables.Count > 0 ? tables.Max(t => t.Blocks.Count) : 0;
            var rows = new List<string[]>();

            for (int specimen = 0; specimen < specimens; specimen++)
            {
                foreach (var table in tables)
                    table.AppendLongRows(rows, specimen);
            }

            return rows;
        }

        private void ExportWorkbook()
        {
            var sheets = StagedSheets(_undoRedo, CurrentName, _scale);
            if (sheets.Count == 0) return;

            var dlg = new SaveFileDialog
            {
                Title = "Export workbook",
                Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                DefaultExt = ".xlsx",
                FileName = "history_workbook.xlsx",
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                WriteXlsx(dlg.FileName, sheets);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the workbook:\n{ex.Message}", "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion

        #region Workbook entry points

        /// <summary>Unstages every sheet. The selection outlives any one window, so
        /// a session reset has to say so explicitly.</summary>
        public static void ClearStagedSheets() => _selectedSheets.Clear();

        public static void ExportAllOperationHistory(
            UndoRedoManager ur, string currentName, ScaleSource scale)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export operation history",
                Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                DefaultExt = ".xlsx",
                FileName = "operation_history.xlsx",
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                WriteXlsx(dlg.FileName, BuildAllSheets(ur, currentName, scale, unfiltered: true));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the workbook:\n{ex.Message}", "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// Writes the Batch Workshop's Geometric Data workbook: the sheets staged in the
        /// operation history, or every sheet when none have been staged. Every mode is
        /// represented, EFA included.
        public static void ExportAllGeometricData(
            UndoRedoManager ur, string currentName, ScaleSource scale)
        {
            var sheets = _selectedSheets.Count > 0
                ? StagedSheets(ur, currentName, scale)
                : BuildAllSheets(ur, currentName, scale);

            // A staged set naming no table that exists — a hand-edited project file, or
            // one from a version whose tabs were named differently again — would otherwise
            // write nothing and say nothing. Everything is the safer reading of it.
            if (sheets.Count == 0) sheets = BuildAllSheets(ur, currentName, scale);

            if (sheets.Count == 0) return;

            var dlg = new SaveFileDialog
            {
                Title = "Export Geometric Data",
                Filter = "Excel workbook (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                DefaultExt = ".xlsx",
                FileName = "geometric_data.xlsx",
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                WriteXlsx(dlg.FileName, sheets);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the workbook:\n{ex.Message}", "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Staged sheets, rebuilt from live history rather than from the rows captured
        // when the tabs were drawn, so a workbook exported after an edit is current.
        private static List<WorkbookSheet> StagedSheets(
            UndoRedoManager ur, string currentName, ScaleSource scale) =>
            BuildAllSheets(ur, currentName, scale)
                .Where(s => _selectedSheets.Contains(s.Name))
                .ToList();

        // One sheet per tab, in tab order. Unfiltered writes every column whatever has
        // been hidden, which only the complete-record export asks for.
        private static List<WorkbookSheet> BuildAllSheets(
            UndoRedoManager ur, string currentName, ScaleSource scale, bool unfiltered = false)
        {
            var sheets = new List<WorkbookSheet>();

            foreach (var spec in Tabs)
            {
                var table = unfiltered
                    ? BuildUnfilteredTable(spec, ur, currentName, scale)
                    : BuildTable(spec, ur, currentName, scale);

                var (headers, rows) = table.ToCsv();
                sheets.Add(new WorkbookSheet { Name = spec.Name, Headers = headers, Rows = rows });
            }

            if (CustomTableSelection.HasColumns || _selectedSheets.Contains(CustomTable.Key))
            {
                var (headers, rows) = CustomTable.Build(ur, currentName, scale).ToCsv();
                sheets.Add(new WorkbookSheet { Name = CustomTable.Key, Headers = headers, Rows = rows });
            }

            return sheets;
        }

        #endregion

        #region Workshop sidebar exports

        /// Every specimen's operations in order: the archived records followed by the
        /// live history. Exposed so other exporters walk history the same way.
        public static IEnumerable<(string Name, IReadOnlyList<WorkOperation> Ops)> SpecimenBlocks(
            UndoRedoManager ur, string currentName) => Blocks(ur, currentName);

        #endregion

        #region Minimal XLSX writer (OOXML, no external dependency)

        // Hand-builds a minimal .xlsx: a workbook part, one worksheet part per sheet,
        // and a bare styles part (Excel requires styles.xml even when unstyled).
        private static void WriteXlsx(string path, List<WorkbookSheet> sheets)
        {
            const string nsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            const string ctWorkbook = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
            const string ctWorksheet = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
            const string ctStyles = "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";
            const string relOfficeDoc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
            const string relWorksheet = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
            const string relStyles = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";

            using var pkg = Package.Open(path, FileMode.Create);

            var wbUri = new Uri("/xl/workbook.xml", UriKind.Relative);
            var wbPart = pkg.CreatePart(wbUri, ctWorkbook);
            pkg.CreateRelationship(wbUri, TargetMode.Internal, relOfficeDoc, "rId1");

            var stylesUri = new Uri("/xl/styles.xml", UriKind.Relative);
            var stylesPart = pkg.CreatePart(stylesUri, ctStyles);
            WritePartText(stylesPart, StylesXml());
            wbPart.CreateRelationship(stylesUri, TargetMode.Internal, relStyles, "rIdStyles");

            // One worksheet part per sheet, related back to the workbook by rId.
            for (int i = 1; i <= sheets.Count; i++)
            {
                var sheetUri = new Uri($"/xl/worksheets/sheet{i}.xml", UriKind.Relative);
                var sheetPart = pkg.CreatePart(sheetUri, ctWorksheet);
                WritePartText(sheetPart, BuildSheetXml(sheets[i - 1]));
                wbPart.CreateRelationship(sheetUri, TargetMode.Internal, relWorksheet, $"rId{i}");
            }

            // workbook.xml: the <sheets> list Excel uses to find and name each tab.
            var wb = new StringBuilder();
            wb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            wb.Append($"<workbook xmlns=\"{nsMain}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (int i = 1; i <= sheets.Count; i++)
                wb.Append($"<sheet name=\"{XmlEscape(SafeSheetName(sheets[i - 1].Name, i))}\" sheetId=\"{i}\" r:id=\"rId{i}\"/>");
            wb.Append("</sheets></workbook>");
            WritePartText(wbPart, wb.ToString());
        }

        private static void WritePartText(PackagePart part, string content)
        {
            using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }

        // Smallest styles.xml Excel accepts: one default font/fill/border/format,
        // nothing actually styled.
        private static string StylesXml() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border/></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

        // One <row> per data row (header row first); the cell reference (A1, B1, …)
        // comes from column position via ColumnLetter.
        private static string BuildSheetXml(WorkbookSheet sheet)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

            sb.Append("<row r=\"1\">");
            for (int c = 0; c < sheet.Headers.Length; c++)
                sb.Append(InlineStringCell($"{ColumnLetter(c)}1", sheet.Headers[c]));
            sb.Append("</row>");

            int rowNum = 2;
            foreach (var row in sheet.Rows)
            {
                sb.Append($"<row r=\"{rowNum}\">");
                for (int c = 0; c < row.Length; c++)
                    sb.Append(Cell($"{ColumnLetter(c)}{rowNum}", row[c]));
                sb.Append("</row>");
                rowNum++;
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        // Numeric cells (<v>) when the value parses as a number, so Excel treats it
        // as a number (sortable, usable in formulas); otherwise an inline string.
        private static string Cell(string reference, string value)
        {
            if (!string.IsNullOrEmpty(value) &&
                double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                return $"<c r=\"{reference}\"><v>{value}</v></c>";
            return InlineStringCell(reference, value);
        }

        private static string InlineStringCell(string reference, string value) =>
            $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(value)}</t></is></c>";

        // 0-based index -> spreadsheet column letters (0->A, 25->Z, 26->AA):
        // bijective base-26, no zero digit.
        private static string ColumnLetter(int index)
        {
            string s = "";
            index++;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                s = (char)('A' + rem) + s;
                index = (index - 1) / 26;
            }
            return s;
        }

        private static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;");
        }

        // Excel sheet-name rules: no : \ / ? * [ ], max 31 chars, non-empty.
        // Falls back to "Sheet{ordinal}" when blank (or blank after stripping).
        private static string SafeSheetName(string name, int ordinal)
        {
            if (string.IsNullOrWhiteSpace(name)) name = $"Sheet{ordinal}";
            foreach (char bad in new[] { ':', '\\', '/', '?', '*', '[', ']' })
                name = name.Replace(bad, ' ');
            name = name.Trim();
            if (name.Length > 31) name = name.Substring(0, 31);
            if (name.Length == 0) name = $"Sheet{ordinal}";
            return name;
        }

        #endregion

        #region Formatting helpers

        internal static string Fmt(double v) =>
            Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

        internal static string Fmt4(double v) =>
            Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);

        // LineLengthRatio is boxed as a double or "N/A"; handle both.
        internal static string FmtRatio(object ratio) =>
            ratio is double d ? Fmt(d) : ratio?.ToString() ?? "";

        // Real-world units when calibrated, else raw image pixels so the cell is
        // never blank. The input is in image pixels, the unit every stored
        // measurement uses, so the number does not depend on the window size at the
        // moment of export. The calibration is passed by value, and it is the one
        // belonging to the specimen whose row this is: a table spanning specimens
        // scaled differently gives each of them its own ratio and its own unit.
        // The number is written with a decimal point whatever the machine's locale, the
        // same as every other number in these tables. On a locale that writes a decimal
        // comma, one table would otherwise read 0.85 for a ratio and 12,40 mm for a
        // length, and the long export, which reads the number back out of the cell,
        // would hand R and pandas a column of text.
        internal static string FmtLength(double imagePixels, ScaleState scale) =>
            scale.IsSet
                ? scale.ToUnitsFromImage(imagePixels).ToString("F2", CultureInfo.InvariantCulture)
                  + " " + scale.Unit
                : $"{Math.Round(imagePixels, 1).ToString(CultureInfo.InvariantCulture)} px";

        internal static string FmtArea(double imagePixelArea, ScaleState scale) =>
            scale.IsSet
                ? scale.ToUnitsAreaFromImage(imagePixelArea).ToString("F2", CultureInfo.InvariantCulture)
                  + " " + scale.Unit + "\u00B2"
                : $"{Math.Round(imagePixelArea, 1).ToString(CultureInfo.InvariantCulture)} px\u00B2";

        #endregion
    }
}