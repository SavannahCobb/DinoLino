using System;
using System.Collections.Generic;
using System.Linq;

namespace DinoLino.Utilities
{
    /// <summary>One variable chosen for the custom table: its mode and its column.</summary>
    public sealed class CustomTableColumn
    {
        public WorkshopCategory Category;
        public string Header;
    }

    /// <summary>
    /// The variables the custom table shows, in the order they were ticked. Any
    /// measurement or formula column of any mode may be chosen, and the choice lasts
    /// for the session.
    /// </summary>
    public static class CustomTableSelection
    {
        private static readonly List<CustomTableColumn> _columns = new List<CustomTableColumn>();

        public static IReadOnlyList<CustomTableColumn> Columns => _columns;

        public static bool HasColumns => _columns.Count > 0;

        public static bool IsSelected(string header) => Find(header) != null;

        /// <summary>The mode a selected variable is read from first, or null when it is not selected.</summary>
        public static WorkshopCategory? CategoryOf(string header) => Find(header)?.Category;

        /// <summary>Adds a variable to the table, or takes it out when it is already there.</summary>
        public static void Toggle(WorkshopCategory category, string header)
        {
            var existing = Find(header);

            if (existing != null) _columns.Remove(existing);
            else _columns.Add(new CustomTableColumn { Category = category, Header = header });

            ProjectSession.MarkChanged();
        }

        public static void Clear() => _columns.Clear();

