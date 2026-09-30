using DinoLino.DataTypes;
using DinoLino.Properties;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using DinoLino.Utilities.Operations;
using SharpVectors.Dom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Policy;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using static System.Net.Mime.MediaTypeNames;

// This one file holds Draw mode itself (in DinoLino.Utilities.Modes) and the label layer
// its Add Label tool puts on the workspace (in DinoLino).

namespace DinoLino.Utilities.Modes
{
    /// <summary>Draw mode for creating constrained shapes and lines on the canvas.</summary>
    public class DrawMode : WorkMode
    {
        // ---- Shared draw state ----

        public override UserControl CreateControlPanel() => new DrawControlPanel(this);
        public override string TabName => "Draw";
        public override bool IsStartingNewOperation => CurrentStep == 0;

        public enum DrawMethod
        {
            None,
            Shape,
            Line,
            Text
        }

        private DrawMethod _currentMethod = DrawMethod.None;

        public DrawMethod CurrentMethod
        {
            get => _currentMethod;
            set
            {
                if (!SetField(ref _currentMethod, value)) return;
                OnPropertyChanged(nameof(IsShapeSelected));
                OnPropertyChanged(nameof(IsLineSelected));
                OnPropertyChanged(nameof(IsTextSelected));
                OnTipChanged?.Invoke();
            }
        }

        public bool IsShapeSelected => CurrentMethod == DrawMethod.Shape;
        public bool IsLineSelected => CurrentMethod == DrawMethod.Line;
        public bool IsTextSelected => CurrentMethod == DrawMethod.Text;

        private AnnotationKind _currentLabelKind = AnnotationKind.Text;

        /// Which of the three a click puts on the picture: words, a dot or a star.
        public AnnotationKind CurrentLabelKind
        {
            get => _currentLabelKind;
            set
            {
                if (!SetField(ref _currentLabelKind, value)) return;
                OnTipChanged?.Invoke();
            }
        }

        public void SelectLabelKind(string tag)
        {
            if (Enum.TryParse(tag, out AnnotationKind kind)) CurrentLabelKind = kind;
        }

        private Vector2 _dragStart;

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            return CurrentMethod switch
            {
                DrawMethod.Line => ProcessLineClick(mousePos),
                DrawMethod.Shape => ProcessShapeClick(mousePos),
                _ => new List<UIElement>()
            };
        }

        public override Vector2 ProcessMouseMovement(Vector2 mousePos)
        {
            return CurrentMethod switch
            {
                DrawMethod.Shape => ProcessShapeMouseMovement(mousePos),
                DrawMethod.Line => ProcessLineMouseMovement(mousePos),
                _ => mousePos
            };
        }

        public void SelectDrawMethod(string tag)
        {
            if (Enum.TryParse(tag, out DrawMethod method))
            {
                CurrentMethod = method;
                ResetDrawingState();
            }
        }

        private void FinishOperation()
        {
            // Clear the transient state so the next draw starts cleanly.
            CurrentOperation.Clear();
            _currentShape = null;
            _currentLine = null;
            CurrentStep = 0;
        }

        internal override void OnHistoryChanged()
        {
            base.OnHistoryChanged();

            // Clear the stored reference direction once no constrained line remains.
            bool anyConstrainedLinesRemain = UndoRedoManager?.History
                .OfType<LineOperation>()
                .Any(op => op.LineLengthImagePixels > 0.00001) ?? false;

            if (!anyConstrainedLinesRemain)
            {
                _hasReferenceLineDirection = false;
                _referenceLineDirection = default;
            }
        }

        public override void ResetDrawingState()
        {
            // Reset the in-progress gesture without clearing the full mode state.
            CurrentStep = 0;
            _currentShape = null;
            _currentLine = null;
            _dragStart = default;
            _referenceLineDirection = default;
            _hasReferenceLineDirection = false;
            CurrentOperation.Clear();
        }

        // ---- Scaled measurements ----

        // Image-space measurements behind the scaled rows, kept so those rows can be
        // re-derived whenever the calibration changes or an undo restores a different
        // shape or line.
        private double _imageShapeArea;
        private bool _hasImageShapeArea;
        private double _imageLineLength;
        private double _imageLineDeltaX;
        private double _imageLineDeltaY;
        private bool _hasImageLineLength;   // covers all three: they are set together

        private void RecomputeScaledResults()
        {
            ShapeAreaScaledResult = FormatScaledArea(_imageShapeArea, _hasImageShapeArea);
            LineLengthScaledResult = FormatScaledLength(_imageLineLength, _hasImageLineLength);
            LineDeltaXScaledResult = FormatScaledLength(_imageLineDeltaX, _hasImageLineLength);
            LineDeltaYScaledResult = FormatScaledLength(_imageLineDeltaY, _hasImageLineLength);
        }

        /// <summary>Restores the image-space area behind the shape row.</summary>
        public void RestoreShapeMeasurement(double imageArea)
        {
            _imageShapeArea = imageArea;
            _hasImageShapeArea = true;
            RecomputeScaledResults();
        }

        /// <summary>Restores the image-space length and axis components behind the line rows.</summary>
        public void RestoreLineMeasurement(double imageLength, double imageDeltaX, double imageDeltaY)
        {
            _imageLineLength = imageLength;
            _imageLineDeltaX = imageDeltaX;
            _imageLineDeltaY = imageDeltaY;
            _hasImageLineLength = true;
            RecomputeScaledResults();
        }

        public override void ClearMetadata()
        {
            DrawAspectRatioResult = 0;
            RelativeAreaResult = "N/A";
            LineLengthRatioResult = "N/A";
            LineAngleResult = "N/A";
            _imageShapeArea = 0;
            _hasImageShapeArea = false;
            _imageLineLength = 0;
            _imageLineDeltaX = 0;
            _imageLineDeltaY = 0;
            _hasImageLineLength = false;
            RecomputeScaledResults();
        }

        public override void RefreshScalePlaceholders()
        {
            RecomputeScaledResults();
        }

        // ---- Shape tools ----

        public enum ShapeConstraint
        {
            None,
            Ellipse,
            Circle,
            Rectangle,
            Square,
        }

        public ShapeConstraint CurrentShape { get; set; } = ShapeConstraint.None;

        private Shape _currentShape = null;

        private double _drawAspectRatioResult;
        private object _relativeAreaResult;

