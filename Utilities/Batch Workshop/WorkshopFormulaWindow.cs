using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Adds or edits one formula column: its name, the formula, the columns the
    /// formula may read, and a preview of the first values it produces. The caller
    /// stores the result, so nothing here changes a table on its own.
    /// </summary>
    internal class WorkshopFormulaWindow : Window
    {
        private const int PreviewRows = 6;

        private readonly WorkshopTable _table;
        private readonly WorkshopFormulaColumn _existing;

        // Columns the formula is allowed to read: the table's own, less the column
        // being edited, which cannot be written in terms of itself.
        private readonly List<string> _available;

        // Names the new column may not take, which covers the columns on show and any
        // the caller reserves because a wider table already carries them.
        private readonly List<string> _taken;

        private readonly TextBox _nameBox;
        private readonly TextBox _formulaBox;
        private readonly TextBox _filterBox;
        private readonly ListBox _columnList;
        private readonly TextBlock _message;
        private readonly TextBlock _preview;
        private readonly Button _okButton;

        /// <summary>The name entered, trimmed and capped.</summary>
        public string ColumnName { get; private set; }

        /// <summary>The formula entered, once it reads correctly.</summary>
        public Formula Result { get; private set; }

        /// <summary>True when the user asked for the column to be deleted instead.</summary>
        public bool DeleteRequested { get; private set; }

        public WorkshopFormulaWindow(
            WorkshopTable table, WorkshopFormulaColumn existing, IEnumerable<string> reservedNames = null)
        {
            _table = table;
            _existing = existing;

            // Columns are calculated in the order they were made, so a formula may
            // read the measurements and the columns made before its own, and nothing
            // from its own position onwards.
            var later = new HashSet<string>(
                WorkshopFormulas.NamesFrom(existing), StringComparer.OrdinalIgnoreCase);

            _available = (table?.MeasurementHeaders ?? new string[0])
                .Where(h => !string.IsNullOrEmpty(h) && !later.Contains(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _taken = _available
                .Concat(reservedNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrEmpty(n))
                .Where(n => existing == null
                            || !string.Equals(n, existing.Name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Title = existing == null ? "Add column" : "Edit column";
            Width = 720;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock { Text = "Column name" });

            _nameBox = new TextBox
            {
                Text = existing == null ? "" : existing.Name,
                MaxLength = WorkshopFormulas.MaxNameLength,
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 12)
            };
            _nameBox.TextChanged += (s, e) => Validate();
            panel.Children.Add(_nameBox);

            panel.Children.Add(new TextBlock { Text = "Formula" });

            var body = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };

            var entry = new DockPanel();
            var equals = new TextBlock
            {
                Text = "=",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(equals, Dock.Left);
            entry.Children.Add(equals);

            _formulaBox = new TextBox
            {
                Text = existing == null ? "" : existing.Text,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                MinHeight = 48,
                MaxLength = FormulaCompiler.MaxLength,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _formulaBox.TextChanged += (s, e) => Validate();
            entry.Children.Add(_formulaBox);
            left.Children.Add(entry);

            left.Children.Add(new TextBlock
            {
                Text = "A column name stands for that column's value on the same row, as in "
                       + "outline_area / outline_perim^2. Type a column name straight into the "
                       + "formula, or find it in the list and use Insert into formula. "
                       + "Operators: + - * / ^ and brackets.\n"
                       + FormulaCompiler.FunctionHelp + "\n"
                       + "A blank cell is not zero: arithmetic touching a blank leaves the result blank. "
                       + "SUM, AVERAGE, MIN, MAX, COUNT, MEDIAN and PRODUCT skip blank arguments, and "
                       + "IFBLANK(value, 0) puts a number in their place.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 0)
            });

            _message = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            left.Children.Add(_message);

            _preview = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 6, 0, 0)
            };
            left.Children.Add(_preview);

            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            var right = new StackPanel { Width = 210 };
            right.Children.Add(new TextBlock { Text = "Columns in this table" });

            // Typing here narrows the list, so a column can be reached by name
            // without scrolling for it.
            _filterBox = new TextBox
            {
                Margin = new Thickness(0, 4, 0, 4),
                ToolTip = "Type part of a column name to find it, then press Enter to insert it"
            };
            _filterBox.TextChanged += (s, e) => ApplyFilter();
            _filterBox.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;

                InsertSelectedColumn();
                e.Handled = true;
            };
            right.Children.Add(_filterBox);

            _columnList = new ListBox
            {
                ItemsSource = _available,
                Height = 168,
                Margin = new Thickness(0, 0, 0, 6)
            };
            _columnList.MouseDoubleClick += (s, e) => InsertSelectedColumn();
            right.Children.Add(_columnList);

            var insert = new Button
            {
                Content = "Insert into formula",
                Padding = new Thickness(8, 3, 8, 3)
            };
            insert.Click += (s, e) => InsertSelectedColumn();
            right.Children.Add(insert);

            Grid.SetColumn(right, 1);
            body.Children.Add(right);

            panel.Children.Add(body);

            // ---- Footer ----

            var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (existing != null)
            {
                var delete = new Button
                {
                    Content = "Delete column",
                    Padding = new Thickness(12, 4, 12, 4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "Remove this column from the table and its exports"
                };
                delete.Click += (s, e) => Delete();

                Grid.SetColumn(delete, 0);
                footer.Children.Add(delete);
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            _okButton = new Button
            {
                Content = "OK",
                MinWidth = 84,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
                IsEnabled = false
            };
            _okButton.Click += (s, e) => Accept();
            buttons.Children.Add(_okButton);

            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 84,
                Padding = new Thickness(12, 4, 12, 4),
                IsCancel = true
            };
            buttons.Children.Add(cancel);

            Grid.SetColumn(buttons, 1);
            footer.Children.Add(buttons);

            panel.Children.Add(footer);

            Content = panel;

            Validate();

            Loaded += (s, e) =>
            {
                if (_nameBox.Text.Length == 0) _nameBox.Focus();
                else _formulaBox.Focus();
            };
        }

        /// <summary>
        /// The name a renamed column should keep. A column other formulas read cannot
        /// be renamed without leaving those formulas naming something their table no
        /// longer holds, so the choice of renaming it belongs to the user.
        /// </summary>
        internal static string KeptName(Window owner, WorkshopFormulaColumn existing, string name)
        {
            if (existing == null) return name;
            if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)) return name;

            var dependents = WorkshopFormulas.DependentsOf(existing);
            if (dependents.Count == 0) return name;

            var answer = MessageBox.Show(
                owner,
                "These columns are calculated from \"" + existing.Name + "\": "
                + string.Join(", ", dependents) + ".\n\n"
                + "Renaming it to \"" + name + "\" stops them being calculated until their formulas "
                + "are updated. Rename it anyway?",
                "Rename column",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return answer == MessageBoxResult.Yes ? name : existing.Name;
        }

        // ---- Editing ----

        // Narrows the column list to the names holding the typed text.
        private void ApplyFilter()
        {
            if (_columnList == null) return;

            string filter = _filterBox.Text.Trim();

            var matches = filter.Length == 0
                ? _available
                : _available
                    .Where(c => c.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

            _columnList.ItemsSource = matches;
            if (matches.Count > 0) _columnList.SelectedIndex = 0;
        }

        private void InsertSelectedColumn()
        {
            string name = _columnList.SelectedItem as string;
            if (string.IsNullOrEmpty(name)) return;

            int caret = _formulaBox.SelectionStart;

            _formulaBox.Text = _formulaBox.Text
                .Remove(caret, _formulaBox.SelectionLength)
                .Insert(caret, name);

            _formulaBox.SelectionStart = caret + name.Length;
            _formulaBox.SelectionLength = 0;
            _formulaBox.Focus();
        }

        /// Checks the name and the formula, and shows the first problem found or the
        /// values the formula would produce.
        private void Validate()
        {
            Result = null;
            ColumnName = "";

            string nameError;
            bool nameOk = WorkshopFormulas.NameIsAvailable(_nameBox.Text, _existing, _taken, out nameError);

            Formula formula;
            string formulaError;
            bool formulaOk = FormulaCompiler.TryCompile(_formulaBox.Text, _available, out formula, out formulaError);

            if (!nameOk || !formulaOk)
            {
                _message.Foreground = Brushes.Firebrick;
                _message.Text = nameOk ? formulaError : nameError;
                _preview.Text = "";
                _okButton.IsEnabled = false;
                return;
            }

            Result = formula;
            ColumnName = WorkshopFormulas.Clip(_nameBox.Text);

            var lines = WorkshopFormulaEvaluator.Preview(_table, formula, PreviewRows);

            _message.Foreground = Brushes.Gray;
            _message.Text = lines.Count == 0
                ? "The formula reads correctly. There are no measured rows to preview yet."
                : "The formula reads correctly. First rows:";

            _preview.Text = string.Join("\n", lines);
            _okButton.IsEnabled = true;
        }

        private void Accept()
        {
            if (Result == null) return;

            Finish();
        }

        private void Delete()
        {
            var dependents = WorkshopFormulas.DependentsOf(_existing);

            string message = dependents.Count == 0
                ? "Delete the column \"" + _existing.Name + "\"?\n\n"
                  + "It is removed from this table and from every export of it."
                : "Delete the column \"" + _existing.Name + "\"?\n\n"
                  + "These columns are calculated from it and will stop appearing as well: "
                  + string.Join(", ", dependents) + ".";

            var confirm = MessageBox.Show(
                this, message, "Delete column", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            DeleteRequested = true;
            Finish();
        }

        // DialogResult may only be set while the window runs modally.
        private void Finish()
        {
            try
            {
                DialogResult = true;
            }
            catch (InvalidOperationException)
            {
                Close();
            }
        }
    }
}
