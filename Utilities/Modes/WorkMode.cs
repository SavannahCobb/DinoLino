using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    /// <summary>Base class for interactive work modes.</summary>
    public abstract class WorkMode : INotifyPropertyChanged
    {
        #region Identity and lifecycle

        public abstract UserControl CreateControlPanel();

        /// <summary>The tab header used to match this mode to its UI tab.</summary>
        public abstract string TabName { get; }

        /// <summary>True when the mode is ready to begin a new operation.</summary>
        public virtual bool IsStartingNewOperation => CurrentStep == 0;

        /// True for probe-style interactions that inspect or adjust an existing
        /// operation instead of starting a new one.
        public virtual bool IsProbeInteraction => false;

        /// <summary>Index of the current step within the active operation.</summary>
        public int CurrentStep { get; set; } = 0;

        public UndoRedoManager UndoRedoManager { get; set; }

        /// <summary>Shared scale calibration supplied by the main window.</summary>
        public ScaleCalibration Scale { get; set; }

        /// <summary>Shared axis alignment supplied by the main window.</summary>
        public ImageAlignment Alignment { get; set; }

        /// Mapping between image pixels and the canvas the modes draw on, kept current
        /// by the workspace as the image is laid out. A point clicked at one window
        /// size converts through this to the same image pixel it would at any other.
        /// It starts unset — scale zero — so a conversion attempted before the
        /// workspace has supplied one falls back rather than passing canvas pixels off
        /// as image pixels.
        public ViewTransform ImageTransform { get; set; }

        /// <summary>Controls whether previously drawn operations remain visible.</summary>
        public bool SeePreviousOperations { get; set; } = false;

        public WorkMode()
        {
            ElementsToRemove = new ReadOnlyObservableCollection<UIElement>(_elementsToRemove);
        }

        /// <summary>Processes mouse movement in mode-specific coordinates.</summary>
        public virtual Vector2 ProcessMouseMovement(Vector2 mousePos) { return mousePos; }

        /// <summary>Processes a click and returns any UI elements created by that click.</summary>
        public virtual List<UIElement> ProcessClick(Vector2 mousePos) { return null; }

        /// <summary>Clears transient state used while an operation is in progress.</summary>
        public virtual void ResetDrawingState() { }

        /// Returns the mode to a fresh workspace context: refreshes undo/redo state,
        /// clears displayed results, and drops any in-progress drawing.
        public virtual void Reset()
        {
            UpdateUndoRedoState();
            ClearMetadata();
            ResetDrawingState();
        }

        /// <summary>Clears the results the control panel displays.</summary>
        public virtual void ClearMetadata() { }

        #endregion

        #region Cancellable operations

        private CancellationTokenSource _operationCTS;

        public CancellationToken CancellationToken =>
            _operationCTS?.Token ?? CancellationToken.None;

        /// <summary>Starts a new cancellable operation and cancels any previous one.</summary>
        public virtual void BeginOperation()
        {
            CancelCurrentOperation();
            _operationCTS = new CancellationTokenSource();
        }

        /// <summary>Cancels the current operation, if one is active.</summary>
        public virtual void CancelCurrentOperation()
        {
            if (_operationCTS != null)
            {
                if (!_operationCTS.IsCancellationRequested)
                    _operationCTS.Cancel();

                _operationCTS.Dispose();
                _operationCTS = null;
            }
        }

        #endregion

        #region Workspace elements

        /// <summary>Thinnest and thickest line the Line Options window offers.</summary>
        public const double MinLineThickness = 0.5;

        public const double MaxLineThickness = 10.0;

        /// <summary>Thickness a window that has never been told otherwise draws at.</summary>
        public const double DefaultLineThickness = 2.0;

        private Brush _lineColor = Brushes.OrangeRed;
        private double _lineThickness = DefaultLineThickness;

        /// <summary>Current drawing color for newly created elements.</summary>
        public Brush LineColor
        {
            get => _lineColor;
            set
            {
                if (_lineColor != value)
                {
                    _lineColor = value;
                    OnPropertyChanged();
                }
            }
        }

        /// Current stroke width for newly created elements, in canvas pixels. The
        /// guide marks a tool draws in its own fixed weight, since they say where a
        /// measurement will go rather than being part of one.
        public double LineThickness
        {
            get => _lineThickness;
            set
            {
                double clipped = ClipThickness(value);

                if (_lineThickness != clipped)
                {
                    _lineThickness = clipped;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>Holds a thickness inside the range the window offers.</summary>
        public static double ClipThickness(double thickness)
        {
            if (double.IsNaN(thickness)) return DefaultLineThickness;

            return Math.Max(MinLineThickness, Math.Min(MaxLineThickness, thickness));
        }

        /// <summary>Elements drawn by the operation in progress.</summary>
        protected readonly List<UIElement> CurrentOperation = new();

        private readonly ObservableCollection<UIElement> _elementsToRemove = new();
        public ReadOnlyObservableCollection<UIElement> ElementsToRemove { get; }

        /// <summary>Queues an element to be removed from the workspace.</summary>
        public void AddElementsToRemove(UIElement element)
        {
            _elementsToRemove.Add(element);
        }

        /// <summary>Clears the pending removal list.</summary>
        public void ClearElementsToRemove()
        {
            _elementsToRemove.Clear();
        }

        protected Line MakeLine(Vector2 a, Vector2 b) => new()
        {
            Stroke = LineColor,
            StrokeThickness = LineThickness,
            X1 = a.X,
            Y1 = a.Y,
            X2 = b.X,
            Y2 = b.Y,
        };

        /// Bold canvas label in the current line color, offset from the given point.
        protected TextBlock MakeLabel(string text, Vector2 pos, double fontSize = 28,
            double offsetX = 5, double offsetY = 5)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = LineColor,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center
            };

            Canvas.SetLeft(label, pos.X + offsetX);
            Canvas.SetTop(label, pos.Y + offsetY);
            return label;
        }

        /// Filled marker dot in the current line color, centred on the given point.
        protected Ellipse MakeDot(Vector2 pos, double diameter = 8)
        {
            var dot = new Ellipse
            {
                Fill = LineColor,
                Width = diameter,
                Height = diameter
            };

            Canvas.SetLeft(dot, pos.X - diameter / 2);
            Canvas.SetTop(dot, pos.Y - diameter / 2);
            return dot;
        }

        #endregion

        #region History

        public void CommitOperation(WorkOperation operation)
        {
            UndoRedoManager?.Commit(operation);
        }

        /// Commits an operation together with the canvas-space points that define it.
        protected void CommitOperation(WorkOperation operation, IEnumerable<Point> canvasPoints)
        {
            RecordGeometry(operation, canvasPoints);
            CommitOperation(operation);
        }

        /// Stamps the operation with this mode and the accumulated elements, commits
        /// it, and empties the accumulator ready for the next operation.
        protected void CommitCurrentOperation(WorkOperation operation)
        {
            if (operation == null) return;

            operation.SourceMode = this;
            operation.Elements = new List<UIElement>(CurrentOperation);
            CommitOperation(operation);
            CurrentOperation.Clear();
        }

        /// The same, for an operation defined by a handful of clicked points.
        protected void CommitCurrentOperation(WorkOperation operation, params Vector2[] canvasPoints)
        {
            RecordGeometry(operation, canvasPoints);
            CommitCurrentOperation(operation);
        }

        /// The same, for an operation defined by a drawn run of points.
        protected void CommitCurrentOperation(WorkOperation operation, IEnumerable<Vector2> canvasPoints)
        {
            RecordGeometry(operation, canvasPoints);
            CommitCurrentOperation(operation);
        }

        /// Stores the points that define an operation, converted into image pixels.
        /// Called again when an operation's geometry changes after it was committed.
        protected void RecordGeometry(WorkOperation operation, IEnumerable<Point> canvasPoints)
        {
            if (operation == null || canvasPoints == null) return;

            var points = new List<Point>();
            foreach (var point in canvasPoints) points.Add(ToImagePoint(point.X, point.Y));

            operation.ImagePoints = points;
        }

        /// <summary>Stores defining points held as the modes' own vectors.</summary>
        protected void RecordGeometry(WorkOperation operation, IEnumerable<Vector2> canvasPoints)
        {
            if (operation == null) return;

            var points = ToImagePoints(canvasPoints);
            if (points == null) return;

            operation.ImagePoints = points;
        }

        /// Converts a run of canvas-space vectors into image pixels. A run with a
        /// missing point describes nothing, so it yields null rather than a shorter
        /// list that would read as a complete record of something else.
        protected List<Point> ToImagePoints(IEnumerable<Vector2> canvasPoints)
        {
            if (canvasPoints == null) return null;

            var points = new List<Point>();

            foreach (var point in canvasPoints)
            {
                if (point == null) return null;
                points.Add(ToImagePoint(point.X, point.Y));
            }

            return points;
        }

        private bool _canUndo;
        public bool CanUndo
        {
            get => _canUndo;
            private set
            {
                if (_canUndo != value)
                {
                    _canUndo = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _canRedo;
        public bool CanRedo
        {
            get => _canRedo;
            private set
            {
                if (_canRedo != value)
                {
                    _canRedo = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>Synchronizes the mode's undo/redo state with the shared manager.</summary>
        protected void UpdateUndoRedoState()
        {
            CanUndo = UndoRedoManager?.CanUndo == true;
            CanRedo = UndoRedoManager?.CanRedo == true;
        }

        /// Called after undo/redo changes the active history so the mode can refresh
        /// any state derived from that history.
        internal virtual void OnHistoryChanged()
        {
            RecomputeAverages();
        }

        /// <summary>Raises PropertyChanged for the mode's Avg* properties.</summary>
        protected virtual void RecomputeAverages() { }

        #endregion

        #region Measurement space

        // Every stored measurement is held in image pixels. Canvas pixels move when
        // the window is resized, because the image is stretched to fit, so a value
        // stored in them would convert to a different real-world number depending on
        // when it was read. These two turn a freshly drawn canvas measurement into
        // the storable form.

        /// <summary>Converts a canvas-space length into image pixels.</summary>
        protected double ToImageLength(double canvasLength) =>
            Scale?.CanvasToImageLength(canvasLength) ?? canvasLength;

        /// <summary>Converts a canvas-space area into square image pixels.</summary>
        protected double ToImageArea(double canvasArea) =>
            Scale?.CanvasToImageArea(canvasArea) ?? canvasArea;

        /// Converts a canvas-space position into image pixels. The transform carries
        /// the image's own offset within the canvas; before the workspace has supplied
        /// one, the scale alone is the best available answer.
        protected Point ToImagePoint(double canvasX, double canvasY) =>
            ImageTransform.IsValid
                ? ImageTransform.CanvasToImage(new Point(canvasX, canvasY))
                : new Point(ToImageLength(canvasX), ToImageLength(canvasY));

        #endregion

        #region Scaled measurements

        /// Placeholder shown in place of a scaled value when the mode holds no
        /// measurement, or holds one the image calibration cannot convert.
        protected string ScaledPlaceholder =>
            Scale != null && Scale.IsCalibrated ? "N/A" : "Unscaled";

        /// <summary>True when the shared calibration can convert stored measurements.</summary>
        protected bool IsScaleUsable => Scale != null && Scale.IsCalibrated;

        /// Formats an image-pixel length in calibrated units. hasMeasurement is false
        /// before anything has been measured, which yields the placeholder instead.
        protected string FormatScaledLength(double imageLength, bool hasMeasurement = true) =>
            hasMeasurement && IsScaleUsable
                ? $"{Scale.ToUnitsFromImage(imageLength):F2} {Scale.Unit}"
                : ScaledPlaceholder;

        /// Formats an image-pixel area in calibrated square units. hasMeasurement is
        /// false before anything has been measured, which yields the placeholder.
        protected string FormatScaledArea(double imageArea, bool hasMeasurement = true) =>
            hasMeasurement && IsScaleUsable
                ? $"{Scale.ToUnitsAreaFromImage(imageArea):F2} {Scale.Unit}\u00B2"
                : ScaledPlaceholder;

        /// Re-derives every scaled value the mode displays from the image-space
        /// measurements it has stored.
        public virtual void RefreshScalePlaceholders() { }

        #endregion

        #region Averages

        /// Mean of a value series to one decimal place, or "N/A" with no attempts.
        protected static string FormatAverage(IEnumerable<double> values)
        {
            var list = values.ToList();
            if (list.Count == 0) return "N/A";
            return Math.Round(list.Average(), 1).ToString();
        }

        /// Mean of an image-pixel length series in calibrated units, or "N/A" with no
        /// attempts or no calibration.
        protected string FormatScaledLengthAverage(IEnumerable<double> imageLengths)
        {
            var list = imageLengths.ToList();
            if (list.Count == 0 || !IsScaleUsable) return "N/A";
            return $"{Scale.ToUnitsFromImage(list.Average()):F2} {Scale.Unit}";
        }

        /// Mean of an image-pixel area series in calibrated square units, or "N/A"
        /// with no attempts or no calibration.
        protected string FormatScaledAreaAverage(IEnumerable<double> imageAreas)
        {
            var list = imageAreas.ToList();
            if (list.Count == 0 || !IsScaleUsable) return "N/A";
            return $"{Scale.ToUnitsAreaFromImage(list.Average()):F2} {Scale.Unit}\u00B2";
        }

        /// Committed operations of one kind from the live history, empty when no
        /// history is attached.
        protected IEnumerable<TOperation> OperationsOfKind<TOperation>() where TOperation : WorkOperation =>
            UndoRedoManager?.History.OfType<TOperation>() ?? Enumerable.Empty<TOperation>();

        #endregion

        #region Tips

        public Action OnTipChanged;

        protected const string TipUndo =
            "💡 Press 'Ctrl+Z' to undo the current operation, or select 'Undo' in the Edit menu.";
        protected const string TipRedo =
            "💡 Press 'Ctrl+Y' to redo an undone operation, or select 'Redo' in the Edit menu.";
        protected const string TipClear =
            "💡 Press 'Ctrl+C' to clear all operations, or click 'Clear' in the sidebar.";
        protected const string TipOpenImage =
            "💡 Press 'Ctrl+F' to open a new image, or select 'Open Image' in the File menu.";
        protected const string TipZoom =
            "💡 Zoom in or out using the scroll wheel.";
        protected const string TipPan =
            "💡 Press 'Ctrl' and left click to drag the image.";
        protected const string TipHelp =
            "💡 The user guide and software information can be found in the Help menu.";
        protected const string TipToggleTips =
            "💡 Toggle tip visibility in the View menu.";

        /// <summary>Trailer appended by BuildTips.</summary>
        private static readonly string[] CommonTipTail =
        {
            TipUndo, TipRedo, TipClear, TipOpenImage, TipZoom, TipPan, TipHelp, TipToggleTips
        };

        /// Builds a tip list from the mode's own lines followed by the shared trailer.
        protected static string[] BuildTips(params string[] modeTips) =>
            modeTips.Concat(CommonTipTail).ToArray();

        public virtual string[] GetTips() => new[] { string.Empty };

        #endregion

        #region Property change notification

        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}