        private string _shapeAreaScaledResult = "Unscaled";
        public string ShapeAreaScaledResult
        {
            get => _shapeAreaScaledResult;
            set => SetField(ref _shapeAreaScaledResult, value);
        }

        public double DrawAspectRatioResult
        {
            get => _drawAspectRatioResult;
            set => SetField(ref _drawAspectRatioResult, value);
        }

        public object RelativeAreaResult
        {
            get => _relativeAreaResult;
            set => SetField(ref _relativeAreaResult, value);
        }

        public void SelectShape(string tag)
        {
            if (Enum.TryParse(tag, out ShapeConstraint shape))
            {
                CurrentShape = shape;
                ResetDrawingState();
            }
        }

        private Vector2 ProcessShapeMouseMovement(Vector2 mousePos)
        {
            if (CurrentShape == ShapeConstraint.None || _currentShape == null)
                return mousePos;

            // Constrain size first, then reposition the shape so it grows from the anchor.
            var (width, height) = GetConstrainedShapeSize(mousePos);
            var (x, y) = GetShapePosition(mousePos, width, height);

            _currentShape.Width = width;
            _currentShape.Height = height;

            Canvas.SetLeft(_currentShape, x);
            Canvas.SetTop(_currentShape, y);

            return mousePos;
        }

        private List<UIElement> ProcessShapeClick(Vector2 mousePos)
        {
            List<UIElement> output = new();

            if (CurrentShape == ShapeConstraint.None)
                return output;

            switch (CurrentStep)
            {
                case 0:
                    // First click records the anchor and creates the preview shape.
                    _dragStart = mousePos;
                    _currentShape = MakeShape(mousePos, mousePos);
                    output.Add(_currentShape);
                    CurrentOperation.Add(_currentShape);
                    CurrentStep++;
                    break;

                case 1:
                    // Second click finalizes the size, computes results, and commits the operation.
                    var (width, height) = GetConstrainedShapeSize(mousePos);
                    var (left, top) = GetShapePosition(mousePos, width, height);

                    CalculateAndUpdateResults(width, height);

                    CommitCurrentOperation(new ShapeOperation
                    {
                        OperationKind = "Shape",

                        // The kind has to survive the draw: the workshop table keeps a
                        // separate column set and a separate attempt count per shape.
                        ShapeKind = CurrentShape,

                        DrawAspectRatio = DrawAspectRatioResult,
                        RelativeArea = RelativeAreaResult,
                        ShapeAreaImagePixels = _imageShapeArea
                    },
                    // Opposite corners of the rectangle just measured, so the geometry
                    // recorded and the area recorded describe the same shape.
                    new Vector2(left, top),
                    new Vector2(left + width, top + height));

                    FinishOperation();
                    break;
            }

            return output;
        }

        private (double x, double y) GetShapePosition(Vector2 mousePos, double width, double height)
        {
            // Keep the original click as the anchor even when the pointer moves left/up.
            double x = mousePos.X >= _dragStart.X
                ? _dragStart.X
                : _dragStart.X - width;

            double y = mousePos.Y >= _dragStart.Y
                ? _dragStart.Y
                : _dragStart.Y - height;

            return (x, y);
        }

        private Shape MakeShape(Vector2 start, Vector2 end)
        {
            // Width/height are stored separately from the position so the shape can be resized live.
            double x = Math.Min(start.X, end.X);
            double y = Math.Min(start.Y, end.Y);
            double width = Math.Abs(end.X - start.X);
            double height = Math.Abs(end.Y - start.Y);

            Shape shape;
            switch (CurrentShape)
            {
                case ShapeConstraint.Ellipse:
                case ShapeConstraint.Circle:
                    shape = new Ellipse();
                    break;
                default:
                    shape = new Rectangle();
                    break;
            }

            shape.Stroke = LineColor;
            shape.StrokeThickness = LineThickness;
            shape.Width = width;
            shape.Height = height;
            Canvas.SetLeft(shape, x);
            Canvas.SetTop(shape, y);

            return shape;
        }

        // ---- Line tools ----

        public enum LineConstraint
        {
            None,
            Parallel,
            Perpendicular,
            AngleLocked
        }

        public LineConstraint CurrentLineType { get; set; } = LineConstraint.None;

        private Line _currentLine = null;
        private Vector2 _referenceLineDirection;
        private bool _hasReferenceLineDirection;

        private string _lineLengthScaledResult = "Unscaled";
        public string LineLengthScaledResult
        {
            get => _lineLengthScaledResult;
            set => SetField(ref _lineLengthScaledResult, value);
        }

        private string _lineDeltaXScaledResult = "Unscaled";
        public string LineDeltaXScaledResult
        {
            get => _lineDeltaXScaledResult;
            set => SetField(ref _lineDeltaXScaledResult, value);
        }

        private string _lineDeltaYScaledResult = "Unscaled";
        public string LineDeltaYScaledResult
        {
            get => _lineDeltaYScaledResult;
            set => SetField(ref _lineDeltaYScaledResult, value);
        }

        public double LockedAngleDegrees { get; set; } = 0;

        private object _lineLengthRatioResult;
        public object LineLengthRatioResult
        {
            get => _lineLengthRatioResult;
            set => SetField(ref _lineLengthRatioResult, value);
        }

        private object _lineAngleResult = "N/A";

        /// Clockwise angle in degrees between the line just drawn and the line it was
        /// drawn against, or "N/A" for the first line. Boxed as a double or that
        /// string, matching LineLengthRatioResult.
        public object LineAngleResult
        {
            get => _lineAngleResult;
            set => SetField(ref _lineAngleResult, value);
        }

        public void SelectLineConstraint(string tag)
        {
            if (Enum.TryParse(tag, out LineConstraint constraint))
            {
                CurrentLineType = constraint;
                ResetDrawingState();
            }
        }

        private Vector2 ProcessLineMouseMovement(Vector2 mousePos)
        {
            if (_currentLine == null)
                return mousePos;

            // Recompute the live endpoint each frame so the preview follows the constraint.
            Vector2 constrained = ApplyLineConstraint(_dragStart, mousePos);

            _currentLine.X2 = constrained.X;
            _currentLine.Y2 = constrained.Y;

            return mousePos;
        }

