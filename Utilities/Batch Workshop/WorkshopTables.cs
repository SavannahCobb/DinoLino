using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ShapeConstraint = DinoLino.Utilities.Modes.DrawMode.ShapeConstraint;

namespace DinoLino.Utilities
{
    /// <summary>The Batch Workshop rows, each of which has one wide table.</summary>
    public enum WorkshopCategory
    {
        Curvature,
        Angle,
        Shape,
        OutlineMetadata,
        Efa,
        Outlines2D
    }

    /// <summary>
    /// Session-scoped group assignments for specimens. Each group column is a named
    /// mapping from specimen to group name; specimens without an assignment read as
    /// blank. Group columns appear after Specimen and Attempt in every data table
    /// and export, and beside the names in the Directory's Sample tab.
    /// </summary>
    public static class SpecimenGroups
    {
        /// <summary>Longest permitted column or group name.</summary>
        public const int MaxNameLength = 14;

        // Creation order, preserved so every table shows the columns in the order
        // they were made.
        private static readonly List<string> _columns = new List<string>();

        // column -> (specimen -> group). Column lookup ignores case, so "Locality"
        // and "locality" are one column; the first-seen casing is what displays.
        // Specimens are keyed by reference, so renaming one keeps its groups.
        private static readonly Dictionary<string, Dictionary<Specimen, string>> _values =
            new Dictionary<string, Dictionary<Specimen, string>>(StringComparer.OrdinalIgnoreCase);

        // The data tables know specimens only by the name in their rows, so the
        // roster is needed to turn a name back into the specimen that holds the
        // groups. Set once from the Directory panel.
        private static SpecimenManager _manager;

        /// <summary>Supplies the roster used to resolve a row's specimen name.</summary>
        public static void Bind(SpecimenManager manager) => _manager = manager;

        /// <summary>Group column names in creation order.</summary>
        public static IReadOnlyList<string> Columns => _columns;

        public static bool HasColumns => _columns.Count > 0;

        /// <summary>Trims a name and caps it at the permitted length.</summary>
        public static string Clip(string name)
        {
            name = (name ?? "").Trim();
            return name.Length <= MaxNameLength ? name : name.Substring(0, MaxNameLength);
        }

        /// Assigns the given specimens to a group, creating the column when it does
        /// not exist yet. Reassigning a specimen under the same column overwrites
        /// its previous group.
        public static void Assign(string column, string groupName, IEnumerable<Specimen> specimens)
        {
            column = Clip(column);
            groupName = Clip(groupName);
            if (column.Length == 0 || groupName.Length == 0 || specimens == null) return;

            Dictionary<Specimen, string> map;
            if (!_values.TryGetValue(column, out map))
            {
                map = new Dictionary<Specimen, string>();
                _values[column] = map;
                _columns.Add(column);
            }

            foreach (var specimen in specimens)
            {
                if (specimen != null)
                    map[specimen] = groupName;
            }

            ProjectSession.MarkChanged();
        }

        /// Makes a column that no specimen is assigned to yet, so a column saved in
        /// a project comes back even when everything that was in it has gone.
        public static void EnsureColumn(string column)
        {
            column = Clip(column);
            if (column.Length == 0 || _values.ContainsKey(column)) return;

            _values[column] = new Dictionary<Specimen, string>();
            _columns.Add(column);
        }

        /// <summary>Removes every group column and assignment.</summary>
        public static void Clear()
        {
            _columns.Clear();
            _values.Clear();
        }

        /// <summary>The specimen's group under one column, or "" when unassigned.</summary>
        public static string ValueFor(string column, Specimen specimen)
        {
            Dictionary<Specimen, string> map;
            string value;

            if (column == null || specimen == null) return "";
            return _values.TryGetValue(column, out map) && map.TryGetValue(specimen, out value)
                ? value
                : "";
        }

        /// <summary>All group values of one specimen, aligned with Columns.</summary>
        public static string[] ValuesFor(Specimen specimen) =>
            _columns.Select(c => ValueFor(c, specimen)).ToArray();

        /// All group values for the specimen a data-table row names. Unknown names
        /// give blanks, which is also what happens before Bind has been called.
        public static string[] ValuesFor(string specimenName) => ValuesFor(Resolve(specimenName));

        // First live specimen whose display name matches. Two specimens sharing a
        // name are indistinguishable here, exactly as they are in the tables.
        private static Specimen Resolve(string specimenName)
        {
            if (_manager == null || specimenName == null) return null;

            foreach (var specimen in _manager.Specimens)
            {
                if (specimen.Deleted) continue;
                if (string.Equals(_manager.NameOf(specimen), specimenName, StringComparison.Ordinal))
                    return specimen;
            }

            return null;
        }
    }

