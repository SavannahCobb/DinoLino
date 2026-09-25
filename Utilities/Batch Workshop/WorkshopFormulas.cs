using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DinoLino.Utilities
{
    /// <summary>One user-defined column: its name, the formula behind it, and its table.</summary>
    public sealed class WorkshopFormulaColumn
    {
        public string TableKey;
        public string Name;
        public Formula Expression;

        /// <summary>The formula as the user typed it, without a leading "=".</summary>
        public string Text => Expression == null ? "" : Expression.Text;
    }

    /// <summary>
    /// Session-scoped formula columns, one set per data table. Each is calculated row
    /// by row while the table is built, so it reaches the Batch Workshop tables, the
    /// History window, every CSV and workbook export, and the plot and PCA variable
    /// lists at the same time as the measurements themselves.
    ///
    /// A column may read the measurements of its table and any formula column made
    /// before it, which is what keeps the order they were created in meaningful.
    /// </summary>
    public static class WorkshopFormulas
    {
        /// <summary>Longest permitted column name, matching the group columns.</summary>
        public const int MaxNameLength = 14;

        // Creation order, preserved so a formula can build on the ones before it.
        private static readonly List<WorkshopFormulaColumn> _columns = new List<WorkshopFormulaColumn>();

        public static IReadOnlyList<WorkshopFormulaColumn> All => _columns;

        /// <summary>One table's formula columns, in creation order.</summary>
        public static List<WorkshopFormulaColumn> ColumnsFor(string tableKey) =>
            string.IsNullOrEmpty(tableKey)
                ? new List<WorkshopFormulaColumn>()
                : _columns
                    .Where(c => string.Equals(c.TableKey, tableKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();

        /// <summary>Trims a name and caps it at the permitted length.</summary>
        public static string Clip(string name)
        {
            name = (name ?? "").Trim();
            return name.Length <= MaxNameLength ? name : name.Substring(0, MaxNameLength);
        }

        /// <summary>Adds a column, or rewrites one in place so it keeps its position.</summary>
        public static void Save(WorkshopFormulaColumn existing, string tableKey, string name, Formula formula)
        {
            if (formula == null) return;

            name = Clip(name);
            if (name.Length == 0) return;

            if (existing != null && _columns.Contains(existing))
            {
                existing.Name = name;
                existing.Expression = formula;
                ProjectSession.MarkChanged();
                return;
            }

            _columns.Add(new WorkshopFormulaColumn
            {
                TableKey = tableKey,
                Name = name,
                Expression = formula
            });

            ProjectSession.MarkChanged();
        }

        public static void Remove(WorkshopFormulaColumn column)
        {
            if (column != null && _columns.Remove(column)) ProjectSession.MarkChanged();
        }

        /// <summary>Drops every formula column of every table.</summary>
        public static void Clear() => _columns.Clear();

        /// <summary>
        /// The name of a column together with those of every column made after it in
        /// the same table. A formula for that column cannot read any of them, because
        /// the table is filled in the order the columns were made.
        /// </summary>
        public static List<string> NamesFrom(WorkshopFormulaColumn column)
        {
            var names = new List<string>();
            if (column == null) return names;

            bool reached = false;

            foreach (var other in ColumnsFor(column.TableKey))
            {
                if (ReferenceEquals(other, column)) reached = true;
                if (reached) names.Add(other.Name);
            }

            return names;
        }

        /// <summary>Names of the columns whose formulas read the given column.</summary>
        public static List<string> DependentsOf(WorkshopFormulaColumn column)
        {
            if (column == null) return new List<string>();

            return _columns
                .Where(c => !ReferenceEquals(c, column)
                            && string.Equals(c.TableKey, column.TableKey, StringComparison.OrdinalIgnoreCase)
                            && c.Expression != null
                            && c.Expression.References.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .ToList();
        }

        /// <summary>
        /// Checks a proposed column name against the naming rules, the columns the
        /// table already shows, and the formula columns of every other table. The
        /// caller leaves the edited column's own name out of <paramref name="takenHeaders"/>.
        /// </summary>
        public static bool NameIsAvailable(
            string name, WorkshopFormulaColumn except, IEnumerable<string> takenHeaders, out string error)
        {
            error = "";
            name = (name ?? "").Trim();

            if (name.Length == 0)
            {
                error = "Enter a column name.";
                return false;
            }

            if (name.Length > MaxNameLength)
            {
                error = "A column name may be at most " + MaxNameLength + " characters long.";
                return false;
            }

            if (!char.IsLetter(name[0]) && name[0] != '_')
            {
                error = "A column name starts with a letter.";
                return false;
            }

            foreach (var character in name)
            {
                if (char.IsLetterOrDigit(character) || character == '_') continue;

                error = "A column name holds letters, digits and underscores only.";
                return false;
            }

            if (FormulaCompiler.IsFunctionName(name))
            {
                error = "\"" + name + "\" is the name of a formula function.";
                return false;
            }

            if (string.Equals(name, "Specimen", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Attempt", StringComparison.OrdinalIgnoreCase))
            {
                error = "\"" + name + "\" is a column every table already has.";
                return false;
            }

            if (takenHeaders != null && takenHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                error = "This table already has a column named \"" + name + "\".";
                return false;
            }

            if (SpecimenGroups.Columns.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                error = "\"" + name + "\" is a group column.";
                return false;
            }

            foreach (var column in _columns)
            {
                if (ReferenceEquals(column, except)) continue;
                if (!string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

                error = "\"" + name + "\" is already a formula column in the " + column.TableKey + " table.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads the number a table cell shows. Cells carry their units, as in
        /// "4.56 cm" or "270 px²", so the leading number is what a formula works with.
        /// Cells holding no number at all, such as a blank or "N/A", read as blank.
        /// </summary>
        public static bool TryReadNumber(string cell, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(cell)) return false;

            string text = cell.Trim();
            int end = 0;

            if (end < text.Length && (text[end] == '+' || text[end] == '-')) end++;
            while (end < text.Length && char.IsDigit(text[end])) end++;

            if (end < text.Length && text[end] == '.')
            {
                end++;
                while (end < text.Length && char.IsDigit(text[end])) end++;
            }

            if (end < text.Length && (text[end] == 'e' || text[end] == 'E'))
            {
                int scan = end + 1;
                if (scan < text.Length && (text[scan] == '+' || text[scan] == '-')) scan++;

                if (scan < text.Length && char.IsDigit(text[scan]))
                {
                    while (scan < text.Length && char.IsDigit(text[scan])) scan++;
                    end = scan;
                }
            }

            return double.TryParse(
                text.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>Calculates the formula columns of a table that has just been built.</summary>
    public static class WorkshopFormulaEvaluator
    {
        /// <summary>Decimal places kept in a calculated cell.</summary>
        private const int Decimals = 4;

        /// <summary>Shown in place of a number the arithmetic could not produce.</summary>
        private const string ErrorCell = "#ERR";

        /// <summary>
        /// Appends the table's formula columns and fills them in. A column whose
        /// formula names something this table does not hold is left out, which is what
        /// lets one table carry formulas the History window's narrower tabs cannot.
        /// </summary>
        public static void Apply(WorkshopTable table, string tableKey)
        {
            if (table == null || table.MeasurementHeaders == null) return;

            var formulas = WorkshopFormulas.ColumnsFor(tableKey);
            if (formulas.Count == 0) return;

            var headers = new List<string>(table.MeasurementHeaders);
            var available = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
            var applicable = new List<WorkshopFormulaColumn>();

            foreach (var column in formulas)
            {
                if (column.Expression == null) continue;
                if (available.Contains(column.Name)) continue;
                if (!column.Expression.References.All(available.Contains)) continue;

                applicable.Add(column);
                available.Add(column.Name);
                headers.Add(column.Name);
            }

            if (applicable.Count == 0) return;

            int first = table.MeasurementHeaders.Length;

            Widen(table, headers.Count);
            table.MeasurementHeaders = headers.ToArray();

            var index = Index(headers);

            // In creation order, so a formula reading an earlier column finds its
            // values already in place.
            for (int i = 0; i < applicable.Count; i++)
                Fill(table, index, applicable[i].Expression, first + i);
        }

        /// <summary>
        /// The first rows a formula would produce, labelled by specimen and attempt,
        /// for the preview in the formula dialog.
        /// </summary>
        public static List<string> Preview(WorkshopTable table, Formula formula, int maxRows)
        {
            var lines = new List<string>();
            if (table == null || table.MeasurementHeaders == null || formula == null) return lines;

            var context = new TableContext(table, Index(new List<string>(table.MeasurementHeaders)));

            foreach (var block in table.Blocks)
            {
                context.SetBlock(block);

                foreach (var row in block.Rows)
                {
                    if (row.Attempt <= 0) continue;
                    if (lines.Count >= maxRows) return lines;

                    context.SetRow(row);

                    string value = Format(formula.Evaluate(context));
                    lines.Add(block.Name + ", attempt " + row.Attempt + ":  "
                              + (value.Length == 0 ? "(blank)" : value));
                }
            }

            return lines;
        }

        private static void Fill(
            WorkshopTable table, Dictionary<string, int> index, Formula formula, int column)
        {
            var context = new TableContext(table, index);

            foreach (var block in table.Blocks)
            {
                context.SetBlock(block);

                foreach (var row in block.Rows)
                {
                    // A specimen with no measurements of this kind keeps its empty row.
                    if (row.Attempt <= 0) continue;

                    context.SetRow(row);
                    row.Cells[column] = Format(formula.Evaluate(context));
                }
            }
        }

        private static void Widen(WorkshopTable table, int columns)
        {
            foreach (var block in table.Blocks)
            {
                foreach (var row in block.Rows)
                {
                    if (row.Cells == null) row.Cells = new string[columns];
                    else if (row.Cells.Length < columns) Array.Resize(ref row.Cells, columns);
                }
            }
        }

        private static Dictionary<string, int> Index(List<string> headers)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < headers.Count; i++)
            {
                // A header claimed twice keeps its first position, which is the column
                // a formula naming it reads.
                if (!index.ContainsKey(headers[i])) index[headers[i]] = i;
            }

            return index;
        }

        private static string Format(double? value)
        {
            if (!value.HasValue) return "";

            double number = value.Value;
            if (double.IsNaN(number) || double.IsInfinity(number)) return ErrorCell;

            return Math.Round(number, Decimals).ToString(CultureInfo.InvariantCulture);
        }

        /// One row of a built table, with the column readings a formula asks for.
        private sealed class TableContext : IFormulaContext
        {
            private readonly WorkshopTable _table;
            private readonly Dictionary<string, int> _index;

            // Whole-column readings hold for the table; specimen readings are dropped
            // each time the walk moves on to the next specimen.
            private readonly Dictionary<string, IReadOnlyList<double>> _tableValues =
                new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase);

            private readonly Dictionary<string, IReadOnlyList<double>> _specimenValues =
                new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase);

            private WorkshopBlock _block;
            private WorkshopRow _row;

            public TableContext(WorkshopTable table, Dictionary<string, int> index)
            {
                _table = table;
                _index = index;
            }

            public void SetBlock(WorkshopBlock block)
            {
                _block = block;
                _specimenValues.Clear();
            }

            public void SetRow(WorkshopRow row) => _row = row;

            public double? Value(string column)
            {
                int position;
                if (_row == null || !_index.TryGetValue(column, out position)) return null;
                if (_row.Cells == null || position >= _row.Cells.Length) return null;

                double value;
                return WorkshopFormulas.TryReadNumber(_row.Cells[position], out value)
                    ? value
                    : (double?)null;
            }

            public IReadOnlyList<double> ColumnValues(string column)
            {
                IReadOnlyList<double> values;
                if (_tableValues.TryGetValue(column, out values)) return values;

                var collected = new List<double>();
                foreach (var block in _table.Blocks)
                    Collect(block, column, collected);

                _tableValues[column] = collected;
                return collected;
            }

            public IReadOnlyList<double> SpecimenValues(string column)
            {
                if (_block == null) return new List<double>();

                IReadOnlyList<double> values;
                if (_specimenValues.TryGetValue(column, out values)) return values;

                var collected = new List<double>();
                Collect(_block, column, collected);

                _specimenValues[column] = collected;
                return collected;
            }

            private void Collect(WorkshopBlock block, string column, List<double> into)
            {
                int position;
                if (!_index.TryGetValue(column, out position)) return;

                foreach (var row in block.Rows)
                {
                    if (row.Attempt <= 0) continue;
                    if (row.Cells == null || position >= row.Cells.Length) continue;

                    double value;
                    if (WorkshopFormulas.TryReadNumber(row.Cells[position], out value)) into.Add(value);
                }
            }
        }
    }
}