        private List<UIElement> ProcessLineClick(Vector2 mousePos)
        {
            List<UIElement> output = new();

            switch (CurrentStep)
            {
                case 0:
                    // First click creates the preview line and stores the anchor point.
                    _dragStart = mousePos;
                    _currentLine = MakeLine(mousePos, mousePos);
                    output.Add(_currentLine);
                    CurrentOperation.Add(_currentLine);
                    CurrentStep = 1;
                    break;

                case 1:
                    // Second click locks the endpoint, updates measurements, and commits the line.
                    Vector2 finalPoint = ApplyLineConstraint(_dragStart, mousePos);
                    _currentLine.X2 = finalPoint.X;
                    _currentLine.Y2 = finalPoint.Y;

                    // Measured before the reference direction is captured below: the
                    // line that DEFINES the reference was drawn freely, so its own
                    // angle belongs to the line before it, not to itself.
                    object angle = MeasureLineAngle();

                    TryCaptureReferenceDirection();
                    CommitLine(angle);

                    FinishOperation();
                    break;
            }

            return output;
        }

        private void TryCaptureReferenceDirection()
        {
            // The first constrained line defines the direction used by later parallel/perpendicular lines.
            if (CurrentLineType == LineConstraint.None || _hasReferenceLineDirection)
                return;

            Vector2 rawDir = new Vector2(_currentLine.X2 - _currentLine.X1, _currentLine.Y2 - _currentLine.Y1);
            double lenSq = rawDir.X * rawDir.X + rawDir.Y * rawDir.Y;

            if (lenSq <= 0.000001) return;

            double len = Math.Sqrt(lenSq);
            _referenceLineDirection = new Vector2(rawDir.X / len, rawDir.Y / len);
            _hasReferenceLineDirection = true;
        }

        private void CommitLine(object angleToPrevious)
        {
            // Measure the final line in image pixels, the space every stored
            // measurement uses.
            double dx = _currentLine.X2 - _currentLine.X1;
            double dy = _currentLine.Y2 - _currentLine.Y1;
            double length = ToImageLength(Math.Sqrt(dx * dx + dy * dy));

            // The same line resolved onto the specimen's own axes. The X and Y axes are
            // perpendicular by construction — a drawn Y axis is turned back 90° when the
            // alignment is stored — so this is a rotation, and the two components close
            // the triangle: deltaX² + deltaY² == length². Magnitudes, not signed offsets:
            // the sign would turn on which end was clicked first and which way the axis
            // line was dragged, neither of which says anything about the specimen.
            // Without an alignment ToAligned passes the vector through, leaving the
            // components on the image's own axes rather than blank.
            Vector aligned = ActiveAlignment.Current.ToAligned(new Vector(dx, dy));
            double deltaX = ToImageLength(Math.Abs(aligned.X));
            double deltaY = ToImageLength(Math.Abs(aligned.Y));

            _imageLineLength = length;
            _imageLineDeltaX = deltaX;
            _imageLineDeltaY = deltaY;
            _hasImageLineLength = true;
            RecomputeScaledResults();

            // Compare against the previous measured line, if one exists.
            var prev = PreviousLine();
            LineLengthRatioResult = GeometryCalculations.RelativeLength(
                length, prev?.LineLengthImagePixels ?? 0);
            LineAngleResult = angleToPrevious;

            CommitCurrentOperation(new LineOperation
            {
                OperationKind = "Lines",
                LineLengthImagePixels = length,
                LineDeltaXImagePixels = deltaX,
                LineDeltaYImagePixels = deltaY,
                LineLengthRatio = LineLengthRatioResult,
                LineAngle = LineAngleResult,
                HeadingDegrees = HeadingOf(dx, dy),
                MeasuredAgainstAxis = ActiveAlignment.Current.IsAligned
            },
            new Vector2(_currentLine.X1, _currentLine.Y1),
            new Vector2(_currentLine.X2, _currentLine.Y2));
        }

        /// The most recent committed line with real length, or null when this is the
        /// first one. Called before the new line is committed, so it never finds
        /// the line being drawn.
        private LineOperation PreviousLine() =>
            OperationsOfKind<LineOperation>().LastOrDefault(op => op.LineLengthImagePixels > 0.00001);

        // ---- Line angle ----

        /// Clockwise angle from the line this one was drawn against to the line just
        /// drawn, in degrees within [0, 360). "N/A" when there is nothing to measure
        /// against, matching how the line-ratio row reports the first line.
        private object MeasureLineAngle()
        {
            double dx = _currentLine.X2 - _currentLine.X1;
            double dy = _currentLine.Y2 - _currentLine.Y1;

            // A click that never moved has no direction to report.
            if (Math.Sqrt(dx * dx + dy * dy) < 0.00001) return "N/A";

            double heading = HeadingOf(dx, dy);

            // A constrained line is drawn against the reference direction rather than
            // against whatever happened to be drawn last, so that is what its angle is
            // measured from. A run of perpendicular lines therefore reads 90, 90, 90
            // instead of 90 followed by zeroes.
            if (CurrentLineType != LineConstraint.None && _hasReferenceLineDirection)
            {
                return Sweep(heading - HeadingOf(_referenceLineDirection.X, _referenceLineDirection.Y));
            }

            // Free-drawn: measure against the line before it.
            var previous = PreviousLine();
            if (previous == null) return "N/A";

            return Sweep(heading - previous.HeadingDegrees);
        }

        // Canvas Y grows downwards, so atan2 already sweeps clockwise on screen:
        // 0° points right, 90° down, 180° left, 270° up. Reading a clock face, a line
        // drawn towards 11 o'clock after one drawn towards 12 gives 330, not 30.
        // Headings stay in canvas space: a uniform scale and a translation both
        // preserve angles, so the view ratio does not enter here.
        private static double HeadingOf(double dx, double dy) =>
            Normalize360(Math.Atan2(dy, dx) * 180.0 / Math.PI);

        private static double Sweep(double degrees)
        {
            double value = Math.Round(Normalize360(degrees), 1);

            // Rounding can push 359.97 over the top; a full turn is no turn.
            return value >= 360.0 ? 0.0 : value;
        }

        private static double Normalize360(double degrees)
        {
            degrees %= 360.0;
            return degrees < 0 ? degrees + 360.0 : degrees;
        }

        // ---- Line constraints ----

