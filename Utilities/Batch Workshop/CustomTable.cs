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

        public static bool IsSelected(WorkshopCategory category, string header) =>
            Find(category, header) != null;

        /// <summary>Adds a variable to the table, or takes it out when it is already there.</summary>
        public static void Toggle(WorkshopCategory category, string header)
        {
            var existing = Find(category, header);

            if (existing != null) _columns.Remove(existing);
            else _columns.Add(new CustomTableColumn { Category = category, Header = header });

            ProjectSession.MarkChanged();
        }

        public static void Clear() => _columns.Clear();

        private static CustomTableColumn Find(WorkshopCategory category, string header) =>
            _columns.FirstOrDefault(c => c.Category == category
                                         && string.Equals(c.Header, header, StringComparison.OrdinalIgnoreCase));
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
            UndoRedoManager undoRedo, string currentName, ScaleCalibration scale)
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
            UndoRedoManager undoRedo, string currentName, ScaleCalibration scale)
        {
            var catalog = new List<CustomTableGroup>();
            if (undoRedo == null) return catalog;

            foreach (var category in Categories)
            {
                var source = WorkshopTables.Build(category, undoRedo, currentName, scale);
                var headers = new List<string>();

                for (int c = 0; c < source.MeasurementHeaders.Length; c++)
                {
                    string header = source.MeasurementHeaders[c];

                    // A variable already in the table keeps its place on the list even
                    // once its measurements are gone, so it can be taken out again.
                    if (HasValues(source, c) || CustomTableSelection.IsSelected(category, header))
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
            UndoRedoManager undoRedo, string currentName, ScaleCalibration scale)
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

            // Where each chosen variable sits in the table it came from.
            var positions = new int[chosen.Count];
            for (int i = 0; i < chosen.Count; i++)
                positions[i] = IndexOf(sources[chosen[i].Category], chosen[i].Header);

            int blockCount = sources.Values.Min(s => s.Blocks.Count);

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

                // Rows of this specimen that hold an actual attempt, per source.
                var measured = new Dictionary<WorkshopCategory, int>();
                foreach (var source in sources)
                    measured[source.Key] = Measured(source.Value.Blocks[b]);

                int rowCount = measured.Values.Max();

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
                        if (positions[i] < 0) continue;

                        if (a >= measured[chosen[i].Category]) continue;

                        var sourceRow = sources[chosen[i].Category].Blocks[b].Rows[a];
                        if (positions[i] < sourceRow.Cells.Length)
                            row.Cells[i] = sourceRow.Cells[positions[i]] ?? "";
                    }

                    // The operations standing behind the row, so the table can tell a
                    // row of measurements from a row of blanks.
                    foreach (var source in sources)
                    {
                        if (a >= measured[source.Key]) continue;

                        foreach (var operation in source.Value.Blocks[b].Rows[a].Operations)
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
