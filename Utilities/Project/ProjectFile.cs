using DinoLino.Utilities.Modes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Xml.Linq;

namespace DinoLino.Utilities
{
    /// <summary>One specimen as a project file holds it.</summary>
    public sealed class ProjectSpecimen
    {
        public int Ordinal;
        public string Name;
        public string FileName;

        /// <summary>Where the picture came from, as saved and as resolved on load.</summary>
        public string SourcePath;

        public bool ImageEdited;

        /// <summary>The picture itself, when the file alone cannot reproduce it.</summary>
        public BitmapSource Image;

        /// <summary>True when the picture was stored in the project rather than read back.</summary>
        public bool PictureStored;

        /// <summary>Set when the saved picture could not be found at either path.</summary>
        public bool PictureMissing;

        public string ModelPath;
        public Quaternion ModelOrientation = Quaternion.Identity;

        public ScaleState Calibration = ScaleState.None;
        public AlignmentState Alignment = AlignmentState.None;
        public CorrectionState Corrections = CorrectionState.None;

        public List<WorkOperation> Operations = new List<WorkOperation>();

        /// <summary>Group column name to the group this specimen belongs to.</summary>
        public Dictionary<string, string> Groups = new Dictionary<string, string>();
    }

    /// <summary>One user-written column, as the project file holds it.</summary>
    public sealed class ProjectFormula
    {
        public string TableKey;
        public string Name;
        public string Text;
    }

    /// <summary>A variable chosen for the custom table.</summary>
    public sealed class ProjectCustomVariable
    {
        public string Category;
        public string Header;
    }

    /// <summary>A column hidden from one table.</summary>
    public sealed class ProjectHiddenColumn
    {
        public string TableKey;
        public string Name;
    }

    /// <summary>One outline the user kept as a silhouette.</summary>
    public sealed class ProjectSilhouette
    {
        public string SpecimenName;
        public string Name;
        public List<Point> Points = new List<Point>();
    }

    /// <summary>A whole saved session, ready to be written out or rebuilt.</summary>
    public sealed class ProjectData
    {
        public List<ProjectSpecimen> Specimens = new List<ProjectSpecimen>();

        /// <summary>Ordinal of the specimen that was loaded when the project was saved.</summary>
        public int CurrentOrdinal;

        /// <summary>Group column names, in the order they were created.</summary>
        public List<string> GroupColumns = new List<string>();

        public List<ProjectFormula> Formulas = new List<ProjectFormula>();
        public List<ProjectCustomVariable> CustomVariables = new List<ProjectCustomVariable>();
        public List<ProjectHiddenColumn> HiddenColumns = new List<ProjectHiddenColumn>();
        public List<string> WorkbookSheets = new List<string>();
        public List<ProjectSilhouette> Silhouettes = new List<ProjectSilhouette>();

        /// <summary>Pictures the project named but could not find when it was opened.</summary>
        public List<string> MissingPictures = new List<string>();
    }

    /// <summary>
    /// Reads and writes a DinoLino project: every specimen with its scale, alignment
    /// and corrections, every measurement with the geometry behind it, and the table
    /// state that goes with them.
    ///
    /// The file is a zip holding one XML document and, where needed, the pictures
    /// themselves. A picture is stored in the file only when the photograph on disk
    /// cannot reproduce it — a view captured from a 3D model, or an image that has
    /// been rotated or flipped since it was opened. Everything else is referenced by
    /// path, relative to the project first so a project and its photographs can be
    /// moved together.
    /// </summary>
    public static class ProjectFile
    {
        /// <summary>Extension and filter used by the open and save dialogs.</summary>
        public const string Extension = ".dlino";

        public const string Filter = "DinoLino project (*.dlino)|*.dlino|All files (*.*)|*.*";

        /// <summary>Format version written into every file.</summary>
        private const int Version = 1;

        private static readonly Uri ProjectPart =
            PackUriHelper.CreatePartUri(new Uri("project.xml", UriKind.Relative));

        /// <summary>Where one specimen's stored picture sits inside the file.</summary>
        private static string PictureName(int ordinal) =>
            "pictures/spec-" + ordinal.ToString(CultureInfo.InvariantCulture) + ".png";

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // =====================
        // Saving
        // =====================