        private Vector2 ApplyLineConstraint(Vector2 start, Vector2 mousePos)
        {
            if (CurrentLineType == LineConstraint.None || !_hasReferenceLineDirection)
                return mousePos;

            if (CurrentLineType == LineConstraint.AngleLocked)
                return ConstrainToAngle(start, mousePos, LockedAngleDegrees);

            // Parallel uses the captured direction; perpendicular rotates that direction by 90°.
            Vector2 constrainDir = CurrentLineType == LineConstraint.Parallel
                ? _referenceLineDirection
                : new Vector2(-_referenceLineDirection.Y, _referenceLineDirection.X);

            Vector2 toMouse = mousePos - start;
            double magnitude = (toMouse.X * constrainDir.X) + (toMouse.Y * constrainDir.Y);

            // Project the mouse vector onto the constraint direction.
            return new Vector2(start.X + constrainDir.X * magnitude, start.Y + constrainDir.Y * magnitude);
        }

        private (double width, double height) GetConstrainedShapeSize(Vector2 mousePos)
        {
            // Measure raw size from the anchor point.
            double rawW = Math.Abs(mousePos.X - _dragStart.X);
            double rawH = Math.Abs(mousePos.Y - _dragStart.Y);

            // Squares/circles lock both dimensions to the larger drag distance.
            if (CurrentShape == ShapeConstraint.Square || CurrentShape == ShapeConstraint.Circle)
            {
                double size = Math.Max(rawW, rawH);
                return (size, size);
            }

            return (rawW, rawH);
        }

        public void UpdateAngle(string textInput)
        {
            if (double.TryParse(textInput, out double val))
            {
                LockedAngleDegrees = val;
            }
            else
            {
                // Invalid input disables the explicit angle lock until the user enters a number again.
                LockedAngleDegrees = 0;
            }
        }

        private Vector2 ConstrainToAngle(Vector2 origin, Vector2 mousePos, double angleDegrees)
        {
            // Start from the reference line direction, then add the user-specified offset.
            double baseAngleRadians = Math.Atan2(_referenceLineDirection.Y, _referenceLineDirection.X);
            double lockedRadians = baseAngleRadians + angleDegrees * Math.PI / 180.0;

            Vector2 direction = new Vector2(Math.Cos(lockedRadians), Math.Sin(lockedRadians));
            Vector2 toMouse = mousePos - origin;
            double magnitude = (toMouse.X * direction.X) + (toMouse.Y * direction.Y);

            // Project onto the rotated direction to keep the line at the requested angle.
            return new Vector2(origin.X + direction.X * magnitude, origin.Y + direction.Y * magnitude);
        }

        // ---- Results and tips ----

        private void CalculateAndUpdateResults(double width, double height)
        {
            // Use ellipse math for round shapes and rectangle math for box shapes.
            double canvasArea = (CurrentShape == ShapeConstraint.Ellipse || CurrentShape == ShapeConstraint.Circle)
                ? GeometryCalculations.EllipseArea(width, height)
                : GeometryCalculations.RectangleArea(width, height);

            _imageShapeArea = ToImageArea(canvasArea);
            _hasImageShapeArea = true;
            RecomputeScaledResults();

            DrawAspectRatioResult = height > 1e-5 ? Math.Round(width / height, 2) : 0;

            // Relative area compares against the most recent shape OF THE SAME KIND,
            // so a circle is measured against the previous circle rather than against
            // whatever shape happened to be drawn before it.
            var previous = OperationsOfKind<ShapeOperation>()
                .LastOrDefault(op => op.ShapeKind == CurrentShape);

            RelativeAreaResult = GeometryCalculations.RelativeArea(
                _imageShapeArea, previous?.ShapeAreaImagePixels ?? 0);
        }

        private static readonly string[] ShapeTips = BuildTips(
            "💡 Any number of shapes or lines may be overlaid on the image. Each click adds a new shape or line.",
            "💡 Aspect ratio is the horizontal length of the shape divided by its maximum height.",
            "💡 Each shape kind is counted and exported separately: rectangles, squares, ellipses, and circles have their own columns.");

        private static readonly string[] LineTips = BuildTips(
            "💡 Any number of shapes or lines may be overlaid on the image. Each click adds a new shape or line.",
            "💡 Line ratio is the length of the most recently drawn line (Line n) divided by the length of the line drawn before it (Line n-1).",
            "💡 Angle is measured clockwise from the previous line, so a line drawn towards 11 o'clock after one towards 12 reads 330°.");

        private static readonly string[] TextTips = BuildTips(
            "💡 Choose Text, Point or Star, then click the image to add a label. A click elsewhere on the image adds another - leave Add Label to stop.",
            "💡 A label may be edited when the pointer rests on it for a moment. The box and its handles then appear and may be selected, dragged, resized, or deleted.",
            "💡 Click, hold, and drag the corner of the text box to resize a label. Click, hold, and drag from its top edge to move it.",
            "💡 A new label takes the size and typeface set under Settings, Font. Changing that setting changes the next label added, not the ones already on the image.",
            "💡 Labels stay on screen whether See Previous Operations is on or off, and they are not counted or exported in data tables.");

        private static readonly string[] NoMethodTips = BuildTips(
            "💡 Select a drawing method to begin.");

        public override string[] GetTips()
        {
            if (IsShapeSelected) return ShapeTips;
            if (IsLineSelected) return LineTips;
            if (IsTextSelected) return TextTips;
            return NoMethodTips;
        }
    }
}

namespace DinoLino
{
    public partial class MainWindow
    {
        // =====================
        // Labels (Draw mode, Add Label)
        // =====================
        // A label is kept in the operation history, so Undo, Redo, Clear All and the
        // per-specimen archive all reach it without a second mechanism behind them. It is
        // drawn on UI_LabelCanvas rather than UI_WorkCanvas, and that is what keeps it out
        // of the sweep See Previous Operations makes of the drawn operations: a label
        // annotates the picture rather than measuring it, so it stays either way. Being a
        // kind no table has a column for, no counter tallies and no history tab picks, it
        // is left out of all three for the same reason.
        //
        // Three things can be put on the picture: words, a dot and a star. They behave
        // alike once they are there, so one record, one layer and one set of handles serve
        // all three, and only the mark itself differs. Words begin at the point clicked; a
        // dot or a star is centred on it, since what a mark says is where it sits.
        //
        // A label has two states. At rest it is the mark and nothing else, so the picture
        // shows through around it and it reads as part of the figure. Resting the pointer
        // on one for LabelHoverDelay brings the box out: the border, the ground behind the
        // mark, the bin, the corner grip that sizes it and the strip along the top edge
        // that moves it. Only then can words be typed in. The wait is what keeps a pointer
        // crossing the picture from turning every label it passes into a control.