    /// <summary>
    /// Remembers which columns the user has hidden, per table, for the session.
    /// Hiding is display-and-export only: no measurement is destroyed, so it can be
    /// restored from the edit window at any time.
    /// </summary>
    public static class WorkshopColumnFilter
    {
        private static readonly Dictionary<string, HashSet<string>> _hidden =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public static bool IsHidden(string table, string column)
        {
            HashSet<string> set;
            return _hidden.TryGetValue(table, out set) && set.Contains(column);
        }

        public static void Hide(string table, string column)
        {
            HashSet<string> set;
            if (!_hidden.TryGetValue(table, out set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _hidden[table] = set;
            }
            set.Add(column);
            ProjectSession.MarkChanged();
        }

        /// <summary>Columns hidden from one table, for a project file to record.</summary>
        public static IEnumerable<string> HiddenColumns(string table)
        {
            HashSet<string> set;
            return _hidden.TryGetValue(table, out set)
                ? (IEnumerable<string>)set.ToList()
                : new List<string>();
        }

        /// Brings one column back, so a column hidden by mistake can be recovered
        /// without also un-hiding the ones that were hidden on purpose.
        public static void Show(string table, string column)
        {
            HashSet<string> columns;
            if (_hidden.TryGetValue(table, out columns) && columns.Remove(column))
                ProjectSession.MarkChanged();
        }

        public static void Restore(string table)
        {
            if (_hidden.Remove(table)) ProjectSession.MarkChanged();
        }

        /// <summary>Un-hides every column of every table.</summary>
        public static void RestoreAll() => _hidden.Clear();


        public static int HiddenCount(string table)
        {
            HashSet<string> set;
            return _hidden.TryGetValue(table, out set) ? set.Count : 0;
        }
    }

    // =====================
    // Table description
    // =====================

    /// <summary>One measurement column and how to read it from an operation.</summary>
    public sealed class WorkshopColumn
    {
        public string Header;
        public Func<WorkOperation, string> Value;
    }

    /// All columns contributed by one operation kind. Each kind fills its own columns
    /// and leaves the others blank on the same row.
    public sealed class WorkshopColumnGroup
    {
        public Type OperationType;

        /// The operation as the user knows it — Circular Arc, Triangle, Ellipse. The
        /// type alone will not do: one type covers several kinds, since a drawn
        /// rectangle and a drawn circle are both a ShapeOperation told apart by the
        /// filter below. Written into the long export's Operation column.
        public string Name;

        // Extra condition beyond the type, e.g. outlines that have metadata, or
        // shapes drawn with one particular constraint.
        public Func<WorkOperation, bool> Filter;

        public List<WorkshopColumn> Columns = new List<WorkshopColumn>();

        public bool Accepts(WorkOperation op) =>
            op != null
            && OperationType.IsInstanceOfType(op)
            && (Filter == null || Filter(op));
    }

    // =====================
    // Built table
    // =====================

    /// <summary>One row of the wide table: an attempt index across all operation kinds.</summary>
    public sealed class WorkshopRow
    {
        public int Attempt;                    // 1-based; 0 means a placeholder row
        public string[] Cells;                 // aligned with WorkshopTable.MeasurementHeaders

        /// The operations that supplied this row's values, so the edit window can
        /// delete exactly what the row shows.
        public List<WorkOperation> Operations = new List<WorkOperation>();
    }

    /// <summary>One specimen's block of rows.</summary>
    public sealed class WorkshopBlock
    {
        public string Name;
        public SpecimenRecord Record;          // null for the active specimen
        public bool IsActive;
        public IReadOnlyList<WorkOperation> AllOperations;
        public List<WorkshopRow> Rows = new List<WorkshopRow>();
    }

    /// <summary>A set of column groups as one wide table.</summary>
    public sealed class WorkshopTable
    {
        /// Key under which columns are hidden. Null or empty means the table is not
        /// filtered and always shows every column.
        public string Key;

        public string Title;
        public bool AllowColumnHiding = true;

        // Excludes Specimen, Attempt, and the specimen group columns, all of which
        // are added by the consumers.
        public string[] MeasurementHeaders;

        /// The operation each measurement column came from, aligned with
        /// MeasurementHeaders. Formula columns are appended after these, so this array
        /// is the shorter of the two and a column past its end belongs to no one
        /// operation. Null on a table not built from column groups.
        public string[] ColumnOperations;

        public List<WorkshopBlock> Blocks = new List<WorkshopBlock>();

