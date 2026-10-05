using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    /// Angle measurement, in two forms. A triangle drawn from three clicks gives its
    /// interior angles, aspect ratio, area, and the area relative to the previous
    /// triangle. A line drawn from two clicks gives its direction against the
    /// specimen's own axis, which is the orientation of a feature rather than a
    /// relationship between two things drawn.
    public class GetAngleMode : WorkMode
    {
        #region Construction

        /// The axis angle readout names the frame its numbers are taken in, so the
        /// mode has to hear about an axis being drawn, cleared, or arriving with
        /// another specimen. Nothing unsubscribes: one instance lives as long as the
        /// window does.
        public GetAngleMode()
        {
            ActiveAlignment.Changed += AlignmentChanged;
        }

        /// Only the readout is refreshed. An angle already measured belongs to the
        /// operation that produced it, which carries the frame it was taken in.
        private void AlignmentChanged() => OnPropertyChanged(nameof(AxisAngleReference));

        #endregion

        #region Mode identity

        public override UserControl CreateControlPanel() => new TriangleControlPanel(this);
        public override string TabName => "Angle";
        public override bool IsStartingNewOperation => CurrentStep == 0 || CurrentStep == 3;

        /// <summary>Which of the two measurements the mode is set to take.</summary>
        public enum AngleMethod
        {
            Triangle,
            AxisAngle
        }

        private AngleMethod _currentMethod = AngleMethod.Triangle;

        public AngleMethod CurrentMethod
        {
            get => _currentMethod;
            set
            {
                if (!SetField(ref _currentMethod, value)) return;

                OnPropertyChanged(nameof(IsTriangleSelected));
                OnPropertyChanged(nameof(IsAxisAngleSelected));
                OnTipChanged?.Invoke();
            }
        }

        public bool IsTriangleSelected => CurrentMethod == AngleMethod.Triangle;
        public bool IsAxisAngleSelected => CurrentMethod == AngleMethod.AxisAngle;

        /// <summary>Switches tools from the control panel and starts the new one clean.</summary>
        public void SelectAngleMethod(string tag)
        {
            if (!Enum.TryParse(tag, out AngleMethod method)) return;

            CurrentMethod = method;
            ResetDrawingState();
        }

        #endregion

        #region Drawing state

        public Line CurrentUILine = null;

        public Vector2 PointA;
        public Vector2 PointB;
        public Vector2 PointC;

        public override void ResetDrawingState()
        {
            CurrentStep = 0;
            CurrentUILine = null;
            CurrentOperation.Clear();
            PointA = PointB = PointC = default;
        }

        #endregion

        #region Results

        private double _angleAResult;
        public double AngleAResult
        {
            get => _angleAResult;
            set => SetField(ref _angleAResult, value);
        }

        private double _angleBResult;
        public double AngleBResult
        {
            get => _angleBResult;
            set => SetField(ref _angleBResult, value);
        }

        private double _angleCResult;
        public double AngleCResult
        {
            get => _angleCResult;
            set => SetField(ref _angleCResult, value);
        }

        // Longest side divided by the height measured against that side.
        private double _triAspectRatioResult;
        public double TriAspectRatioResult
        {
            get => _triAspectRatioResult;
            set => SetField(ref _triAspectRatioResult, value);
        }

        // Current triangle area divided by the area of the previous triangle, or
        // "N/A" when there is no previous triangle to compare against.
        private object _relativeAreaResult;
        public object RelativeAreaResult
        {
            get => _relativeAreaResult;
            set => SetField(ref _relativeAreaResult, value);
        }

        // Clockwise angle from the specimen's axis to the line just drawn. Held as a
        // string so an unmeasured state can read "N/A" the way the ratios do.
        private object _axisAngleResult = "N/A";
        public object AxisAngleResult
        {
            get => _axisAngleResult;
            set => SetField(ref _axisAngleResult, value);
        }

        // The frame the next angle will be taken in, shown beneath the value so a
        // number taken on an unaligned specimen is never mistaken for an aligned one.
        // Read live, so drawing or clearing an axis is reflected the moment it happens
        // rather than at the next measurement.
        public string AxisAngleReference => ActiveAlignment.Current.IsAligned
            ? "Measuring from the specimen axis"
            : "No alignment set: measuring from the image";

        private string _triAreaScaledResult = "Unscaled";
        public string TriAreaScaledResult
        {
            get => _triAreaScaledResult;
            set => SetField(ref _triAreaScaledResult, value);
        }

        // Image-space area of the displayed triangle, kept so the scaled row can be
        // re-derived whenever the calibration changes or an undo restores a
        // different triangle.
        private double _imageArea;
        private bool _hasImageArea;

        private void RecomputeScaledResults()
        {
            TriAreaScaledResult = FormatScaledArea(_imageArea, _hasImageArea);
        }

        /// <summary>Restores the image-space area behind the scaled row.</summary>
        public void RestoreScaledMeasurements(double imageArea)
        {
            _imageArea = imageArea;
            _hasImageArea = true;
            RecomputeScaledResults();
        }

        /// <summary>Puts a stored axis angle back on the panel.</summary>
        public void RestoreAxisAngle(AxisAngleOperation operation)
        {
            if (operation == null) return;

            AxisAngleResult = FormatAngle(operation.AxisAngleDegrees);
        }

        public override void ClearMetadata()
        {
            AngleAResult = 0;
            AngleBResult = 0;
            AngleCResult = 0;
            TriAspectRatioResult = 0;
            RelativeAreaResult = "N/A";
            AxisAngleResult = "N/A";
            _imageArea = 0;
            _hasImageArea = false;
            RecomputeScaledResults();
        }

        public override void RefreshScalePlaceholders()
        {
            RecomputeScaledResults();
            OnPropertyChanged(nameof(AvgTriAreaScaledResult));
        }

        #endregion

        #region Click handling

        public override Vector2 ProcessMouseMovement(Vector2 mousePos)
        {
            if (CurrentUILine != null)
            {
                CurrentUILine.X2 = mousePos.X;
                CurrentUILine.Y2 = mousePos.Y;
            }

            return mousePos;
        }

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            return IsAxisAngleSelected
                ? ProcessAxisAngleClick(mousePos)
                : ProcessTriangleClick(mousePos);
        }

        /// Two clicks: the first starts the line, the second finishes it and measures
        /// its direction. Each line is one measurement, so the step returns to zero
        /// rather than carrying a point over the way the triangle does.
        private List<UIElement> ProcessAxisAngleClick(Vector2 mousePos)
        {
            List<UIElement> output = new();

            if (CurrentStep == 0)
            {
                PointA = mousePos;
                CurrentUILine = MakeLine(PointA, PointA);

                output.Add(CurrentUILine);
                CurrentOperation.Add(CurrentUILine);
                CurrentStep = 1;

                return output;
            }

            // Too close to the first point for a direction to mean anything, so the
            // click is spent rather than measured and the line goes on following the
            // cursor.
            if ((mousePos - PointA).Magnitude() < MinimumAxisLinePixels) return output;

            PointB = mousePos;
            CurrentUILine.X2 = mousePos.X;
            CurrentUILine.Y2 = mousePos.Y;

            MeasureAxisAngle();

            CurrentUILine = null;
            CurrentStep = 0;

            return output;
        }

        /// The direction of the drawn line in the specimen's frame. Canvas Y grows
        /// downward, so the angle runs clockwise on screen, and it is reported over a
        /// full turn: a line drawn one way and the same line drawn back are different
        /// readings, which is what tells a feature pointing forward from one pointing
        /// back.
        private void MeasureAxisAngle()
        {
            Vector2 along = PointB - PointA;

            bool aligned = ActiveAlignment.Current.IsAligned;

            double canvasAngle = Math.Atan2(along.Y, along.X);
            double degrees = ActiveAlignment.Current.ToAlignedAngle(canvasAngle) * 180.0 / Math.PI;

            degrees = Normalize360(degrees);

            AxisAngleResult = FormatAngle(degrees);

            CommitCurrentOperation(new AxisAngleOperation
            {
                OperationKind = "Axis Angle",
                AxisAngleDegrees = degrees,
                MeasuredAgainstAxis = aligned
            }, PointA, PointB);
        }

        private List<UIElement> ProcessTriangleClick(Vector2 mousePos)
        {
            List<UIElement> output = new();
            switch (CurrentStep)
            {
                case 0: // Start the first line and store the first point

                    PointA = mousePos;
                    CurrentUILine = MakeLine(PointA, PointA);
                    output.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep++;
                    break;

                case 1: // End the first line, store the second point, and start the second line

                    PointB = mousePos;
                    CurrentUILine.X2 = mousePos.X;
                    CurrentUILine.Y2 = mousePos.Y;
                    CurrentUILine = MakeLine(PointB, PointB);
                    output.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep++;
                    break;

                case 2: // Store the third point, close the triangle, measure it, and commit

                    PointC = mousePos;

                    var ab = MakeLine(PointA, PointB);
                    var bc = MakeLine(PointB, PointC);
                    var ca = MakeLine(PointC, PointA);

                    output.Add(ab);
                    output.Add(bc);
                    output.Add(ca);

                    // The corner letters follow Settings ▸ Font, like the rest of the
                    // writing on screen.
                    var labelA = MakeLabel("A", PointA, LabelFontSize, font: LabelFont);
                    var labelB = MakeLabel("B", PointB, LabelFontSize, font: LabelFont);
                    var labelC = MakeLabel("C", PointC, LabelFontSize, font: LabelFont);

                    output.Add(labelA);
                    output.Add(labelB);
                    output.Add(labelC);

                    CurrentOperation.Add(ab);
                    CurrentOperation.Add(bc);
                    CurrentOperation.Add(ca);
                    CurrentOperation.Add(labelA);
                    CurrentOperation.Add(labelB);
                    CurrentOperation.Add(labelC);

                    CalculateAndUpdateResults();

                    CommitCurrentOperation(new GetAngleOperation
                    {
                        OperationKind = "Triangle",
                        AngleA = AngleAResult,
                        AngleB = AngleBResult,
                        AngleC = AngleCResult,
                        TriAspectRatio = TriAspectRatioResult,
                        TriAreaImagePixels = _imageArea,
                        RelativeArea = RelativeAreaResult
                    }, PointA, PointB, PointC);

                    CurrentUILine = null;
                    CurrentStep++;
                    break;

                case 3: // Reuse this click as the first point of the next triangle

                    ResetDrawingState();
                    PointA = mousePos;

                    CurrentUILine = MakeLine(PointA, PointA);
                    output.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);

                    CurrentStep = 1;
                    break;
            }
            return output;
        }

        /// <summary>Shortest line, in canvas pixels, whose direction means anything.</summary>
        private const double MinimumAxisLinePixels = 8.0;

        private static string FormatAngle(double degrees) =>
            degrees.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + "\u00B0";

        private static double Normalize360(double degrees)
        {
            double value = degrees % 360.0;
            if (value < 0) value += 360.0;

            // Rounded to the precision the reading is shown at, so the number kept
            // and the number on screen never disagree, and so a value a hair under a
            // full turn is not rounded up past one: a full turn is no turn.
            value = Math.Round(value, 1);
            return value >= 360.0 ? 0.0 : value;
        }

        private void CalculateAndUpdateResults()
        {
            Vector2 AB = PointB - PointA;
            Vector2 AC = PointC - PointA;
            Vector2 BC = PointC - PointB;
            Vector2 CA = PointA - PointC;

            // Reject near-collinear points (not a triangle)
            double cross = (AB ^ AC);
            if (Math.Abs(cross) < 0.0001)
                return;

            AngleAResult = GeometryCalculations.InteriorAngle(PointB, PointA, PointC);
            AngleBResult = GeometryCalculations.InteriorAngle(PointA, PointB, PointC);
            AngleCResult = GeometryCalculations.InteriorAngle(PointA, PointC, PointB);

            double sideAB = AB.Magnitude();
            double sideBC = BC.Magnitude();
            double sideCA = CA.Magnitude();

            double canvasArea = Math.Abs(cross) / 2.0;

            // Aspect ratio divides a length by a height derived from the area, so
            // both terms have to be in the same space; it is computed before the
            // area is converted.
            double longestSide = Math.Max(sideAB, Math.Max(sideBC, sideCA));
            TriAspectRatioResult = GeometryCalculations.TriangleAspectRatio(longestSide, canvasArea);

            _imageArea = ToImageArea(canvasArea);
            _hasImageArea = true;
            RecomputeScaledResults();

            // Compare area to the previous triangle's area if one exists
            var previousTriangle = TriangleOps.LastOrDefault();

            RelativeAreaResult = GeometryCalculations.RelativeArea(
                _imageArea, previousTriangle?.TriAreaImagePixels ?? 0);
        }

        #endregion

        #region Operation averages

        // Live averages of each numeric triangle output across all attempts, read from the
        // undo/redo history so they stay correct through commit/undo/redo/clear. "Relative
        // Size" is excluded: it's a ratio between consecutive attempts, not an absolute
        // measurement, and is stored as a mixed value, so a mean of it isn't well-defined.

        private IEnumerable<GetAngleOperation> TriangleOps => OperationsOfKind<GetAngleOperation>();

        public string AvgAngleAResult => FormatAverage(TriangleOps.Select(o => o.AngleA));
        public string AvgAngleBResult => FormatAverage(TriangleOps.Select(o => o.AngleB));
        public string AvgAngleCResult => FormatAverage(TriangleOps.Select(o => o.AngleC));
        public string AvgTriAspectRatioResult => FormatAverage(TriangleOps.Select(o => o.TriAspectRatio));
        public string AvgTriAreaScaledResult => FormatScaledAreaAverage(TriangleOps.Select(o => o.TriAreaImagePixels));

        private IEnumerable<AxisAngleOperation> AxisAngleOps => OperationsOfKind<AxisAngleOperation>();

        /// The mean direction of every angle measured on this specimen. Taken round
        /// the circle rather than as a plain average: 359 degrees and 1 degree are two
        /// degrees apart, and averaging them as numbers would answer 180.
        public string AvgAxisAngleResult => FormatMeanDirection(
            AxisAngleOps.Select(o => o.AxisAngleDegrees));

        private static string FormatMeanDirection(IEnumerable<double> degrees)
        {
            var list = degrees.ToList();
            if (list.Count == 0) return "N/A";

            double x = 0, y = 0;

            foreach (double d in list)
            {
                double radians = d * Math.PI / 180.0;
                x += Math.Cos(radians);
                y += Math.Sin(radians);
            }

            // Directions cancelling out leave no mean to report, which is what an
            // evenly spread set of angles genuinely has.
            if (Math.Abs(x) < 1e-9 && Math.Abs(y) < 1e-9) return "N/A";

            double mean = Normalize360(Math.Atan2(y, x) * 180.0 / Math.PI);
            return mean.ToString();
        }

        protected override void RecomputeAverages()
        {
            OnPropertyChanged(nameof(AvgAngleAResult));
            OnPropertyChanged(nameof(AvgAngleBResult));
            OnPropertyChanged(nameof(AvgAngleCResult));
            OnPropertyChanged(nameof(AvgTriAspectRatioResult));
            OnPropertyChanged(nameof(AvgTriAreaScaledResult));
            OnPropertyChanged(nameof(AvgAxisAngleResult));
        }

        #endregion

        #region Tips

        private static readonly string[] TriangleTips = BuildTips(
            "💡 Click three points to define a triangle. Results update automatically after the third click.",
            "💡 The aspect ratio of any triangle is the length of its longest side divided by its height.");

        private static readonly string[] AxisAngleTips = BuildTips(
            "💡 Click two points along a feature to measure the direction it points.",
            "💡 Set the specimen's axis first with Tools > Align > Align Specimen, or the angle is measured from the image instead.");

        public override string[] GetTips() => IsAxisAngleSelected ? AxisAngleTips : TriangleTips;

        #endregion
    }
}