        private const double LabelMinSize = 5.0;
        private const double LabelMaxSize = 200.0;

        // The handles sit outside the box, each at a corner of its own, so the box is free
        // to hold nothing but the mark and to close right up against it.
        private const double LabelBinSize = 18.0;
        private const double LabelGripSize = 14.0;

        // The strip along the top edge that moves a label: shallow, so it takes the border
        // and the space above the first line rather than the words themselves, but never
        // so shallow against a large label that it is hard to take hold of.
        private const double LabelMoverHeight = 5.0;
        private const double LabelMoverInLetters = 0.25;

        // Enough of an empty box to take hold of before anything has been typed in it.
        // Flat rather than a share of the lettering: a floor that grew with the size would
        // hold a short label's box open long after there was something in it to measure.
        private const double LabelMinWidth = 40.0;

        // The ground left around a dot or a star, and so half of how far the box's corner
        // lies from the point the mark is centred on.
        private const double LabelMarkPad = 4.0;

        // Words fold at this many times the height of a letter, so a long label wraps into
        // a block at the same width in letters whatever size it is set to rather than
        // running off the side of the picture.
        private const double LabelWrapAt = 22.0;

        // How long the pointer must rest on a label before its box comes out.
        private static readonly TimeSpan LabelHoverDelay = TimeSpan.FromSeconds(1.5);

        // Labels whose box was out when a screenshot put it away.
        private readonly List<LabelVisual> _hiddenForScreenshot = new List<LabelVisual>();

        /// One label on the picture: the mark, the bin that removes it, the corner grip
        /// that sizes it and the top strip that moves it, together with the wait that
        /// brings those three out.
        private class LabelVisual : Grid
        {
            public AnnotationOperation Note;
            public Border Chrome;
            public FrameworkElement Mark;

            /// The mark itself when the label is words, and null when it is a dot or a
            /// star: only words have anything to type in or a caret to put there.
            public TextBox Box;

            public Button Bin;
            public FrameworkElement Grip;
            public FrameworkElement Mover;

            /// Invisible ground joining the box to the two handles outside it.
            public FrameworkElement Apron;

            public DispatcherTimer Hover;
            public bool Armed;
        }

        /// Puts a label where the picture was clicked, with its box already out and, for
        /// words, the caret in it: a label just placed is one the user means to work on, so
        /// it does not make them wait out the hover before they can.
        private void AddLabel(Vector2 canvasPos, AnnotationKind kind)
        {
            if (UndoRedoManager == null || UI_LabelCanvas == null) return;

            Point image = LabelTransform().CanvasToImage(new Point(canvasPos.X, canvasPos.Y));

            // No SourceMode: a label is not a measurement, so no mode has a reading to
            // restore from it or to clear when it goes.
            var note = new AnnotationOperation
            {
                OperationKind = kind.ToString(),
                Kind = kind,
                ImageX = image.X,
                ImageY = image.Y,
                Size = StartingLabelSize(),
                FontName = _currentFont?.Source ?? ""
            };

            // Committing raises the history change the layer listens for, so the label is
            // on screen by the time its handles and its caret are asked for.
            UndoRedoManager.Commit(note);
            ArmNewLabel(note);
        }

        /// Takes a label off the picture. Redo brings it back, exactly as undoing it would,
        /// which is why this does not use the permanent removal the tables do.
        private void DeleteLabel(AnnotationOperation note)
        {
            if (UndoRedoManager == null || note == null) return;
            UndoRedoManager.Retract(note);
        }

        /// Draws the label layer from the history, which is where the labels are kept.
        /// Every change to the history comes through here, so adding, deleting, undoing,
        /// redoing, clearing and changing specimen all land in one place rather than each
        /// keeping the layer in step by itself.
        private void RefreshLabels()
        {
            if (UI_LabelCanvas == null) return;

            if (UndoRedoManager == null)
            {
                ClearLabelLayer();
                return;
            }

            var notes = UndoRedoManager.History.OfType<AnnotationOperation>().ToList();

            // Rebuilding throws away the box being typed in, so it is only done when the
            // labels themselves have moved on. Measuring something else changes the
            // history without changing the labels, and must not take the caret.
            if (LabelsMatch(notes)) return;

            ClearLabelLayer();

            foreach (var note in notes)
            {
                var visual = BuildLabel(note);
                PositionLabel(visual, note);
                UI_LabelCanvas.Children.Add(visual);
            }
        }

        // Each label carries a wait that may be part-way through. Stopping it here keeps it
        // from coming due against a label that is no longer on screen.
        private void ClearLabelLayer()
        {
            foreach (UIElement child in UI_LabelCanvas.Children)
            {
                if (child is LabelVisual visual) visual.Hover.Stop();
            }

            bool hadCaret = UI_LabelCanvas.IsKeyboardFocusWithin;

            _hiddenForScreenshot.Clear();
            UI_LabelCanvas.Children.Clear();

            // A label taken away under the caret leaves the keyboard with the window
            // itself, where the shortcuts that ask for the workspace stop answering.
            // Handing it back is what lets the arrow keys and the space bar carry on.
            if (hadCaret) UI_WorkCanvas?.Focus();
        }

        // True when the layer already holds these labels, in this order.
        private bool LabelsMatch(List<AnnotationOperation> notes)
        {
            if (UI_LabelCanvas.Children.Count != notes.Count) return false;

            for (int i = 0; i < notes.Count; i++)
            {
                if (!(UI_LabelCanvas.Children[i] is LabelVisual visual)
                    || !ReferenceEquals(visual.Note, notes[i])) return false;
            }

            return true;
        }

        /// Puts every label back where its stored position says. The picture is laid out to
        /// fit, so resizing the window moves the canvas under the labels; this is what
        /// keeps each one on the feature it names.
        private void RepositionLabels()
        {
            if (UI_LabelCanvas == null) return;

            foreach (UIElement child in UI_LabelCanvas.Children)
            {
                if (child is LabelVisual visual) PositionLabel(visual, visual.Note);
            }
        }

        // The mapping the modes draw through, so a label and the measurements around it
        // agree about where a point on the picture is. Identity until a picture has been
        // laid out, which is harmless: there are no labels before then.
        private ViewTransform LabelTransform()
        {
            var transform = CurrentWorkMode?.ImageTransform ?? ViewTransform.Identity;
            return transform.IsValid ? transform : ViewTransform.Identity;
        }