        /// <summary>Measurement columns actually shown, with hidden ones dropped.</summary>
        public List<int> VisibleColumnIndexes()
        {
            var keep = new List<int>(MeasurementHeaders.Length);
            bool filtered = !string.IsNullOrEmpty(Key);

            for (int i = 0; i < MeasurementHeaders.Length; i++)
            {
                if (!filtered || !WorkshopColumnFilter.IsHidden(Key, MeasurementHeaders[i]))
                    keep.Add(i);
            }
            return keep;
        }

        /// Flattens to CSV form: Specimen, Attempt, the specimen group columns, then
        /// the visible measurement columns. Group columns are never hidden, and every
        /// row of a specimen repeats its group values the way it repeats its name.
        public (string[] Headers, List<string[]> Rows) ToCsv()
        {
            var keep = VisibleColumnIndexes();
            var groupColumns = SpecimenGroups.Columns;
            int groupCount = groupColumns.Count;

            var headers = new string[2 + groupCount + keep.Count];
            headers[0] = "Specimen";
            headers[1] = "Attempt";
            for (int g = 0; g < groupCount; g++)
                headers[2 + g] = groupColumns[g];
            for (int i = 0; i < keep.Count; i++)
                headers[2 + groupCount + i] = MeasurementHeaders[keep[i]];

            var rows = new List<string[]>();
            foreach (var block in Blocks)
            {
                var groupValues = SpecimenGroups.ValuesFor(block.Name);

                foreach (var row in block.Rows)
                {
                    var cells = new string[headers.Length];
                    cells[0] = block.Name;
                    cells[1] = row.Attempt > 0 ? row.Attempt.ToString() : "";
                    for (int g = 0; g < groupCount; g++)
                        cells[2 + g] = groupValues[g];
                    for (int i = 0; i < keep.Count; i++)
                        cells[2 + groupCount + i] = row.Cells[keep[i]] ?? "";
                    rows.Add(cells);
                }
            }

            return (headers, rows);
        }

        /// <summary>
        /// The long shape's column names: what identifies a value, then the value and
        /// its unit. One row per specimen, replicate, operation and variable: the shape
        /// ggplot2 and pandas read as they are given it. A package wanting one row per
        /// specimen — geomorph, Momocs — is pivoted to from here rather than read into
        /// directly, which is the reason Operation and Replicate are columns of their
        /// own: a pivot needs a key it can trust.
        ///
        /// The unit is a column of its own so Value is a bare number. A wide cell reads
        /// "12.40 mm", which makes the whole column text in R; split, Value stays
        /// numeric and the unit is still there to be checked or grouped by.
        /// </summary>
        public static string[] LongHeaders()
        {
            var headers = new List<string> { "Specimen" };
            headers.AddRange(SpecimenGroups.Columns);
            headers.Add("Replicate");
            headers.Add("Operation");
            headers.Add("Variable");
            headers.Add("Value");
            headers.Add("Unit");

            return headers.ToArray();
        }

        /// <summary>
        /// Adds one specimen's values from this table to a long-shape file. Called once
        /// per mode for the same specimen, so a file can hold the whole session with one
        /// specimen's rows together.
        ///
        /// An empty cell is left out: in this shape a blank row would say only that a
        /// measurement was not taken, which its absence already says, and these tables
        /// are mostly blank — one attempt rarely uses every tool of a mode. Hidden
        /// columns are left out too, the same as in the wide shape.
        /// </summary>
        public void AppendLongRows(List<string[]> into, int blockIndex)
        {
            if (into == null || blockIndex < 0 || blockIndex >= Blocks.Count) return;

            var block = Blocks[blockIndex];
            var groupValues = SpecimenGroups.ValuesFor(block.Name);
            int groupCount = SpecimenGroups.Columns.Count;
            var keep = VisibleColumnIndexes();

            foreach (var row in block.Rows)
            {
                foreach (int column in keep)
                {
                    string cell = row.Cells[column];
                    if (string.IsNullOrWhiteSpace(cell)) continue;

                    string value, unit;
                    LongValue(cell, out value, out unit);

                    var cells = new string[groupCount + 6];
                    int i = 0;

                    cells[i++] = block.Name;
                    for (int g = 0; g < groupCount; g++) cells[i++] = groupValues[g];
                    cells[i++] = row.Attempt > 0 ? row.Attempt.ToString() : "";
                    cells[i++] = OperationOf(column);
                    cells[i++] = MeasurementHeaders[column];
                    cells[i++] = value;
                    cells[i] = unit;

                    into.Add(cells);
                }
            }
        }