        /// <summary>Writes a session to a project file, replacing anything already there.</summary>
        public static void Save(string path, ProjectData data)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("No file name.", nameof(path));
            if (data == null) throw new ArgumentNullException(nameof(data));

            string folder = Path.GetDirectoryName(Path.GetFullPath(path));

            // Written beside the target and moved into place, so a failure halfway
            // through leaves the previous project file untouched.
            string temporary = path + ".saving";

            try
            {
                using (var package = Package.Open(temporary, FileMode.Create, FileAccess.ReadWrite))
                {
                    var document = new XDocument(BuildProjectElement(data, folder));

                    var part = package.CreatePart(ProjectPart, "application/xml", CompressionOption.Normal);

                    using (var stream = part.GetStream(FileMode.Create, FileAccess.Write))
                        document.Save(stream);

                    foreach (var specimen in data.Specimens)
                    {
                        if (!NeedsStoredPicture(specimen)) continue;

                        WritePicture(package, specimen);
                    }
                }

                // Replace swaps the two in one step, so there is no moment where the
                // project file is missing. Both names sit in the same folder, which
                // is what lets it do that.
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // Nothing more to do about a half-written file that will not go.
                }

                throw;
            }
        }

        /// A picture belongs in the file when no photograph on disk reproduces it: a
        /// captured 3D view, or an image edited since it was opened.
        private static bool NeedsStoredPicture(ProjectSpecimen specimen) =>
            specimen.Image != null
            && (string.IsNullOrEmpty(specimen.SourcePath) || specimen.ImageEdited);

        private static void WritePicture(Package package, ProjectSpecimen specimen)
        {
            var uri = PackUriHelper.CreatePartUri(
                new Uri(PictureName(specimen.Ordinal), UriKind.Relative));

            // Already compressed, so the package stores it as it stands.
            var part = package.CreatePart(uri, "image/png", CompressionOption.NotCompressed);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(specimen.Image));

            using (var stream = part.GetStream(FileMode.Create, FileAccess.Write))
                encoder.Save(stream);

            specimen.PictureStored = true;
        }

        private static XElement BuildProjectElement(ProjectData data, string projectFolder)
        {
            var root = new XElement("dinolino-project",
                new XAttribute("version", Version),
                new XAttribute("saved", DateTime.Now.ToString("s", Invariant)));

            var session = new XElement("session", new XAttribute("current", data.CurrentOrdinal));

            foreach (var specimen in data.Specimens)
                session.Add(BuildSpecimenElement(specimen, projectFolder));

            root.Add(session);
            root.Add(BuildGroupsElement(data));

            var formulas = new XElement("formulas");
            foreach (var formula in data.Formulas)
            {
                formulas.Add(new XElement("column",
                    new XAttribute("table", formula.TableKey ?? ""),
                    new XAttribute("name", formula.Name ?? ""),
                    new XAttribute("text", formula.Text ?? "")));
            }
            root.Add(formulas);

            var custom = new XElement("custom");
            foreach (var variable in data.CustomVariables)
            {
                custom.Add(new XElement("variable",
                    new XAttribute("category", variable.Category ?? ""),
                    new XAttribute("header", variable.Header ?? "")));
            }
            root.Add(custom);

            var hidden = new XElement("hidden");
            foreach (var column in data.HiddenColumns)
            {
                hidden.Add(new XElement("column",
                    new XAttribute("table", column.TableKey ?? ""),
                    new XAttribute("name", column.Name ?? "")));
            }
            root.Add(hidden);

            var workbook = new XElement("workbook");
            foreach (var sheet in data.WorkbookSheets)
                workbook.Add(new XElement("sheet", new XAttribute("name", sheet ?? "")));
            root.Add(workbook);

            var silhouettes = new XElement("silhouettes");
            foreach (var outline in data.Silhouettes)
            {
                silhouettes.Add(new XElement("outline",
                    new XAttribute("specimen", outline.SpecimenName ?? ""),
                    new XAttribute("name", outline.Name ?? ""),
                    new XAttribute("points", Text(outline.Points))));
            }
            root.Add(silhouettes);

            return root;
        }

        private static XElement BuildGroupsElement(ProjectData data)
        {
            var groups = new XElement("groups");

            foreach (var name in data.GroupColumns)
            {
                var column = new XElement("column", new XAttribute("name", name ?? ""));

                foreach (var specimen in data.Specimens)
                {
                    string value;
                    if (!specimen.Groups.TryGetValue(name, out value)) continue;
                    if (string.IsNullOrEmpty(value)) continue;

                    column.Add(new XElement("assignment",
                        new XAttribute("specimen", specimen.Ordinal),
                        new XAttribute("group", value)));
                }

                groups.Add(column);
            }

            return groups;
        }

        private static XElement BuildSpecimenElement(ProjectSpecimen specimen, string projectFolder)
        {
            var element = new XElement("specimen",
                new XAttribute("ordinal", specimen.Ordinal),
                new XAttribute("name", specimen.Name ?? ""),
                new XAttribute("file", specimen.FileName ?? ""),
                new XAttribute("edited", specimen.ImageEdited));

            if (!string.IsNullOrEmpty(specimen.SourcePath))
            {
                element.Add(new XAttribute("path", specimen.SourcePath));

                string relative = RelativePath(projectFolder, specimen.SourcePath);
                if (!string.IsNullOrEmpty(relative)) element.Add(new XAttribute("relative", relative));
            }

            if (NeedsStoredPicture(specimen))
            {
                element.Add(new XAttribute("picture", PictureName(specimen.Ordinal)));
            }

            if (!string.IsNullOrEmpty(specimen.ModelPath))
            {
                element.Add(new XAttribute("model", specimen.ModelPath));
                element.Add(new XAttribute("orientation", Text(specimen.ModelOrientation)));
            }

            if (specimen.Calibration.IsSet)
            {
                element.Add(new XElement("scale",
                    new XAttribute("unitsPerImagePixel", Text(specimen.Calibration.UnitsPerImagePixel)),
                    new XAttribute("unit", specimen.Calibration.Unit ?? "")));
            }

            if (specimen.Alignment.IsSet)
            {
                element.Add(new XElement("alignment",
                    new XAttribute("radians", Text(specimen.Alignment.RotationRadians)),
                    new XAttribute("axis", specimen.Alignment.DrawnAxis.ToString())));
            }

            if (specimen.Corrections.IsSet)
            {
                element.Add(new XElement("corrections",
                    new XAttribute("contrast", Text(specimen.Corrections.Contrast)),
                    new XAttribute("brightness", Text(specimen.Corrections.Brightness)),
                    new XAttribute("saturation", Text(specimen.Corrections.Saturation))));
            }

            var operations = new XElement("operations");
            foreach (var operation in specimen.Operations)
            {
                var written = BuildOperationElement(operation);
                if (written != null) operations.Add(written);
            }
            element.Add(operations);

            return element;
        }

        private static XElement BuildOperationElement(WorkOperation operation)
        {
            if (operation == null) return null;

            var element = new XElement("operation",
                new XAttribute("kind", operation.OperationKind ?? ""));

            element.Add(new XAttribute("points", Text(operation.ImagePoints)));

            var arc = operation as CircularArcOperation;
            if (arc != null)
            {
                element.Add(new XAttribute("type", "CircularArc"));
                element.Add(new XAttribute("centralAngle", Text(arc.CentralAngle)));
                element.Add(new XAttribute("aspectRatio", Text(arc.AspectRatio)));
                element.Add(new XAttribute("chordArcRatio", Text(arc.ChordArcRatio)));
                element.Add(new XAttribute("radius", Text(arc.RadiusImagePixels)));
                return element;
            }

            var parabola = operation as ParabolaOperation;
            if (parabola != null)
            {
                element.Add(new XAttribute("type", "Parabola"));
                element.Add(new XAttribute("function", parabola.XYFunction ?? ""));
                element.Add(new XAttribute("riseSpan", Text(parabola.RiseSpanRatio)));
                element.Add(new XAttribute("chordArcRatio", Text(parabola.PChordArcRatio)));
                element.Add(new XAttribute("vertexCurvature", Text(parabola.VertexCurvature)));
                element.Add(new XAttribute("vertexRadius", Text(parabola.VertexRadiusImagePixels)));
                return element;
            }

            var spline = operation as SplineOperation;
            if (spline != null)
            {
                element.Add(new XAttribute("type", "Spline"));
                element.Add(new XAttribute("turningAngle", Text(spline.TurningAngleArcRatio)));
                element.Add(new XAttribute("tortuosity", Text(spline.SChordArcRatio)));
                element.Add(new XAttribute("length", Text(spline.SplineLengthImagePixels)));
                element.Add(new XAttribute("controlPoints", Text(spline.ControlImagePoints)));
                return element;
            }

            var triangle = operation as GetAngleOperation;
            if (triangle != null)
            {
                element.Add(new XAttribute("type", "Triangle"));
                element.Add(new XAttribute("angleA", Text(triangle.AngleA)));
                element.Add(new XAttribute("angleB", Text(triangle.AngleB)));
                element.Add(new XAttribute("angleC", Text(triangle.AngleC)));
                element.Add(new XAttribute("aspectRatio", Text(triangle.TriAspectRatio)));
                element.Add(new XAttribute("area", Text(triangle.TriAreaImagePixels)));
                element.Add(new XAttribute("relativeArea", Text(triangle.RelativeArea)));
                return element;
            }

            var shape = operation as ShapeOperation;
            if (shape != null)
            {
                element.Add(new XAttribute("type", "Shape"));
                element.Add(new XAttribute("shapeKind", shape.ShapeKind.ToString()));
                element.Add(new XAttribute("aspectRatio", Text(shape.DrawAspectRatio)));
                element.Add(new XAttribute("area", Text(shape.ShapeAreaImagePixels)));
                element.Add(new XAttribute("relativeArea", Text(shape.RelativeArea)));
                return element;
            }

            var line = operation as LineOperation;
            if (line != null)
            {
                element.Add(new XAttribute("type", "Line"));
                element.Add(new XAttribute("length", Text(line.LineLengthImagePixels)));
                element.Add(new XAttribute("deltaX", Text(line.LineDeltaXImagePixels)));
                element.Add(new XAttribute("deltaY", Text(line.LineDeltaYImagePixels)));
                element.Add(new XAttribute("heading", Text(line.HeadingDegrees)));
                element.Add(new XAttribute("lengthRatio", Text(line.LineLengthRatio)));
                element.Add(new XAttribute("angle", Text(line.LineAngle)));
                return element;
            }

            var outline = operation as OutlineOperation;
            if (outline != null)
            {
                element.Add(new XAttribute("type", "Outline"));
                element.Add(new XAttribute("hasMetadata", outline.HasMetadata));
                element.Add(new XAttribute("aspectRatio", Text(outline.AspectRatio)));
                element.Add(new XAttribute("circularity", Text(outline.Circularity)));
                element.Add(new XAttribute("solidity", Text(outline.Solidity)));
                element.Add(new XAttribute("convexity", Text(outline.Convexity)));
                element.Add(new XAttribute("sumTurning", Text(outline.SumTurningAngles)));
                element.Add(new XAttribute("turningLength", Text(outline.TurningAngleLength)));
                element.Add(new XAttribute("perimeter", Text(outline.PerimeterImagePixels)));
                element.Add(new XAttribute("area", Text(outline.AreaImagePixels)));
                element.Add(new XAttribute("maxLength", Text(outline.MaxLengthImagePixels)));
                element.Add(new XAttribute("maxWidth", Text(outline.MaxWidthImagePixels)));
                element.Add(new XAttribute("spacing", Text(outline.MeasurementSpacingImagePixels)));
                element.Add(new XAttribute("pointCount", outline.MeasurementPointCount));
                element.Add(new XAttribute("summary", outline.MetadataSummary ?? ""));
                element.Add(new XAttribute("warning", outline.NormalizationWarning ?? ""));

                if (outline.EFDCoefficients != null && outline.EFDCoefficients.Length > 0)
                    element.Add(new XElement("efd", Text(outline.EFDCoefficients)));

                return element;
            }

            return null;
        }

        // =====================
        // Loading
        // =====================

        /// <summary>
        /// Reads a project file. Pictures are resolved as far as possible; any that
        /// cannot be found are named in <see cref="ProjectData.MissingPictures"/> and
        /// their specimens come back without an image but with their measurements.
        /// </summary>
        public static ProjectData Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("No file name.", nameof(path));

            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            var data = new ProjectData();

            using (var package = Package.Open(path, FileMode.Open, FileAccess.Read))
            {
                if (!package.PartExists(ProjectPart))
                    throw new InvalidDataException("This file is not a DinoLino project.");

                var part = package.GetPart(ProjectPart);

                XDocument document;
                using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
                    document = XDocument.Load(stream);

                var root = document.Root;
                if (root == null || root.Name != "dinolino-project")
                    throw new InvalidDataException("This file is not a DinoLino project.");

                int version = (int)Number(root.Attribute("version"), 0);
                if (version > Version)
                {
                    throw new InvalidDataException(
                        "This project was saved by a newer version of DinoLino.");
                }

                var session = root.Element("session");
                if (session != null)
                {
                    data.CurrentOrdinal = (int)Number(session.Attribute("current"), 0);

                    foreach (var element in session.Elements("specimen"))
                        data.Specimens.Add(ReadSpecimen(element, package, folder, data));
                }

                ReadGroups(root.Element("groups"), data);

                var formulas = root.Element("formulas");
                if (formulas != null)
                {
                    foreach (var element in formulas.Elements("column"))
                    {
                        data.Formulas.Add(new ProjectFormula
                        {
                            TableKey = Value(element.Attribute("table")),
                            Name = Value(element.Attribute("name")),
                            Text = Value(element.Attribute("text"))
                        });
                    }
                }

                var custom = root.Element("custom");
                if (custom != null)
                {
                    foreach (var element in custom.Elements("variable"))
                    {
                        data.CustomVariables.Add(new ProjectCustomVariable
                        {
                            Category = Value(element.Attribute("category")),
                            Header = Value(element.Attribute("header"))
                        });
                    }
                }

                var hidden = root.Element("hidden");
                if (hidden != null)
                {
                    foreach (var element in hidden.Elements("column"))
                    {
                        data.HiddenColumns.Add(new ProjectHiddenColumn
                        {
                            TableKey = Value(element.Attribute("table")),
                            Name = Value(element.Attribute("name"))
                        });
                    }
                }

                var silhouettes = root.Element("silhouettes");
                if (silhouettes != null)
                {
                    foreach (var element in silhouettes.Elements("outline"))
                    {
                        data.Silhouettes.Add(new ProjectSilhouette
                        {
                            SpecimenName = Value(element.Attribute("specimen")),
                            Name = Value(element.Attribute("name")),
                            Points = ReadPoints(Value(element.Attribute("points")))
                        });
                    }
                }

                var workbook = root.Element("workbook");
                if (workbook != null)
                {
                    foreach (var element in workbook.Elements("sheet"))
                    {
                        string name = Value(element.Attribute("name"));
                        if (!string.IsNullOrEmpty(name)) data.WorkbookSheets.Add(name);
                    }
                }
            }

            return data;
        }

        private static void ReadGroups(XElement groups, ProjectData data)
        {
            if (groups == null) return;

            foreach (var column in groups.Elements("column"))
            {
                string name = Value(column.Attribute("name"));
                if (string.IsNullOrEmpty(name)) continue;

                data.GroupColumns.Add(name);

                foreach (var assignment in column.Elements("assignment"))
                {
                    int ordinal = (int)Number(assignment.Attribute("specimen"), -1);
                    string group = Value(assignment.Attribute("group"));
                    if (string.IsNullOrEmpty(group)) continue;

                    var specimen = data.Specimens.FirstOrDefault(s => s.Ordinal == ordinal);
                    if (specimen != null) specimen.Groups[name] = group;
                }
            }
        }

        private static ProjectSpecimen ReadSpecimen(
            XElement element, Package package, string projectFolder, ProjectData data)
        {
            var specimen = new ProjectSpecimen
            {
                Ordinal = (int)Number(element.Attribute("ordinal"), 0),
                Name = Value(element.Attribute("name")),
                FileName = Value(element.Attribute("file")),
                ImageEdited = Flag(element.Attribute("edited")),
                ModelPath = Value(element.Attribute("model"))
            };

            if (string.IsNullOrEmpty(specimen.Name)) specimen.Name = null;

            // An empty path is no path: a specimen with one reads as a 3D model that
            // still needs positioning, which a photograph never is.
            if (string.IsNullOrEmpty(specimen.ModelPath)) specimen.ModelPath = null;

            var orientation = element.Attribute("orientation");
            if (orientation != null) specimen.ModelOrientation = ReadQuaternion(orientation.Value);

            specimen.SourcePath = ResolvePicturePath(element, projectFolder);
            specimen.Image = ReadPicture(element, package, specimen, data);

            var scale = element.Element("scale");
            if (scale != null)
            {
                specimen.Calibration = ScaleState.FromUnitsPerImagePixel(
                    Number(scale.Attribute("unitsPerImagePixel"), 0),
                    Value(scale.Attribute("unit")));
            }

            var alignment = element.Element("alignment");
            if (alignment != null)
            {
                AlignmentAxis axis;
                if (!Enum.TryParse(Value(alignment.Attribute("axis")), out axis)) axis = AlignmentAxis.X;

                specimen.Alignment = AlignmentState.FromRadians(
                    Number(alignment.Attribute("radians"), 0), axis);
            }

            var corrections = element.Element("corrections");
            if (corrections != null)
            {
                specimen.Corrections = new CorrectionState(
                    Number(corrections.Attribute("contrast"), 0),
                    Number(corrections.Attribute("brightness"), 0),
                    Number(corrections.Attribute("saturation"), 0));
            }

            var operations = element.Element("operations");
            if (operations != null)
            {
                foreach (var child in operations.Elements("operation"))
                {
                    var operation = ReadOperation(child);
                    if (operation != null) specimen.Operations.Add(operation);
                }
            }

            return specimen;
        }

        /// The photograph's path, taken relative to the project first so a project and
        /// its pictures can be moved together, and falling back to where they were.
        private static string ResolvePicturePath(XElement element, string projectFolder)
        {
            string relative = Value(element.Attribute("relative"));
            string absolute = Value(element.Attribute("path"));

            if (!string.IsNullOrEmpty(relative) && !string.IsNullOrEmpty(projectFolder))
            {
                try
                {
                    string combined = Path.GetFullPath(Path.Combine(projectFolder, relative));
                    if (File.Exists(combined)) return combined;
                }
                catch
                {
                    // An unusable path is simply not the answer; the absolute one may be.
                }
            }

            return absolute;
        }

        private static BitmapSource ReadPicture(
            XElement element, Package package, ProjectSpecimen specimen, ProjectData data)
        {
            string stored = Value(element.Attribute("picture"));

            if (!string.IsNullOrEmpty(stored))
            {
                var image = ReadStoredPicture(package, stored);
                if (image != null)
                {
                    specimen.PictureStored = true;
                    return image;
                }
            }

            if (!string.IsNullOrEmpty(specimen.SourcePath) && File.Exists(specimen.SourcePath))
            {
                var image = ReadPictureFile(specimen.SourcePath);
                if (image != null) return image;
            }

            // A specimen registered from a 3D model has no picture until it is
            // positioned, and that is not something to report as lost.
            bool expectsPicture = !string.IsNullOrEmpty(stored) || !string.IsNullOrEmpty(specimen.SourcePath);

            if (expectsPicture)
            {
                specimen.PictureMissing = true;

                data.MissingPictures.Add(string.IsNullOrEmpty(specimen.SourcePath)
                    ? (specimen.FileName ?? "")
                    : specimen.SourcePath);
            }

            return null;
        }

        private static BitmapSource ReadStoredPicture(Package package, string partName)
        {
            try
            {
                var uri = PackUriHelper.CreatePartUri(new Uri(partName, UriKind.Relative));
                if (!package.PartExists(uri)) return null;

                var part = package.GetPart(uri);

                using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
                {
                    var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    memory.Position = 0;

                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = memory;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch
            {
                return null;
            }
        }

        private static BitmapSource ReadPictureFile(string path)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private static WorkOperation ReadOperation(XElement element)
        {
            string type = Value(element.Attribute("type"));
            WorkOperation operation;

            switch (type)
            {
                case "CircularArc":
                    operation = new CircularArcOperation
                    {
                        CentralAngle = Number(element.Attribute("centralAngle"), 0),
                        AspectRatio = Number(element.Attribute("aspectRatio"), 0),
                        ChordArcRatio = Number(element.Attribute("chordArcRatio"), 0),
                        RadiusImagePixels = Number(element.Attribute("radius"), 0)
                    };
                    break;

                case "Parabola":
                    operation = new ParabolaOperation
                    {
                        XYFunction = Value(element.Attribute("function")),
                        RiseSpanRatio = Number(element.Attribute("riseSpan"), 0),
                        PChordArcRatio = Number(element.Attribute("chordArcRatio"), 0),
                        VertexCurvature = Number(element.Attribute("vertexCurvature"), 0),
                        VertexRadiusImagePixels = Number(element.Attribute("vertexRadius"), 0)
                    };
                    break;

                case "Spline":
                    operation = new SplineOperation
                    {
                        TurningAngleArcRatio = Number(element.Attribute("turningAngle"), 0),
                        SChordArcRatio = Number(element.Attribute("tortuosity"), 0),
                        SplineLengthImagePixels = Number(element.Attribute("length"), 0),
                        ControlImagePoints = ReadPoints(Value(element.Attribute("controlPoints")))
                    };
                    break;

                case "Triangle":
                    operation = new GetAngleOperation
                    {
                        AngleA = Number(element.Attribute("angleA"), 0),
                        AngleB = Number(element.Attribute("angleB"), 0),
                        AngleC = Number(element.Attribute("angleC"), 0),
                        TriAspectRatio = Number(element.Attribute("aspectRatio"), 0),
                        TriAreaImagePixels = Number(element.Attribute("area"), 0),
                        RelativeArea = ReadNumberOrText(element.Attribute("relativeArea"))
                    };
                    break;

                case "Shape":
                    {
                        DrawMode.ShapeConstraint kind;
                        if (!Enum.TryParse(Value(element.Attribute("shapeKind")), out kind))
                            kind = DrawMode.ShapeConstraint.None;

                        operation = new ShapeOperation
                        {
                            ShapeKind = kind,
                            DrawAspectRatio = Number(element.Attribute("aspectRatio"), 0),
                            ShapeAreaImagePixels = Number(element.Attribute("area"), 0),
                            RelativeArea = ReadNumberOrText(element.Attribute("relativeArea"))
                        };
                        break;
                    }

                case "Line":
                    operation = new LineOperation
                    {
                        LineLengthImagePixels = Number(element.Attribute("length"), 0),
                        LineDeltaXImagePixels = Number(element.Attribute("deltaX"), 0),
                        LineDeltaYImagePixels = Number(element.Attribute("deltaY"), 0),
                        HeadingDegrees = Number(element.Attribute("heading"), 0),
                        LineLengthRatio = ReadNumberOrText(element.Attribute("lengthRatio")),
                        LineAngle = ReadNumberOrText(element.Attribute("angle"))
                    };
                    break;

                case "Outline":
                    {
                        var outline = new OutlineOperation
                        {
                            HasMetadata = Flag(element.Attribute("hasMetadata")),
                            AspectRatio = Number(element.Attribute("aspectRatio"), 0),
                            Circularity = Number(element.Attribute("circularity"), 0),
                            Solidity = Number(element.Attribute("solidity"), 0),
                            Convexity = Number(element.Attribute("convexity"), 0),
                            SumTurningAngles = Number(element.Attribute("sumTurning"), 0),
                            TurningAngleLength = Number(element.Attribute("turningLength"), 0),
                            PerimeterImagePixels = Number(element.Attribute("perimeter"), 0),
                            AreaImagePixels = Number(element.Attribute("area"), 0),
                            MaxLengthImagePixels = Number(element.Attribute("maxLength"), 0),
                            MaxWidthImagePixels = Number(element.Attribute("maxWidth"), 0),
                            MeasurementSpacingImagePixels = Number(element.Attribute("spacing"), 0),
                            MeasurementPointCount = (int)Number(element.Attribute("pointCount"), 0),
                            MetadataSummary = Value(element.Attribute("summary")),
                            NormalizationWarning = Value(element.Attribute("warning"))
                        };

                        var efd = element.Element("efd");
                        if (efd != null) outline.EFDCoefficients = ReadNumbers(efd.Value);

                        operation = outline;
                        break;
                    }

                default:
                    return null;
            }

            operation.OperationKind = Value(element.Attribute("kind"));
            operation.ImagePoints = ReadPoints(Value(element.Attribute("points")));
            return operation;
        }

        // =====================
        // Text conversions
        // =====================

        // Round-trip formatting throughout: a saved measurement reads back as the
        // number that was measured, not as a rounded copy of it.
        private static string Text(double value) => value.ToString("G17", Invariant);

        private static string Text(object value)
        {
            if (value == null) return "";
            if (value is double) return Text((double)value);

            return value.ToString();
        }

        private static string Text(IEnumerable<Point> points)
        {
            if (points == null) return "";

            var builder = new System.Text.StringBuilder();

            foreach (var point in points)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(Text(point.X)).Append(',').Append(Text(point.Y));
            }

            return builder.ToString();
        }

        private static string Text(IEnumerable<double> values)
        {
            if (values == null) return "";
            return string.Join(" ", values.Select(Text));
        }

        private static string Text(Quaternion value) =>
            Text(value.X) + "," + Text(value.Y) + "," + Text(value.Z) + "," + Text(value.W);

        private static string Value(XAttribute attribute) => attribute == null ? "" : attribute.Value;

        private static bool Flag(XAttribute attribute)
        {
            bool value;
            return attribute != null && bool.TryParse(attribute.Value, out value) && value;
        }

        private static double Number(XAttribute attribute, double fallback)
        {
            double value;

            if (attribute != null
                && double.TryParse(attribute.Value, NumberStyles.Float, Invariant, out value))
                return value;

            return fallback;
        }

        /// A value the program keeps as either a number or a short text such as "N/A".
        private static object ReadNumberOrText(XAttribute attribute)
        {
            if (attribute == null || attribute.Value.Length == 0) return null;

            double value;
            if (double.TryParse(attribute.Value, NumberStyles.Float, Invariant, out value)) return value;

            return attribute.Value;
        }

        private static List<Point> ReadPoints(string text)
        {
            var points = new List<Point>();
            if (string.IsNullOrWhiteSpace(text)) return points;

            foreach (var pair in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(',');
                if (parts.Length != 2) continue;

                double x, y;
                if (!double.TryParse(parts[0], NumberStyles.Float, Invariant, out x)) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, Invariant, out y)) continue;

                points.Add(new Point(x, y));
            }

            return points;
        }

        private static double[] ReadNumbers(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var values = new List<double>();

            foreach (var token in text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double value;
                if (double.TryParse(token, NumberStyles.Float, Invariant, out value)) values.Add(value);
            }

            return values.Count == 0 ? null : values.ToArray();
        }

        private static Quaternion ReadQuaternion(string text)
        {
            var parts = (text ?? "").Split(',');
            if (parts.Length != 4) return Quaternion.Identity;

            double x, y, z, w;
            if (!double.TryParse(parts[0], NumberStyles.Float, Invariant, out x)) return Quaternion.Identity;
            if (!double.TryParse(parts[1], NumberStyles.Float, Invariant, out y)) return Quaternion.Identity;
            if (!double.TryParse(parts[2], NumberStyles.Float, Invariant, out z)) return Quaternion.Identity;
            if (!double.TryParse(parts[3], NumberStyles.Float, Invariant, out w)) return Quaternion.Identity;

            return new Quaternion(x, y, z, w);
        }

        /// The path of a file relative to a folder, or an empty string when the two
        /// are on different drives and no relative path exists.
        private static string RelativePath(string folder, string filePath)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(filePath)) return "";

            try
            {
                var from = new Uri(AppendSeparator(Path.GetFullPath(folder)));
                var to = new Uri(Path.GetFullPath(filePath));

                if (from.Scheme != to.Scheme) return "";

                string relative = Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString());
                return relative.Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return "";
            }
        }

        private static string AppendSeparator(string folder) =>
            folder.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? folder
                : folder + Path.DirectorySeparatorChar;
    }
}