        private void PositionLabel(FrameworkElement visual, AnnotationOperation note)
        {
            Point canvas = LabelTransform().ImageToCanvas(new Point(note.ImageX, note.ImageY));
            Point offset = LabelAnchorOffset(note);

            Canvas.SetLeft(visual, canvas.X - offset.X);
            Canvas.SetTop(visual, canvas.Y - offset.Y);
        }

        // How far the box's top left corner lies from the point the label is pinned to.
        // Words begin at that point; a dot or a star straddles it.
        private static Point LabelAnchorOffset(AnnotationOperation note)
        {
            if (note.Kind == AnnotationKind.Text) return new Point(0, 0);

            double half = LabelMarkPad + note.Size / 2;
            return new Point(half, half);
        }

        // The typeface the label was written in, which is kept with the label exactly as
        // its size is. Read live from the setting instead, the words would change face
        // under the user the next time anything rebuilt the layer, and a label saved in one
        // face would come back in whichever was current when the project was opened.
        private FontFamily LabelFontFor(AnnotationOperation note) =>
            string.IsNullOrEmpty(note.FontName) ? _currentFont : new FontFamily(note.FontName);

        /// What a label starts out at: the size and typeface set under Settings, Font, so
        /// labels match the rest of the writing on screen and a larger setting puts on a
        /// larger mark. The start only — the corner grip gives each label its own size from
        /// there, and changing the setting afterwards leaves the labels already placed as
        /// they are.
        private double StartingLabelSize()
        {
            // Not a number passes both ends of a comparison, so it would come through a
            // plain clamp untouched and be written onto the record as the label's size.
            if (double.IsNaN(_currentFontSize)) return LabelMinSize;

            return Math.Max(LabelMinSize, Math.Min(LabelMaxSize, _currentFontSize));
        }

        // One label: the mark, a border and ground behind it, and the three handles that
        // remove, size and move it.
        private LabelVisual BuildLabel(AnnotationOperation note)
        {
            // The border and the ground are drawn here rather than on the mark itself. A
            // text box's own border is under the theme's control, which paints it blue
            // wherever the pointer or the caret is, and the pointer is on a label at
            // exactly the moment its box comes out; a border of our own is the only one
            // that answers to what is asked of it, and the only one that can be taken away
            // again for a screenshot.
            var chrome = new Border { BorderThickness = new Thickness(1) };

            TextBox box;
            var mark = BuildLabelMark(note, out box);

            var apron = BuildLabelApron();
            var mover = BuildLabelMover();
            var bin = BuildLabelBin();
            var grip = BuildLabelGrip();

            var visual = new LabelVisual
            {
                Note = note,
                Chrome = chrome,
                Mark = mark,
                Box = box,
                Bin = bin,
                Grip = grip,
                Mover = mover,
                Apron = apron,
                Tag = note,
                Hover = new DispatcherTimer { Interval = LabelHoverDelay }
            };

            // The apron first and so beneath everything, since it is only there to catch
            // the pointer where nothing else would; then the border, so it lies behind the
            // mark; the mover above the mark, so the top edge grabs rather than typing; and
            // the handles last, above it all.
            visual.Children.Add(apron);
            visual.Children.Add(chrome);
            visual.Children.Add(mark);
            visual.Children.Add(mover);
            visual.Children.Add(bin);
            visual.Children.Add(grip);

            if (box != null)
            {
                // What is typed goes straight onto the record, which is the label itself,
                // so it travels with the specimen and is saved with the project. Nothing
                // else marks it: the history is untouched, so the mark that rides on a
                // history change never comes, and the words would go unsaved unprompted.
                box.TextChanged += (s, e) =>
                {
                    note.Text = box.Text;
                    ProjectSession.MarkChanged();
                };

                box.LostKeyboardFocus += (s, e) =>
                {
                    if (!visual.IsMouseOver) SetLabelArmed(visual, false);
                };
            }

            bin.Click += (s, e) => DeleteLabel(note);

            ApplyLabelSize(visual);
            SetLabelArmed(visual, false);
            AttachLabelGrip(visual);
            AttachLabelMover(visual);

            visual.Hover.Tick += (s, e) =>
            {
                visual.Hover.Stop();
                SetLabelArmed(visual, true);
            };

            // The wait begins when the pointer arrives and is abandoned when it leaves, so
            // resting on a label brings its box out and crossing it does not.
            visual.MouseEnter += (s, e) =>
            {
                if (!visual.Armed) visual.Hover.Start();
            };

            visual.MouseLeave += (s, e) =>
            {
                visual.Hover.Stop();

                // Not while it is being typed in: the pointer is free to wander off the
                // words without the box closing under the caret.
                if (!visual.IsKeyboardFocusWithin) SetLabelArmed(visual, false);
            };

            // A press on a label with its box out is that label's own and must not also
            // read as a click on the picture: the mark has already taken what it needs by
            // the time this runs, so stopping it here costs the label nothing and stops a
            // second label being dropped underneath the first. A press on a resting label
            // is let through, so a label never stands between the pointer and the picture
            // it is written on.
            //
            // The apron is let through as well. It holds the box open on the way to a
            // handle and nothing more; it lies wholly outside the box, so a press on it is
            // a press on the picture, and swallowing it would leave a band beside every
            // label just placed where clicking did nothing at all.
            visual.MouseDown += (s, e) =>
            {
                if (visual.Armed && !ReferenceEquals(e.OriginalSource, visual.Apron))
                    e.Handled = true;
            };

            return visual;
        }