        // The operation a column came from. A formula column is appended after the ones
        // the groups contributed, so it falls past the end of the array and belongs to
        // no single operation.
        /// Names the operation for the columns past the ones the groups contributed —
        /// the user's own formula columns, which Apply appends after them. The label
        /// carries the table they were made on, since two modes may each hold a formula
        /// column of the same name and a file holding both would otherwise give the two
        /// the same specimen, replicate, operation and variable.
        public void LabelFormulaColumns(string label)
        {
            if (MeasurementHeaders == null || ColumnOperations == null) return;

            int first = ColumnOperations.Length;
            if (MeasurementHeaders.Length <= first) return;

            Array.Resize(ref ColumnOperations, MeasurementHeaders.Length);
            for (int i = first; i < ColumnOperations.Length; i++)
                ColumnOperations[i] = label;
        }

        private string OperationOf(int column) =>
            ColumnOperations != null && column < ColumnOperations.Length
                ? ColumnOperations[column] ?? ""
                : "Formula";

        /// <summary>
        /// One wide cell as a long row's Value and Unit. Value is kept a number in every
        /// row, because one piece of text anywhere in the column makes the whole column
        /// text in R and an object column in pandas, and a plot drawn against it then
        /// fails or sorts the numbers as words.
        /// </summary>
        /// <remarks>
        /// So the three cells that hold no number are written as numbers or as nothing:
        /// a yes/no flag becomes 1 or 0, labelled boolean; a ratio the geometry leaves
        /// undefined becomes empty, which is NA to both; and a formula's error becomes
        /// empty labelled error, so the row still says a value was expected here and the
        /// column can be found with a search.
        /// </remarks>
        private static void LongValue(string cell, out string value, out string unit)
        {
            if (cell == "yes" || cell == "no")
            {
                value = cell == "yes" ? "1" : "0";
                unit = "boolean";
                return;
            }

            if (cell == "N/A")
            {
                value = "";
                unit = "";
                return;
            }

            if (cell == "#ERR")
            {
                value = "";
                unit = "error";
                return;
            }

            SplitValue(cell, out value, out unit);
        }

        /// Splits a formatted cell into its number and its unit. Every cell carrying a
        /// unit was written as the number, a space, then the unit — 12.40 mm, 812.40 mm²,
        /// 4.0 px — so the first space divides them, and the head is only taken as the
        /// value when it reads as a number. Anything else is passed through whole with
        /// no unit; the degrees, ratios and counts the wide table writes bare arrive
        /// here as a bare number and keep an empty unit.
        private static void SplitValue(string cell, out string value, out string unit)
        {
            value = cell;
            unit = "";

            int space = cell.IndexOf(' ');
            if (space <= 0) return;

            string head = cell.Substring(0, space);

            double parsed;
            if (!double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return;

            value = head;
            unit = cell.Substring(space + 1).Trim();
        }

        /// <summary>True when no specimen contributed an actual measurement.</summary>
        public bool IsEmpty =>
            Blocks.All(b => b.Rows.All(r => r.Operations.Count == 0));
    }

    // =====================
    // Builder
    // =====================

    /// <summary>
    /// Builds wide tables from column groups. Operations of different kinds are
    /// joined by attempt number rather than stacked in separate sections, so attempt
    /// 1 of every kind shares a row and kinds with fewer attempts leave blank cells.
    /// The column definitions here are the single source for the Batch Workshop
    /// tables, the History window's tabs, and every export built from either.
    /// </summary>
    public static class WorkshopTables
    {
        /// <summary>One Batch Workshop category as a table.</summary>
        public static WorkshopTable Build(
            WorkshopCategory category, UndoRedoManager undoRedo, string currentName, ScaleSource scales)
        {
            var table = BuildFromGroups(
                KeyFor(category), ColumnGroups(category, undoRedo, scales), undoRedo, currentName, scales);

            table.Title = TitleFor(category);

            // The silhouette export has no column-based CSV, so hiding a column there
            // would not correspond to anything.
            table.AllowColumnHiding = category != WorkshopCategory.Outlines2D;

            return table;
        }

        /// Builds a table from any subset of column groups, so a caller can take one
        /// operation kind out of a category and table it on its own. The formula key
        /// names the table whose formula columns belong here, for a caller that shows
        /// part of a category under no filter key of its own.
        public static WorkshopTable BuildFromGroups(
            string key, IReadOnlyList<WorkshopColumnGroup> groups,
            UndoRedoManager undoRedo, string currentName, ScaleSource scales,
            string formulaKey = null)
        {
            var headers = new List<string>();

            // The operation behind each column, built alongside the names so the long
            // export can say which tool a value came from.
            var operations = new List<string>();

            // Column index where each group's columns start, so a group can write into
            // its own slice of the row and leave the rest blank.
            var offsets = new int[groups.Count];

            for (int g = 0; g < groups.Count; g++)
            {
                offsets[g] = headers.Count;
                foreach (var column in groups[g].Columns)
                {
                    headers.Add(column.Header);
                    operations.Add(groups[g].Name ?? "");
                }
            }

            var table = new WorkshopTable
            {
                Key = key,
                MeasurementHeaders = headers.ToArray(),
                ColumnOperations = operations.ToArray()
            };

            if (undoRedo != null) AddBlocks(table, groups, offsets, undoRedo, currentName, scales);

            // The user's own columns for this table, calculated from the measurement
            // columns and carried into every export, plot and analysis built from it.
            WorkshopFormulaEvaluator.Apply(table, formulaKey ?? key);
            table.LabelFormulaColumns(FormulaOperation(formulaKey ?? key));

            return table;
        }