        // A variable more than one mode measures is one quantity under one name, so
        // the name alone identifies it. The mode a column carries says which table to
        // read it from first, not which variable it is.
        private static CustomTableColumn Find(string header) =>
            _columns.FirstOrDefault(c =>
                string.Equals(c.Header, header, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the custom table: the chosen variables of every mode side by side. Rows
    /// follow the same rule as a single mode's table, so attempt n of each kind shares
    /// a row and a kind with fewer attempts leaves its cells blank. Formula columns
    /// written for this table are calculated over whatever it currently holds.
    /// </summary>
    public static class CustomTable
    {
        /// <summary>Names the tab, the workbook sheet, and the table's formula columns.</summary>
        public const string Key = "Custom";

        public const string FileName = "custom_data.csv";

        // The modes whose variables can be chosen. The silhouette gallery is left out:
        // it has no column-based table of its own.
        private static readonly WorkshopCategory[] Categories =
        {
            WorkshopCategory.Curvature,
            WorkshopCategory.Angle,
            WorkshopCategory.Shape,
            WorkshopCategory.OutlineMetadata,
            WorkshopCategory.Efa
        };

        public static WorkshopTable Build(
            UndoRedoManager undoRedo, string currentName, ScaleSource scale)
        {
            var chosen = CustomTableSelection.Columns.ToList();

            var table = new WorkshopTable
            {
                Key = Key,
                Title = Key,
                MeasurementHeaders = chosen.Select(c => c.Header).ToArray()
            };

            if (undoRedo != null && chosen.Count > 0)
                Fill(table, chosen, undoRedo, currentName, scale);

            WorkshopFormulaEvaluator.Apply(table, Key);

            return table;
        }

        /// <summary>
        /// Every variable on offer, grouped by the mode that measured it. A column no
        /// specimen has filled in yet is left out, so the list holds what can actually
        /// be tabled.
        /// </summary>
        public static List<CustomTableGroup> Catalog(
            UndoRedoManager undoRedo, string currentName, ScaleSource scale)
        {
            var catalog = new List<CustomTableGroup>();
            if (undoRedo == null) return catalog;

            // Names already spoken for, so a quantity two modes both measure is
            // offered once rather than once per mode.
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var category in Categories)
            {
                var source = WorkshopTables.Build(category, undoRedo, currentName, scale);
                var headers = new List<string>();

                for (int c = 0; c < source.MeasurementHeaders.Length; c++)
                {
                    string header = source.MeasurementHeaders[c];

                    // A variable already in the table keeps its place on the list even
                    // once its measurements are gone, so it can be taken out again. That
                    // place is under the mode it was taken from, so a mode with nothing
                    // measured cannot hold the name open against one that has.
                    if (!HasValues(source, c) && CustomTableSelection.CategoryOf(header) != category)
                        continue;

                    if (!listed.Add(header)) continue;

                    headers.Add(header);
                }

                if (headers.Count > 0)
                {
                    catalog.Add(new CustomTableGroup
                    {
                        Category = category,
                        Title = WorkshopTables.TitleFor(category),
                        Headers = headers
                    });
                }
            }

            return catalog;
        }

        private static void Fill(
            WorkshopTable table, IReadOnlyList<CustomTableColumn> chosen,
            UndoRedoManager undoRedo, string currentName, ScaleSource scale)
        {
            // One source table per mode the choice touches. All of them list the same
            // specimens in the same order, which is what lets their rows line up.
            var sources = new Dictionary<WorkshopCategory, WorkshopTable>();

            foreach (var column in chosen)
            {
                if (!sources.ContainsKey(column.Category))
                {
                    sources[column.Category] =
                        WorkshopTables.Build(column.Category, undoRedo, currentName, scale);
                }
            }

            // A quantity two modes both measure is listed once and so is read from one
            // of them, which would leave the other's rows blank. These extra tables
            // answer that lookup. They are consulted for values only: how many rows a
            // specimen gets is still settled by the modes actually chosen from.
            var lookups = new Dictionary<WorkshopCategory, WorkshopTable>(sources);

            foreach (var category in Categories)
            {
                if (lookups.ContainsKey(category)) continue;

                var extra = WorkshopTables.Build(category, undoRedo, currentName, scale);
                if (chosen.Any(c => IndexOf(extra, c.Header) >= 0)) lookups[category] = extra;
            }

            // Every table each chosen variable can be read from, its own mode first.
            var readOrder = new List<WorkshopCategory>[chosen.Count];
            var positions = new Dictionary<WorkshopCategory, int[]>();

            foreach (var pair in lookups)
            {
                var found = new int[chosen.Count];
                for (int i = 0; i < chosen.Count; i++)
                    found[i] = IndexOf(pair.Value, chosen[i].Header);
                positions[pair.Key] = found;
            }

            for (int i = 0; i < chosen.Count; i++)
            {
                var order = new List<WorkshopCategory> { chosen[i].Category };

                // Walked in Categories order rather than the dictionary's, so the
                // table a value comes from does not turn on hashing.
                foreach (var category in Categories)
                {
                    if (category == chosen[i].Category) continue;
                    if (lookups.ContainsKey(category) && positions[category][i] >= 0)
                        order.Add(category);
                }

                readOrder[i] = order;
            }

            // Every mode that supplies one of the chosen variables. A row belongs in
            // the table when any of these has an attempt for it, so a specimen measured
            // only in the mode a shared variable was NOT taken from still shows it.
            var contributing = new List<WorkshopCategory>();
            foreach (var category in Categories)
            {
                if (readOrder.Any(o => o.Contains(category))) contributing.Add(category);
            }

            int blockCount = lookups.Values.Min(s => s.Blocks.Count);

            for (int b = 0; b < blockCount; b++)
            {
                var reference = sources[chosen[0].Category].Blocks[b];

                var block = new WorkshopBlock
                {
                    Name = reference.Name,
                    Record = reference.Record,
                    IsActive = reference.IsActive,
                    AllOperations = reference.AllOperations
                };

                // Rows of this specimen that hold an actual attempt, per table.
                var measured = new Dictionary<WorkshopCategory, int>();
                foreach (var source in lookups)
                    measured[source.Key] = Measured(source.Value.Blocks[b]);

                int rowCount = contributing.Max(k => measured[k]);

                if (rowCount == 0)
                {
                    // Keep the specimen visible with one empty row.
                    block.Rows.Add(new WorkshopRow
                    {
                        Attempt = 0,
                        Cells = new string[chosen.Count]
                    });

                    table.Blocks.Add(block);
                    continue;
                }

                for (int a = 0; a < rowCount; a++)
                {
                    var row = new WorkshopRow
                    {
                        Attempt = a + 1,
                        Cells = new string[chosen.Count]
                    };

                    for (int i = 0; i < chosen.Count; i++)
                    {
                        foreach (var category in readOrder[i])
                        {
                            int position = positions[category][i];
                            if (position < 0) continue;
                            if (a >= measured[category]) continue;

                            var sourceRow = lookups[category].Blocks[b].Rows[a];
                            if (position >= sourceRow.Cells.Length) continue;

                            // A blank is not an answer: within one mode a shared
                            // variable sits on one kind of operation only, so the rows
                            // holding the other kinds leave it empty and the mode that
                            // does carry it for this row answers instead. Every value
                            // still belongs to an operation of this same row.
                            string cell = sourceRow.Cells[position];
                            if (string.IsNullOrEmpty(cell)) continue;

                            row.Cells[i] = cell;
                            break;
                        }
                    }

                    // The operations standing behind the row, so the table can tell a
                    // row of measurements from a row of blanks. Taken from the same
                    // modes the values were, so a row is never backed by less than what
                    // put it there.
                    foreach (var category in contributing)
                    {
                        if (a >= measured[category]) continue;

                        foreach (var operation in lookups[category].Blocks[b].Rows[a].Operations)
                        {
                            if (!row.Operations.Contains(operation)) row.Operations.Add(operation);
                        }
                    }

                    block.Rows.Add(row);
                }

                table.Blocks.Add(block);
            }
        }

        // Rows holding an actual attempt, which excludes the placeholder row a
        // specimen with no measurements of that kind gets.
        private static int Measured(WorkshopBlock block) => block.Rows.Count(r => r.Attempt > 0);

        private static int IndexOf(WorkshopTable table, string header)
        {
            for (int c = 0; c < table.MeasurementHeaders.Length; c++)
            {
                if (string.Equals(table.MeasurementHeaders[c], header, StringComparison.OrdinalIgnoreCase))
                    return c;
            }

            return -1;
        }

        private static bool HasValues(WorkshopTable table, int column)
        {
            foreach (var block in table.Blocks)
            {
                foreach (var row in block.Rows)
                {
                    if (row.Attempt <= 0) continue;
                    if (column >= row.Cells.Length) continue;
                    if (!string.IsNullOrWhiteSpace(row.Cells[column])) return true;
                }
            }

            return false;
        }
    }

    /// <summary>One mode's variables, as the custom table's picker lists them.</summary>
    public sealed class CustomTableGroup
    {
        public WorkshopCategory Category;
        public string Title;
        public List<string> Headers;
    }
}