        // The mark: the words, a dot, or a star. The box has no set width, so for words it
        // takes the width of what is typed and the border closes up against them.
        private FrameworkElement BuildLabelMark(AnnotationOperation note, out TextBox box)
        {
            if (note.Kind != AnnotationKind.Text)
            {
                box = null;

                Shape drawn = note.Kind == AnnotationKind.Star
                    ? (Shape)new Polygon()
                    : new Ellipse();

                drawn.Fill = Brushes.Black;
                drawn.HorizontalAlignment = HorizontalAlignment.Left;
                drawn.VerticalAlignment = VerticalAlignment.Top;
                drawn.Margin = new Thickness(LabelMarkPad);
                return drawn;
            }

            box = new TextBox
            {
                Text = note.Text ?? "",
                MinWidth = LabelMinWidth,
                FontFamily = LabelFontFor(note),
                Foreground = Brushes.Black,
                Background = Brushes.Transparent,

                // No border of its own, and a margin in its place, so the words sit
                // exactly where the border around them leaves room for them.
                BorderThickness = new Thickness(0),
                Margin = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            return box;
        }

        /// Brings a label's box out or puts it away, mark and all.
        private void SetLabelArmed(LabelVisual visual, bool armed)
        {
            visual.Armed = armed;
            ShowLabelChrome(visual, armed);

            // Out of the pointer's reach at rest, not merely read-only: a label is part of
            // the picture until it is asked for, and a click on it is a click on the
            // picture underneath.
            visual.Mark.IsHitTestVisible = armed;

            if (visual.Box == null) return;

            visual.Box.IsReadOnly = !armed;
            visual.Box.Focusable = armed;
            visual.Box.Cursor = armed ? Cursors.IBeam : Cursors.Arrow;
        }

        /// Shows or hides everything about a label that is not its mark: the border, the
        /// ground behind it, and the three handles. The border keeps its thickness either
        /// way and is simply drawn in nothing when hidden, so the mark does not shift by a
        /// pixel when the box appears around it.
        private static void ShowLabelChrome(LabelVisual visual, bool show)
        {
            visual.Chrome.BorderBrush = show
                ? new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0x00, 0x00))
                : Brushes.Transparent;