        // One block of rows per specimen: the archived records, then the live one.
        private static void AddBlocks(
            WorkshopTable table, IReadOnlyList<WorkshopColumnGroup> groups, int[] offsets,
            UndoRedoManager undoRedo, string currentName, ScaleSource scales)
        {
            foreach (var block in EnumerateBlocks(undoRedo, currentName))
            {
                // This specimen's own calibration, before any of its cells are written.
                // The column definitions read it from here, so every block is converted
                // and labelled with the scale measured against its own image.
                scales?.Select(block.Record);

                // Each group's operations for this specimen, in the order they were made.
                var perGroup = new List<List<WorkOperation>>();
                int rowCount = 0;

                foreach (var group in groups)
                {
                    var ops = block.AllOperations.Where(group.Accepts).ToList();
                    perGroup.Add(ops);
                    if (ops.Count > rowCount) rowCount = ops.Count;
                }

                if (rowCount == 0)
                {
                    // Keep the specimen visible with one empty row.
                    block.Rows.Add(new WorkshopRow
                    {
                        Attempt = 0,
                        Cells = new string[table.MeasurementHeaders.Length]
                    });
                    table.Blocks.Add(block);
                    continue;
                }

                for (int i = 0; i < rowCount; i++)
                {
                    var row = new WorkshopRow
                    {
                        Attempt = i + 1,
                        Cells = new string[table.MeasurementHeaders.Length]
                    };

                    for (int g = 0; g < groups.Count; g++)
                    {
                        var ops = perGroup[g];

                        // This kind has fewer attempts than the widest one, so its
                        // cells stay blank on this row.
                        if (i >= ops.Count) continue;

                        var op = ops[i];
                        row.Operations.Add(op);

                        var columns = groups[g].Columns;
                        for (int c = 0; c < columns.Count; c++)
                            row.Cells[offsets[g] + c] = Safe(columns[c].Value, op);
                    }

                    block.Rows.Add(row);
                }

                table.Blocks.Add(block);
            }
        }

        // A malformed operation should blank one cell rather than break the table.
        private static string Safe(Func<WorkOperation, string> getter, WorkOperation op)
        {
            try
            {
                return getter(op) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static IEnumerable<WorkshopBlock> EnumerateBlocks(UndoRedoManager undoRedo, string currentName)
        {
            foreach (var record in undoRedo.Archive)
            {
                yield return new WorkshopBlock
                {
                    Name = record.SpecimenName,
                    Record = record,
                    IsActive = false,
                    AllOperations = record.Operations
                };
            }

            yield return new WorkshopBlock
            {
                Name = currentName,
                Record = null,
                IsActive = true,
                AllOperations = undoRedo.History
            };
        }

        // Columns that have been renamed, and what they are called now. A name is what
        // a saved selection, a hidden column and a formula all refer to, so a project
        // written before the rename would otherwise come back having quietly lost them.
        private static readonly Dictionary<string, string> RenamedColumns =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "tri_anglea", "angle_a" },
                { "tri_angleb", "angle_b" },
                { "tri_anglec", "angle_c" }
            };

        /// What a column name saved by an earlier version is called in this one. A name
        /// this version already uses is returned unchanged, so translating twice is the
        /// same as translating once.
        public static string CurrentColumnName(string saved)
        {
            if (string.IsNullOrEmpty(saved)) return saved;

            string current;
            return RenamedColumns.TryGetValue(saved, out current) ? current : saved;
        }

        /// What a formula column's operation reads. The key is what a project files a
        /// table's formula columns under, and it is the tab's name but for Draw, whose
        /// columns are filed under Shape.
        private static string FormulaOperation(string key) =>
            "Formula (" + (key == "Shape" ? "Draw" : key) + ")";

        public static string KeyFor(WorkshopCategory category)
        {
            switch (category)
            {
                case WorkshopCategory.Curvature: return "Curvature";
                case WorkshopCategory.Angle: return "Angle";
                case WorkshopCategory.Shape: return "Shape";
                case WorkshopCategory.OutlineMetadata: return "Outline";
                case WorkshopCategory.Efa: return "EFA";
                default: return "Outlines2D";
            }
        }