            // Transparent when hidden rather than unset: the pointer has to land on
            // something for the wait to begin, and at rest the mark is out of its reach,
            // so the ground is what catches it. Unset is nothing to hit at all.
            visual.Chrome.Background = show
                ? new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF))
                : Brushes.Transparent;

            var state = show ? Visibility.Visible : Visibility.Collapsed;

            visual.Apron.Visibility = state;
            visual.Mover.Visibility = state;
            visual.Bin.Visibility = state;
            visual.Grip.Visibility = state;
        }

        // Puts the stored size on the mark, and for words sizes the fold to match.
        private void ApplyLabelSize(LabelVisual visual)
        {
            double size = visual.Note.Size;

            // Put right on the record and not merely on screen, so the size the label is
            // drawn at and the size it is saved at are the same one.
            if (double.IsNaN(size) || size < LabelMinSize || size > LabelMaxSize)
            {
                size = StartingLabelSize();
                visual.Note.Size = size;
            }

            // Grown to match the lettering, where the leading above the glyphs is room to
            // spend on it. A dot or a star has only its own narrow pad above it, so a strip
            // grown to match the mark would lie in a band across the top of it.
            visual.Mover.Height = visual.Box == null
                ? LabelMoverHeight
                : Math.Max(LabelMoverHeight, size * LabelMoverInLetters);

            if (visual.Box == null)
            {
                visual.Mark.Width = size;
                visual.Mark.Height = size;

                // A star is redrawn at each size rather than stretched to it: stretching a
                // shape leaves its points blunt at one size and needle-thin at another.
                if (visual.Mark is Polygon star) star.Points = StarPoints(size);
                return;
            }

            visual.Box.FontSize = size;
            visual.Box.MaxWidth = size * LabelWrapAt;
        }

        // A five-pointed star filling the given box, point upwards. The inner radius is the
        // one at which the points meet as they do in a pentagram: any larger and the star
        // is a starfish, any smaller and it is a spider.
        private static PointCollection StarPoints(double size)
        {
            const double InnerRatio = 0.382;   // (3 - root 5) / 2

            double centre = size / 2;
            double inner = centre * InnerRatio;
            var points = new PointCollection(10);

            for (int i = 0; i < 10; i++)
            {
                double radius = i % 2 == 0 ? centre : inner;
                double angle = -Math.PI / 2 + i * Math.PI / 5;

                points.Add(new Point(
                    centre + radius * Math.Cos(angle),
                    centre + radius * Math.Sin(angle)));
            }

            return points;
        }

        // The two handles sit clear of the box so that nothing ever covers the mark, which
        // leaves a gap between them and it. This is that gap, and the reach over the
        // handles themselves: invisible, and out of the pointer's way until the box comes
        // out. Without it the pointer would leave the label on its way to a handle, the
        // label would close, and the handle would be gone before it could be reached.
        private static FrameworkElement BuildLabelApron() =>
            new Border
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, -LabelBinSize, -LabelBinSize, -LabelGripSize),
                Visibility = Visibility.Collapsed
            };

        // The strip along the top edge, which moves the label. Inside the border, above the
        // mark, and faintly shaded so the one place that drags is the one place that looks
        // as though it would.
        private static FrameworkElement BuildLabelMover() =>
            new Border
            {
                Margin = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
                Cursor = Cursors.SizeAll,
                Visibility = Visibility.Collapsed,
                ToolTip = "Drag to move this label."
            };

        // The bin, and below it the grip: a corner each, both outside the box, so neither
        // ever sits over the mark however small the label is drawn.
        private Button BuildLabelBin()
        {
            var bin = new Button
            {
                Width = LabelBinSize,
                Height = LabelBinSize,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Delete this label. Redo brings it back.",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -LabelBinSize, -LabelBinSize, 0),
                Visibility = Visibility.Collapsed,

                // Its own pale ground, since it sits out over the picture where a bare
                // outline would be lost against anything busy.
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
                    CornerRadius = new CornerRadius(3),
                    Child = BuildLabelBinIcon()
                }
            };

            return bin;
        }

        private static Path BuildLabelBinIcon() =>
            new Path
            {
                Stroke = Brushes.Black,
                StrokeThickness = 1.3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse(
                    "M2,4 H16 "                  // lid
                    + "M6.5,4 V2 H11.5 V4 "      // handle
                    + "M3.8,4 L4.8,16.5 H13.2 L14.2,4 "   // body
                    + "M7.3,6.5 V14 M10.7,6.5 V14")       // ribs
            };

        // The corner grip: two short strokes across a corner, the usual mark for one that
        // can be pulled.
        private static FrameworkElement BuildLabelGrip() =>
            new Border
            {
                Width = LabelGripSize,
                Height = LabelGripSize,
                Background = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -LabelGripSize, -LabelGripSize),
                Cursor = Cursors.SizeNWSE,
                Visibility = Visibility.Collapsed,
                ToolTip = "Drag to size this label.",
                Child = new Path
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.4,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M2,11 L11,2 M6.5,11 L11,6.5")
                }
            };

        // Pulling the corner sizes the label: how far the pointer is from the point the
        // label is pinned to, against how far it was when the pull began, so pulling twice
        // as far out doubles the lettering and half way back in halves it. Measured from
        // that point because it is the one thing in the picture the pull cannot move.
        private void AttachLabelGrip(LabelVisual visual)
        {
            double startSize = 0;
            double startReach = 0;

            visual.Grip.MouseLeftButtonDown += (s, e) =>
            {
                startSize = visual.Note.Size;
                startReach = LabelReach(visual, e);

                if (startReach > 1 && visual.Grip.CaptureMouse()) e.Handled = true;
                else startReach = 0;
            };

            visual.Grip.MouseMove += (s, e) =>
            {
                if (startReach <= 0 || !visual.Grip.IsMouseCaptured) return;

                double size = startSize * LabelReach(visual, e) / startReach;
                size = Math.Max(LabelMinSize, Math.Min(LabelMaxSize, size));

                if (Math.Abs(size - visual.Note.Size) < 0.05) return;

                visual.Note.Size = size;
                ApplyLabelSize(visual);

                // A mark straddles the point it names, so how far the box's corner lies
                // from that point changes with the size and the box has to be put back.
                PositionLabel(visual, visual.Note);
                ProjectSession.MarkChanged();
            };

            visual.Grip.MouseLeftButtonUp += (s, e) =>
            {
                if (visual.Grip.IsMouseCaptured) visual.Grip.ReleaseMouseCapture();
            };

            // A capture can be taken away, by another window coming forward say, and the
            // pull has to end with it rather than pick up again the next time the pointer
            // passes over the grip.
            visual.Grip.LostMouseCapture += (s, e) => startReach = 0;
        }

        // Dragging the top strip moves the label. What the pointer travels across the
        // picture is what the label is moved by, read in image pixels, so the mark keeps up
        // with the pointer at every magnification.
        private void AttachLabelMover(LabelVisual visual)
        {
            Point grabbed = new Point();
            double startX = 0;
            double startY = 0;
            bool moving = false;

            visual.Mover.MouseLeftButtonDown += (s, e) =>
            {
                if (!visual.Mover.CaptureMouse()) return;

                grabbed = LabelTransform().CanvasToImage(e.GetPosition(UI_LabelCanvas));
                startX = visual.Note.ImageX;
                startY = visual.Note.ImageY;
                moving = true;
                e.Handled = true;
            };

            visual.Mover.MouseMove += (s, e) =>
            {
                if (!moving || !visual.Mover.IsMouseCaptured) return;

                Point now = LabelTransform().CanvasToImage(e.GetPosition(UI_LabelCanvas));

                visual.Note.ImageX = KeepOnPicture(
                    startX + now.X - grabbed.X, WorkingImage?.PixelWidth ?? 0);

                visual.Note.ImageY = KeepOnPicture(
                    startY + now.Y - grabbed.Y, WorkingImage?.PixelHeight ?? 0);

                PositionLabel(visual, visual.Note);
                ProjectSession.MarkChanged();
            };

            visual.Mover.MouseLeftButtonUp += (s, e) =>
            {
                if (visual.Mover.IsMouseCaptured) visual.Mover.ReleaseMouseCapture();
            };

            visual.Mover.LostMouseCapture += (s, e) => moving = false;
        }

        // A label dragged past the edge of the picture would be out of sight with nothing
        // left to take hold of, so it is stopped at the edge instead.
        private static double KeepOnPicture(double value, double extent) =>
            extent <= 0 ? value : Math.Max(0, Math.Min(extent, value));

        // How far the pointer is from the point a label is pinned to, read on the layer the
        // labels sit on: the zoom is carried by the border above it, so the same pull reads
        // the same at every magnification.
        private double LabelReach(LabelVisual visual, MouseEventArgs e)
        {
            Point at = e.GetPosition(UI_LabelCanvas);
            Point anchor = LabelTransform().ImageToCanvas(
                new Point(visual.Note.ImageX, visual.Note.ImageY));

            double dx = at.X - anchor.X;
            double dy = at.Y - anchor.Y;

            return Math.Sqrt(dx * dx + dy * dy);
        }

        // A label just placed has its handles out and, if it is words, the caret. Done once
        // the label has been laid out, since a box with no size yet has nowhere to put one.
        private void ArmNewLabel(AnnotationOperation note)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() =>
                {
                    foreach (UIElement child in UI_LabelCanvas.Children)
                    {
                        if (!(child is LabelVisual visual)
                            || !ReferenceEquals(visual.Note, note)) continue;

                        SetLabelArmed(visual, true);
                        visual.Box?.Focus();
                        return;
                    }
                }));
        }

        /// Carries the labels through a flip or a rotation of the picture, so each one
        /// stays on the feature it names. The bitmap is turned about the origin and then
        /// brought back to it, which is what the bounds are subtracted for.
        private void MoveLabelsThrough(Transform transform, double oldWidth, double oldHeight)
        {
            if (UndoRedoManager == null || transform == null) return;
            if (oldWidth <= 0 || oldHeight <= 0) return;

            // Bounds from the transform, which is what carries TransformBounds; the matrix
            // behind it moves the points themselves.
            Rect bounds = transform.TransformBounds(new Rect(0, 0, oldWidth, oldHeight));
            Matrix matrix = transform.Value;

            foreach (var note in UndoRedoManager.History.OfType<AnnotationOperation>())
            {
                Point moved = matrix.Transform(new Point(note.ImageX, note.ImageY));
                note.ImageX = moved.X - bounds.X;
                note.ImageY = moved.Y - bounds.Y;
            }
        }

        /// Puts every label's box away for a screenshot and afterwards brings back the ones
        /// that had it out. The border, the ground behind the mark and the handles are
        /// controls rather than part of the figure; the mark is the figure, and it stays.
        /// Only what is drawn is touched, not what can be typed in: a label being written
        /// when the shot is taken keeps its caret.
        private void SyncLabelChrome(bool hideAll)
        {
            if (UI_LabelCanvas == null) return;

            if (hideAll)
            {
                _hiddenForScreenshot.Clear();

                foreach (UIElement child in UI_LabelCanvas.Children)
                {
                    if (!(child is LabelVisual visual) || !visual.Armed) continue;

                    _hiddenForScreenshot.Add(visual);
                    ShowLabelChrome(visual, false);
                }

                return;
            }

            foreach (var visual in _hiddenForScreenshot)
            {
                if (UI_LabelCanvas.Children.Contains(visual)) ShowLabelChrome(visual, true);
            }

            _hiddenForScreenshot.Clear();
        }
    }
}