        public static string TitleFor(WorkshopCategory category)
        {
            switch (category)
            {
                case WorkshopCategory.Curvature: return "Curvature Data";
                case WorkshopCategory.Angle: return "Angle Data";
                case WorkshopCategory.Shape: return "Draw Data";
                case WorkshopCategory.OutlineMetadata: return "Outline Metadata";
                case WorkshopCategory.Efa: return "EFA Data";
                default: return "2D Outlines";
            }
        }

        public static string FileNameFor(WorkshopCategory category)
        {
            switch (category)
            {
                case WorkshopCategory.Curvature: return "curvature_data.csv";
                case WorkshopCategory.Angle: return "angle_data.csv";
                case WorkshopCategory.Shape: return "draw_data.csv";
                case WorkshopCategory.OutlineMetadata: return "outline_metadata.csv";
                case WorkshopCategory.Efa: return "efa_data.csv";
                default: return "outlines.csv";
            }
        }

        // =====================
        // Column definitions
        // =====================

        // Every header is lowercase, so nothing downstream has to remember which
        // variable was capitalised. Each is prefixed by the operation kind that
        // produced it, which is what keeps one wide table readable: circ_, para_ and
        // spline_ for curvature; tri_ for triangles; rect_, sqr_, ellipse_ and circ_
        // for the four drawn shapes; line_ for lines; outline_ and efa_ for outlines.
        // Note circ_ means the circular arc in the Curvature table and the drawn
        // circle in the Draw table — separate tables, so the names never meet.
        //
        // Every length and area reaching FmtLength or FmtArea is in image pixels,
        // which is what those two expect.

        /// One category's column groups, in table order. Callers can take a subset to
        /// table a single operation kind on its own.
        public static List<WorkshopColumnGroup> ColumnGroups(
            WorkshopCategory category, UndoRedoManager undoRedo, ScaleSource scales)
        {
            // The column definitions below read the source as they are filled, so it
            // stands in for a caller that had none. Every specimen then reads as
            // unscaled, which is what a table with no calibration behind it should say.
            scales = scales ?? new ScaleSource(null, null);

            switch (category)
            {
                case WorkshopCategory.Curvature:
                    return new List<WorkshopColumnGroup>
                    {
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(CircularArcOperation),
                            Name = "Circular Arc",
                            Columns = new List<WorkshopColumn>
                            {
                                Col("circ_centangle", o => GeomOpHistoryWindow.Fmt(((CircularArcOperation)o).CentralAngle)),
                                Col("circ_chordarc", o => GeomOpHistoryWindow.Fmt(((CircularArcOperation)o).ChordArcRatio)),
                                Col("circ_risespan", o => GeomOpHistoryWindow.Fmt(((CircularArcOperation)o).AspectRatio)),
                                Col("circ_radius", o => GeomOpHistoryWindow.FmtLength(((CircularArcOperation)o).RadiusImagePixels, scales.Current))
                            }
                        },
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(ParabolaOperation),
                            Name = "Parabolic Arc",
                            Columns = new List<WorkshopColumn>
                            {
                                Col("para_chordarc", o => GeomOpHistoryWindow.Fmt(((ParabolaOperation)o).PChordArcRatio)),
                                Col("para_risespan", o => GeomOpHistoryWindow.Fmt(((ParabolaOperation)o).RiseSpanRatio)),
                                Col("para_vertcurv", o => GeomOpHistoryWindow.Fmt(((ParabolaOperation)o).VertexCurvature)),
                                Col("para_radius", o => GeomOpHistoryWindow.FmtLength(((ParabolaOperation)o).VertexRadiusImagePixels, scales.Current))
                            }
                        },
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(SplineOperation),
                            Name = "n-Point Spline",
                            Columns = new List<WorkshopColumn>
                            {
                                Col("spline_turnangle", o => GeomOpHistoryWindow.Fmt(((SplineOperation)o).TurningAngleArcRatio)),
                                Col("spline_tortuosity", o => GeomOpHistoryWindow.Fmt(((SplineOperation)o).SChordArcRatio)),
                                Col("spline_length", o => GeomOpHistoryWindow.FmtLength(((SplineOperation)o).SplineLengthImagePixels, scales.Current))
                            }
                        }
                    };

                case WorkshopCategory.Angle:
                    return new List<WorkshopColumnGroup>
                    {
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(GetAngleOperation),
                            Name = "Triangle",
                            Columns = new List<WorkshopColumn>
                            {
                                Col("angle_a", o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).AngleA)),
                                Col("angle_b", o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).AngleB)),
                                Col("angle_c", o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).AngleC)),
                                Col("tri_aspect", o => GeomOpHistoryWindow.Fmt(((GetAngleOperation)o).TriAspectRatio)),
                                Col("tri_area", o => GeomOpHistoryWindow.FmtArea(((GetAngleOperation)o).TriAreaImagePixels, scales.Current))
                            }
                        },

                        // A group of its own, so attempt n means the nth angle
                        // measured rather than the nth thing drawn in this mode.
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(AxisAngleOperation),
                            Name = "Axis Angle",
                            Columns = new List<WorkshopColumn>
                            {
                                Col("axis_angle", o =>
                                    GeomOpHistoryWindow.Fmt(((AxisAngleOperation)o).AxisAngleDegrees)),
                                Col("spec_aligned", o =>
                                    ((AxisAngleOperation)o).MeasuredAgainstAxis ? "yes" : "no")
                            }
                        }
                    };

                case WorkshopCategory.Shape:
                    // One group per shape kind. Because the builder walks each group's
                    // operations independently, attempt n means the nth rectangle, the
                    // nth ellipse, and so on, rather than the nth shape of any kind.
                    return new List<WorkshopColumnGroup>
                    {
                        ShapeGroup(ShapeConstraint.Rectangle, "rect", hasAspect: true, scales: scales),
                        ShapeGroup(ShapeConstraint.Square, "sqr", hasAspect: false, scales: scales),
                        ShapeGroup(ShapeConstraint.Ellipse, "ellipse", hasAspect: true, scales: scales),
                        ShapeGroup(ShapeConstraint.Circle, "circ", hasAspect: false, scales: scales),

                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(LineOperation),
                            Name = "Line",
                            Columns = new List<WorkshopColumn>
                            {
                                Col("line_length", o => GeomOpHistoryWindow.FmtLength(((LineOperation)o).LineLengthImagePixels, scales.Current)),
                                Col("line_xdist",  o => GeomOpHistoryWindow.FmtLength(((LineOperation)o).LineDeltaXImagePixels, scales.Current)),
                                Col("line_ydist",  o => GeomOpHistoryWindow.FmtLength(((LineOperation)o).LineDeltaYImagePixels, scales.Current)),
                                Col("line_ratio",  o => GeomOpHistoryWindow.FmtRatio(((LineOperation)o).LineLengthRatio)),
                                Col("line_angle",  o => GeomOpHistoryWindow.FmtRatio(((LineOperation)o).LineAngle)),

                                // X and Y distance are taken on the specimen's axes
                                // when it has them and on the image's when it does
                                // not, so a table of lines has to say which it was.
                                Col("spec_aligned", o => ((LineOperation)o).MeasuredAgainstAxis ? "yes" : "no")
                            }
                        }
                    };

                case WorkshopCategory.OutlineMetadata:
                    return new List<WorkshopColumnGroup>
                    {
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(OutlineOperation),
                            Name = "Outline",
                            Filter = o => ((OutlineOperation)o).HasMetadata,
                            Columns = new List<WorkshopColumn>
                            {
                                Col("outline_aspect", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).AspectRatio)),
                                Col("outline_perim", o => GeomOpHistoryWindow.FmtLength(((OutlineOperation)o).PerimeterImagePixels, scales.Current)),
                                Col("outline_area", o => GeomOpHistoryWindow.FmtArea(((OutlineOperation)o).AreaImagePixels, scales.Current)),
                                Col("outline_maxlength", o => GeomOpHistoryWindow.FmtLength(((OutlineOperation)o).MaxLengthImagePixels, scales.Current)),
                                Col("outline_maxwidth", o => GeomOpHistoryWindow.FmtLength(((OutlineOperation)o).MaxWidthImagePixels, scales.Current)),
                                Col("outline_circ", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).Circularity)),
                                Col("outline_convexity", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).Convexity)),
                                Col("outline_solidity", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).Solidity)),
                                Col("outline_sumturn", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).SumTurningAngles)),
                                Col("outline_turnlength", o => GeomOpHistoryWindow.Fmt4(((OutlineOperation)o).TurningAngleLength)),
                                Col("outline_spacing", o => GeomOpHistoryWindow.FmtLength(((OutlineOperation)o).MeasurementSpacingImagePixels, scales.Current)),
                                Col("outline_points", o => ((OutlineOperation)o).MeasurementPointCount.ToString())
                            }
                        }
                    };

                case WorkshopCategory.Efa:
                    return new List<WorkshopColumnGroup> { EfaGroup(undoRedo) };

                default:
                    return new List<WorkshopColumnGroup>
                    {
                        new WorkshopColumnGroup
                        {
                            OperationType = typeof(OutlineOperation),
                            Name = "Outline",
                            Columns = new List<WorkshopColumn>
                            {
                                Col("outline_vertices", o => VertexCount((OutlineOperation)o).ToString()),
                                Col("outline_hasmeta", o => ((OutlineOperation)o).HasMetadata ? "yes" : "no"),
                                Col("outline_perim", o => ((OutlineOperation)o).HasMetadata
                                    ? GeomOpHistoryWindow.FmtLength(((OutlineOperation)o).PerimeterImagePixels, scales.Current) : ""),
                                Col("outline_area", o => ((OutlineOperation)o).HasMetadata
                                    ? GeomOpHistoryWindow.FmtArea(((OutlineOperation)o).AreaImagePixels, scales.Current) : "")
                            }
                        }
                    };
            }
        }

        /// One drawn shape kind as its own set of columns.
        /// A square and a circle are equilateral by construction, so their aspect
        /// ratio is always 1 and no column is emitted for it.
        private static WorkshopColumnGroup ShapeGroup(
            ShapeConstraint kind, string prefix, bool hasAspect, ScaleSource scales)
        {
            var group = new WorkshopColumnGroup
            {
                OperationType = typeof(ShapeOperation),

                // The kind, not the type: a rectangle and a circle are both a
                // ShapeOperation and only the filter tells them apart.
                Name = kind.ToString(),
                Filter = o => ((ShapeOperation)o).ShapeKind == kind
            };

            if (hasAspect)
                group.Columns.Add(Col(prefix + "_aspect",
                    o => GeomOpHistoryWindow.Fmt(((ShapeOperation)o).DrawAspectRatio)));

            group.Columns.Add(Col(prefix + "_area",
                o => GeomOpHistoryWindow.FmtArea(((ShapeOperation)o).ShapeAreaImagePixels, scales.Current)));

            return group;
        }

        /// EFA columns are one set of four per harmonic, so their count depends on the
        /// deepest analysis in the session.
        private static WorkshopColumnGroup EfaGroup(UndoRedoManager undoRedo)
        {
            int maxHarmonics = 0;

            if (undoRedo != null)
            {
                foreach (var record in undoRedo.Archive)
                    maxHarmonics = Math.Max(maxHarmonics, MaxHarmonicsIn(record.Operations));
                maxHarmonics = Math.Max(maxHarmonics, MaxHarmonicsIn(undoRedo.History));
            }

            var group = new WorkshopColumnGroup
            {
                OperationType = typeof(OutlineOperation),
                Name = "EFA",
                Filter = o => HarmonicsOf((OutlineOperation)o) > 0
            };

            group.Columns.Add(Col("efa_harmonics", o => HarmonicsOf((OutlineOperation)o).ToString()));

            for (int h = 1; h <= maxHarmonics; h++)
            {
                int harmonic = h;   // captured per iteration
                group.Columns.Add(Col($"efa_a{harmonic}", o => Coefficient(o, harmonic, 0)));
                group.Columns.Add(Col($"efa_b{harmonic}", o => Coefficient(o, harmonic, 1)));
                group.Columns.Add(Col($"efa_c{harmonic}", o => Coefficient(o, harmonic, 2)));
                group.Columns.Add(Col($"efa_d{harmonic}", o => Coefficient(o, harmonic, 3)));
            }

            return group;
        }

        private static int MaxHarmonicsIn(IEnumerable<WorkOperation> ops)
        {
            int max = 0;
            foreach (var op in ops.OfType<OutlineOperation>())
                max = Math.Max(max, HarmonicsOf(op));
            return max;
        }

        private static int HarmonicsOf(OutlineOperation op) =>
            op.EFDCoefficients == null ? 0 : op.EFDCoefficients.Length / 4;

        // An outline with fewer harmonics than the widest one leaves the extra
        // coefficient cells blank.
        private static string Coefficient(WorkOperation op, int harmonic, int component)
        {
            var outline = (OutlineOperation)op;
            int index = (harmonic - 1) * 4 + component;

            if (outline.EFDCoefficients == null || index >= outline.EFDCoefficients.Length)
                return "";

            return GeomOpHistoryWindow.Fmt4(outline.EFDCoefficients[index]);
        }

        private static int VertexCount(OutlineOperation op)
        {
            var polyline = op.Elements?.OfType<System.Windows.Shapes.Polyline>().FirstOrDefault();
            return polyline == null ? 0 : polyline.Points.Count;
        }

        private static WorkshopColumn Col(string header, Func<WorkOperation, string> value) =>
            new WorkshopColumn { Header = header, Value = value };
    }
}