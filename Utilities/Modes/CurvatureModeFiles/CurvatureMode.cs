using DinoLino.DataTypes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    public class CurvatureMode : WorkMode
    {
        #region Shared Curvature Infrastructure
        //-----BROAD/SHARED CURVATURE SECTION-----//
        public override UserControl CreateControlPanel() => new CurvatureControlPanel(this);
        public override string TabName => "Curvature";
        public override bool IsStartingNewOperation => CurrentStep == 0 || CurrentStep == 3;

        // A probe click measures the existing spline and adds nothing, so the router
        // must not clear the workspace on it.
        public override bool IsProbeInteraction =>
            CurrentMethod == CurvatureMethod.NPointSpline && FindTurningAngleMode;

        public enum CurvatureMethod
        {
            None,
            CircularArc,
            ParabolicArc,
            NPointSpline
        }

        // How the spline's shape is arrived at. The first two take clicked control
        // points; Freehand takes a dragged stroke and reduces it to control points of
        // its own, so all three end up measured by the same code.
        public enum SplineAlgorithm { CatmullRom, Bezier, Freehand }

        private CurvatureMethod _currentMethod = CurvatureMethod.None;
        public CurvatureMethod CurrentMethod
        {
            get => _currentMethod;
            set
            {
                if (!SetField(ref _currentMethod, value)) return;
                OnPropertyChanged(nameof(IsCircularArcSelected));
                OnPropertyChanged(nameof(IsParabolicArcSelected));
                OnPropertyChanged(nameof(IsNPointSplineSelected));
                OnTipChanged?.Invoke();
            }
        }

        public bool IsCircularArcSelected => CurrentMethod == CurvatureMethod.CircularArc;
        public bool IsParabolicArcSelected => CurrentMethod == CurvatureMethod.ParabolicArc;
        public bool IsNPointSplineSelected => CurrentMethod == CurvatureMethod.NPointSpline;

        private Line CurrentUILine = null;

        public void SelectCurvature(string option)
        {
            if (!Enum.TryParse<CurvatureMethod>(option, ignoreCase: true, out var selection))
                return;

            switch (selection)
            {
                case CurvatureMethod.None:
                    SelectNone();
                    break;
                case CurvatureMethod.CircularArc:
                    SelectCircularArc();
                    break;
                case CurvatureMethod.ParabolicArc:
                    SelectParabolicArc();
                    break;
                case CurvatureMethod.NPointSpline:
                    SelectNPointSpline();
                    break;
            }
        }

        public override List<UIElement> ProcessClick(Vector2 mousePos)
        {
            return CurrentMethod switch
            {
                CurvatureMethod.NPointSpline => FindTurningAngleMode
                    ? ProcessFindTurningAngleClick(mousePos)
                    : ProcessSplineClick(mousePos),
                CurvatureMethod.CircularArc => ProcessCircArcClick(mousePos),
                CurvatureMethod.ParabolicArc => ProcessParArcClick(mousePos),
                _ => new List<UIElement>()
            };
        }

        public void SelectNone()
        {
            ExitFindTurningAngle();
            CurrentMethod = CurvatureMethod.None;
            ResetDrawingState();
        }

        public override Vector2 ProcessMouseMovement(Vector2 mousePos)
        {
            // Snap the cursor to the nearest point on the last spline while probing.
            if (FindTurningAngleMode)
            {
                if (TryProjectOntoSpline(mousePos, out Vector2 onCurve, out int idx))
                {
                    _turningIndex = idx;
                    UpdateWindowOval(idx);
                    return onCurve;
                }
                return mousePos;
            }

            if (CurrentMethod == CurvatureMethod.None)
                return mousePos;

            if (CurrentUILine == null) return mousePos;

            Vector2 modifiedPos = mousePos;
            switch (CurrentStep)
            {
                case 1:
                    CurrentUILine.X2 = mousePos.X;
                    CurrentUILine.Y2 = mousePos.Y;
                    break;

                case 2:


                    Vector2 toMouse = mousePos - Midpoint;
                    double newMag = Orthogonal | toMouse;

                    Vector2 newDist = Orthogonal * newMag;

                    CurrentUILine.X2 = Midpoint.X + newDist.X;
                    CurrentUILine.Y2 = Midpoint.Y + newDist.Y;
                    modifiedPos = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);
                    break;

            }
            return modifiedPos;
        }

        public override void ClearMetadata()
        {
            CentralAngleResult = 0;
            AspectRatioResult = 0;
            ChordArcRatioResult = 0;
            XYFunctionResult = "";
            PChordArcRatioResult = 0;
            RiseSpanRatioResult = 0;
            VertexCurvatureResult = 0;
            TurningAngleArcRatioResult = 0;
            SChordArcRatioResult = 0;
            _imageSplineLength = 0;
            _hasImageSplineLength = false;
            _imageCircularRadius = 0;
            _hasImageCircularRadius = false;
            _imageParabolicVertexRadius = 0;
            _hasImageParabolicVertexRadius = false;
            RecomputeScaledResults();
        }

        public override void RefreshScalePlaceholders()
        {
            RecomputeScaledResults();
            OnPropertyChanged(nameof(AvgSplineLengthScaledResult));
            OnPropertyChanged(nameof(AvgCircularRadiusScaledResult));
            OnPropertyChanged(nameof(AvgParabolicRadiusScaledResult));
        }

        // Image-space length of the displayed spline, kept so the scaled row can be
        // re-derived whenever the calibration changes or an undo restores a different
        // spline.
        private double _imageSplineLength;
        private bool _hasImageSplineLength;

        private double _imageCircularRadius;
        private bool _hasImageCircularRadius;

        private double _imageParabolicVertexRadius;
        private bool _hasImageParabolicVertexRadius;

        private string _circularRadiusScaledResult = "Unscaled";
        public string CircularRadiusScaledResult
        {
            get => _circularRadiusScaledResult;
            set => SetField(ref _circularRadiusScaledResult, value);
        }

        private string _parabolicRadiusScaledResult = "Unscaled";
        public string ParabolicRadiusScaledResult
        {
            get => _parabolicRadiusScaledResult;
            set => SetField(ref _parabolicRadiusScaledResult, value);
        }

        /// <summary>Restores the image-space radius behind the circular arc's scaled row.</summary>
        public void RestoreCircularArcRadius(double imageRadius)
        {
            _imageCircularRadius = imageRadius;
            _hasImageCircularRadius = true;
            RecomputeScaledResults();
        }

        /// <summary>Restores the image-space radius behind the parabola's scaled row.</summary>
        public void RestoreParabolicVertexRadius(double imageRadius)
        {
            _imageParabolicVertexRadius = imageRadius;
            _hasImageParabolicVertexRadius = true;
            RecomputeScaledResults();
        }

        private void RecomputeScaledResults()
        {
            SplineLengthScaledResult = FormatScaledLength(_imageSplineLength, _hasImageSplineLength);
            CircularRadiusScaledResult = FormatScaledLength(_imageCircularRadius, _hasImageCircularRadius);
            ParabolicRadiusScaledResult =
                FormatScaledLength(_imageParabolicVertexRadius, _hasImageParabolicVertexRadius);
        }

        /// <summary>Restores the image-space length behind the scaled row.</summary>
        public void RestoreScaledMeasurements(double imageLength)
        {
            _imageSplineLength = imageLength;
            _hasImageSplineLength = true;
            RecomputeScaledResults();
        }

        // Resets the in-progress drawing only. Does NOT exit FindTurningAngleMode:
        // that is toggled explicitly (see ExitFindTurningAngle).
        public override void ResetDrawingState()
        {
            CurrentStep = 0;
            CurrentUILine = null;
            CurrentOperation.Clear();
            _splinePoints.Clear();
            _splinePreview = null;
            _freehandDrawing = false;
            _freehandPreview = null;
            _freehandStroke.Clear();
        }

        // Leaves probe mode (removes the oval, clears the readout).
        private void ExitFindTurningAngle()
        {
            if (FindTurningAngleMode) FindTurningAngleMode = false;
        }

        public override void Reset()
        {
            base.Reset();
            _lastSplineDense = null;
            FindTurningAngleDisplay = "";
        }
        #endregion

        #region 3-Point Arc Section
        //-----THREE-POINT ARC SECTION-----//

        public Vector2 PointA;
        public Vector2 PointB;
        public Vector2 Midpoint;
        public Vector2 Orthogonal;
        public Vector2 PointC;
        public Vector2 Intersection;
        public Vector2 ACMid;
        public Vector2 BCMid;

        private void StartChord(Vector2 mousePos, List<UIElement> outputElements)
        {
            PointA = new Vector2(mousePos.X, mousePos.Y);
            CurrentUILine = MakeLine(mousePos, mousePos);
            outputElements.Add(CurrentUILine);
            CurrentOperation.Add(CurrentUILine);
            CurrentStep++;
        }

        private void FinishChord(Vector2 mousePos, List<UIElement> outputElements)
        {
            CurrentUILine.X2 = mousePos.X;
            CurrentUILine.Y2 = mousePos.Y;
            PointB = mousePos;
        }

        private void StartBisector(Vector2 mousePos, List<UIElement> outputElements)
        {
            Midpoint = (PointA + PointB) * 0.5;
            CurrentUILine = MakeLine(Midpoint, Midpoint);
            outputElements.Add(CurrentUILine);
            CurrentOperation.Add(CurrentUILine);

            Vector3 p1 = new Vector3(PointA.X, PointA.Y, 1);
            Vector3 p2 = new Vector3(PointB.X, PointB.Y, 1);
            Orthogonal = (p1 ^ p2).ToVector2();
            Orthogonal.Normalize();

            CurrentStep++;
        }


        #region Circular Arc Section
        //-----CIRCULAR ARCS-----//

        // Every circular-arc output is a ratio of two canvas lengths or an angle
        // between them, so none of them changes with the view ratio and none needs
        // converting to image space.

        private double _chordArcRatioResult;
        public double ChordArcRatioResult
        {
            get => _chordArcRatioResult;
            set => SetField(ref _chordArcRatioResult, value);
        }

        private double _centralAngleResult;
        public double CentralAngleResult
        {
            get => _centralAngleResult;
            set => SetField(ref _centralAngleResult, value);
        }

        private double _aspectRatioResult;
        public double AspectRatioResult
        {
            get => _aspectRatioResult;
            set => SetField(ref _aspectRatioResult, value);
        }

        public void SelectCircularArc()
        {
            ExitFindTurningAngle();
            CurrentMethod = CurvatureMethod.CircularArc;
            CurrentStep = 0;
            ResetDrawingState();
        }

        private List<UIElement> ProcessCircArcClick(Vector2 mousePos)
        {
            List<UIElement> outputElements = new List<UIElement>();
            ClearElementsToRemove();

            switch (CurrentStep)
            {
                case 0: // Start the first chord

                    StartChord(mousePos, outputElements);
                    break;

                case 1: // End the first chord and start the bisector line 

                    FinishChord(mousePos, outputElements);
                    StartBisector(mousePos, outputElements);
                    break;

                case 2: // Send Bisector line, calculate all remaining POIs, and calculate the final results.

                    PointC = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);

                    ACMid = (PointA + PointC) * 0.5;
                    BCMid = (PointB + PointC) * 0.5;


                    Vector2 Ray13 = (new Vector3(PointA.X, PointA.Y, 1) ^ new Vector3(PointC.X, PointC.Y, 1)).ToVector2();
                    Ray13.Normalize();

                    Vector2 Ray23 = (new Vector3(PointB.X, PointB.Y, 1) ^ new Vector3(PointC.X, PointC.Y, 1)).ToVector2();
                    Ray23.Normalize();

                    double dx = BCMid.X - ACMid.X;
                    double dy = BCMid.Y - ACMid.Y;
                    double det = Ray23 ^ Ray13;

                    if (Math.Abs(det) <= 0.00001)
                    {
                        //don't allow 0 height, just ignore the click and try again
                        break;
                    }

                    double u = (dy * Ray23.X - dx * Ray23.Y) / det;

                    Vector2 offset = Ray13 * u;

                    Intersection = ACMid + offset;

                    double radius = (PointA - Intersection).Magnitude();
                    var circularArc = MakeCircularArc(Intersection, PointA, PointB, radius);

                    var thetaLabel = MakeLabel("\u03B8", Intersection, 22, -7, -30);

                    outputElements.Add(circularArc);
                    CurrentOperation.Add(circularArc);
                    outputElements.Add(thetaLabel);
                    CurrentOperation.Add(thetaLabel);

                    CurrentUILine = null;

                    var line5 = MakeLine(PointA, Intersection);
                    var line6 = MakeLine(PointB, Intersection);

                    outputElements.Add(line5);
                    outputElements.Add(line6);

                    CurrentOperation.Add(line5);
                    CurrentOperation.Add(line6);

                    CalculateCircularArcResults();

                    CurrentStep++;

                    CommitCurrentOperation(new CircularArcOperation
                    {
                        OperationKind = "Circular Arc",
                        CentralAngle = CentralAngleResult,
                        AspectRatio = AspectRatioResult,
                        ChordArcRatio = ChordArcRatioResult,
                        RadiusImagePixels = _imageCircularRadius
                    }, PointA, PointB, PointC);

                    break;
                case 3:
                    ResetDrawingState();
                    PointA = new Vector2(mousePos.X, mousePos.Y);
                    CurrentUILine = MakeLine(mousePos, mousePos);
                    outputElements.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep = 1;
                    break;
            }

            return outputElements;
        }

        private Path MakeCircularArc(Vector2 center, Vector2 start, Vector2 end, double radius)
        {
            double crossProduct = (PointC.X - start.X) * (end.Y - start.Y) - (PointC.Y - start.Y) * (end.X - start.X);
            SweepDirection direction = crossProduct > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

            // Arc exceeds 180° when the centre lies inside triangle ABC.
            bool isLargeArc = GeometryCalculations.IsPointInTriangle(center, start, end, PointC);

            var figure = new PathFigure();
            figure.StartPoint = new Point(start.X, start.Y);

            var arc = new ArcSegment
            {
                Point = new Point(end.X, end.Y),
                Size = new Size(radius, radius),
                RotationAngle = 0,
                IsLargeArc = isLargeArc,
                SweepDirection = direction,
                IsStroked = true
            };

            figure.Segments.Add(arc);

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return new Path
            {
                Stroke = this.LineColor,
                StrokeThickness = this.LineThickness,
                Data = geometry
            };
        }

        private void CalculateCircularArcResults()
        {
            CentralAngleResult = GeometryCalculations.CentralAngle(PointA, PointB, PointC, Intersection);
            double chordLength = (PointB - PointA).Magnitude();
            double bisectorLength = (PointC - Midpoint).Magnitude();
            AspectRatioResult = GeometryCalculations.CircularArcAspectRatio(chordLength, bisectorLength);
            double radius = (PointA - Intersection).Magnitude();
            double arcLength = GeometryCalculations.CircularArcLength(radius, CentralAngleResult);
            ChordArcRatioResult = GeometryCalculations.ChordArcRatio(chordLength, arcLength);

            _imageCircularRadius = ToImageLength(radius);
            _hasImageCircularRadius = true;
            RecomputeScaledResults();
        }
        #endregion

        #region Parabolic Arc Section
        //-----PARABOLIC ARCS-----//

        // The fitted parabola is solved in a chord-normalized local basis, so its
        // coefficients and every metric derived from them are already independent of
        // the view ratio.

        private double ParabolaA;
        private double ParabolaB;
        private double ParabolaC;

        private string _xyFunctionResult;
        public string XYFunctionResult
        {
            get => _xyFunctionResult;
            set => SetField(ref _xyFunctionResult, value);
        }

        private double _pChordArcRatioResult;
        public double PChordArcRatioResult
        {
            get => _pChordArcRatioResult;
            set => SetField(ref _pChordArcRatioResult, value);
        }

        private double _riseSpanRatioResult;
        public double RiseSpanRatioResult
        {
            get => _riseSpanRatioResult;
            set => SetField(ref _riseSpanRatioResult, value);
        }

        private double _vertexCurvatureResult;
        public double VertexCurvatureResult
        {
            get => _vertexCurvatureResult;
            set => SetField(ref _vertexCurvatureResult, value);
        }

        public void SelectParabolicArc()
        {
            ExitFindTurningAngle();
            CurrentMethod = CurvatureMethod.ParabolicArc;
            CurrentStep = 0;
            ResetDrawingState();
        }

        private List<UIElement> ProcessParArcClick(Vector2 mousePos)
        {
            List<UIElement> outputElements = new List<UIElement>();
            ClearElementsToRemove();
            switch (CurrentStep)
            {
                case 0: // Start the first chord

                    StartChord(mousePos, outputElements);
                    break;

                case 1: // End the first chord and start the bisector line 

                    FinishChord(mousePos, outputElements);
                    StartBisector(mousePos, outputElements);
                    break;

                case 2: // finish Bisector line, calculate all remaining POIs, and calculate the final results.

                    PointC = new Vector2(CurrentUILine.X2, CurrentUILine.Y2);

                    var parabola = MakeParabolicArc(PointA, PointB, PointC);

                    outputElements.Add(parabola);
                    CurrentOperation.Add(parabola);

                    CalculateParabolicArcResults();

                    CurrentStep++;

                    CommitCurrentOperation(new ParabolaOperation
                    {
                        OperationKind = "Parabolic Arc",
                        XYFunction = XYFunctionResult,
                        RiseSpanRatio = RiseSpanRatioResult,
                        PChordArcRatio = PChordArcRatioResult,
                        VertexCurvature = VertexCurvatureResult,
                        VertexRadiusImagePixels = _imageParabolicVertexRadius
                    }, PointA, PointB, PointC);

                    break;
                case 3:
                    ResetDrawingState();
                    PointA = new Vector2(mousePos.X, mousePos.Y);
                    CurrentUILine = MakeLine(mousePos, mousePos);
                    outputElements.Add(CurrentUILine);
                    CurrentOperation.Add(CurrentUILine);
                    CurrentStep = 1;
                    break;
            }

            return outputElements;
        }

        private Path MakeParabolicArc(Vector2 pointA, Vector2 pointB, Vector2 pointC)
        {
            if (!GeometryCalculations.BuildLocalBasis(pointA, pointB, out Vector2 xAxis, out Vector2 yAxis, out double chordLength))
                return null;

            Vector2 cDelta = pointC - pointA;
            Vector2 cL = new Vector2(
                (cDelta | xAxis) / chordLength,
                (cDelta | yAxis) / chordLength
            );

            (ParabolaA, ParabolaB, ParabolaC) = GeometryCalculations.SolveParabola(0, 0, 1, 0, cL.X, cL.Y);

            if (ParabolaA == 0 && ParabolaB == 0 && ParabolaC == 0)
                return null;

            List<Vector2> worldPoints = SampleParabolaWorldPoints(pointA, xAxis, yAxis, chordLength, 64);

            PathFigure figure = new PathFigure { StartPoint = new Point(worldPoints[0].X, worldPoints[0].Y), IsClosed = false };
            PolyLineSegment segment = new PolyLineSegment();
            for (int i = 1; i < worldPoints.Count; i++)
                segment.Points.Add(new Point(worldPoints[i].X, worldPoints[i].Y));
            figure.Segments.Add(segment);

            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return new Path { Data = geometry, Stroke = this.LineColor, StrokeThickness = this.LineThickness };
        }

        private List<Vector2> SampleParabolaWorldPoints(Vector2 origin, Vector2 xAxis, Vector2 yAxis, double chordLength, int count)
        {
            var points = new List<Vector2>(count + 1);
            for (int i = 0; i <= count; i++)
            {
                double t = (double)i / count;
                double yNorm = ParabolaA * t * t + ParabolaB * t + ParabolaC;
                points.Add(origin + new Vector2(
                    xAxis.X * t * chordLength + yAxis.X * yNorm * chordLength,
                    xAxis.Y * t * chordLength + yAxis.Y * yNorm * chordLength));
            }
            return points;
        }

        private void CalculateParabolicArcResults()
        {
            double pChordLength = (PointB - PointA).Magnitude();
            double rise = (PointC - Midpoint).Magnitude();

            RiseSpanRatioResult = GeometryCalculations.RiseSpanRatio(rise, pChordLength);
            VertexCurvatureResult = GeometryCalculations.ParabolaVertexCurvature(ParabolaA);

            XYFunctionResult = $"y = {ParabolaA:F3}x² + {ParabolaB:F3}x + {ParabolaC:F3}";

            if (!GeometryCalculations.BuildLocalBasis(PointA, PointB, out Vector2 xAxis, out Vector2 yAxis, out double chordLength))
                return;
            List<Vector2> worldPoints = SampleParabolaWorldPoints(PointA, xAxis, yAxis, chordLength, 64);
            double arcLength = GeometryCalculations.ArcLength(worldPoints);

            PChordArcRatioResult = GeometryCalculations.ChordArcRatio(pChordLength, arcLength);
            _imageParabolicVertexRadius =
                ToImageLength(GeometryCalculations.ParabolaVertexRadius(ParabolaA, pChordLength));
            _hasImageParabolicVertexRadius = true;
            RecomputeScaledResults();
        }
        #endregion
        #endregion

        #region n-point spline section
        //-----N-POINT SPLINE SECTION-----//
        private List<Vector2> _splinePoints = new List<Vector2>();
        private UIElement _splinePreview = null;

        private SplineAlgorithm _splineAlgorithm = SplineAlgorithm.CatmullRom;
        public SplineAlgorithm CurrentSplineAlgorithm
        {
            get => _splineAlgorithm;
            set
            {
                if (!SetField(ref _splineAlgorithm, value)) return;
                OnPropertyChanged(nameof(CurrentSplineAlgorithm));
                OnPropertyChanged(nameof(IsCatmullRomSelected));
                OnPropertyChanged(nameof(IsBezierSelected));
                OnPropertyChanged(nameof(IsFreehandSelected));
                OnTipChanged?.Invoke();
                ResetDrawingState(); // switching algorithm mid-draw starts fresh
            }
        }

        public bool IsCatmullRomSelected
        {
            get => _splineAlgorithm == SplineAlgorithm.CatmullRom;
            set { if (value) CurrentSplineAlgorithm = SplineAlgorithm.CatmullRom; }
        }

        public bool IsBezierSelected
        {
            get => _splineAlgorithm == SplineAlgorithm.Bezier;
            set { if (value) CurrentSplineAlgorithm = SplineAlgorithm.Bezier; }
        }

        public bool IsFreehandSelected
        {
            get => _splineAlgorithm == SplineAlgorithm.Freehand;
            set { if (value) CurrentSplineAlgorithm = SplineAlgorithm.Freehand; }
        }

        private double _turningAngleArcRatioResult;
        public double TurningAngleArcRatioResult
        {
            get => _turningAngleArcRatioResult;
            set
            {
                _turningAngleArcRatioResult = value;
                OnPropertyChanged(nameof(TurningAngleArcRatioResult));
            }
        }

        private double _sChordArcRatioResult;
        public double SChordArcRatioResult
        {
            get => _sChordArcRatioResult;
            set
            {
                _sChordArcRatioResult = value;
                OnPropertyChanged(nameof(SChordArcRatioResult));
            }
        }

        private string _splineLengthScaledResult = "Unscaled";
        public string SplineLengthScaledResult
        {
            get => _splineLengthScaledResult;
            set => SetField(ref _splineLengthScaledResult, value);
        }

        private List<UIElement> ProcessSplineClick(Vector2 mousePos)
        {
            List<UIElement> output = new List<UIElement>();
            ClearElementsToRemove();

            // New spline: drop the retained probe target (committed visuals stay).
            if (_splinePoints.Count == 0)
                _lastSplineDense = null;

            _splinePoints.Add(mousePos);

            var dot = MakeDot(mousePos);
            CurrentOperation.Add(dot);
            output.Add(dot);

            if (_splinePoints.Count >= 2)
            {
                if (_splinePreview != null)
                {
                    AddElementsToRemove(_splinePreview);
                    CurrentOperation.Remove(_splinePreview);
                }

                _splinePreview = _splineAlgorithm == SplineAlgorithm.Bezier
                    ? MakeSchneiderBezierPath(_splinePoints)
                    : MakeCatmullRomPath(_splinePoints);
                CurrentOperation.Add(_splinePreview);
                output.Add(_splinePreview);
            }

            return output;
        }

        public void SelectNPointSpline()
        {
            // Don't reset if a probe-ready spline exists: reaching the
            // Find-Turning-Angle control re-invokes this and must not discard it.
            bool alreadySplineWithFinished =
                CurrentMethod == CurvatureMethod.NPointSpline
                && _lastSplineDense != null && _lastSplineDense.Count >= 3;

            CurrentMethod = CurvatureMethod.NPointSpline;
            _splineAlgorithm = SplineAlgorithm.CatmullRom;
            OnPropertyChanged(nameof(IsCatmullRomSelected));
            OnPropertyChanged(nameof(IsBezierSelected));
            OnPropertyChanged(nameof(IsFreehandSelected));

            if (!alreadySplineWithFinished)
                ResetDrawingState();
        }

        // True when there's an in-progress spline ready to finalize with Enter.
        public bool CanFinalizeSpline =>
            CurrentMethod == CurvatureMethod.NPointSpline
            && _splineAlgorithm != SplineAlgorithm.Freehand   // a stroke ends on release, not on Enter
            && !FindTurningAngleMode
            && _splinePoints.Count >= 3;

        // Commits the spline and returns its elements (Enter key, wired in MainWindow).
        public List<UIElement> FinalizeSpline()
        {
            if (CurrentMethod != CurvatureMethod.NPointSpline)
                return new List<UIElement>();

            if (FindTurningAngleMode)
                return new List<UIElement>();   // Enter is a no-op while probing

            if (_splinePoints.Count < 3)
                return new List<UIElement>();   // not enough points yet; keep what's there

            List<Vector2> splinePointsDense = _splineAlgorithm == SplineAlgorithm.Bezier
                    ? SplineFitting.GetSchneiderBezierPoints(_splinePoints, 50)
                    : SplineFitting.GetCatmullRomPoints(_splinePoints, 50);

            return CommitSpline(_splinePoints, splinePointsDense,
                _splineAlgorithm == SplineAlgorithm.Bezier
                    ? "n-Point Bezier Spline"
                    : "n-Point Catmull-Rom Spline");
        }

        /// Measures a finished spline and records it. Every route into the mode —
        /// clicked points or a dragged stroke — ends here, so a freehand curve and a
        /// clicked one are the same numbers arrived at the same way.
        private List<UIElement> CommitSpline(
            List<Vector2> controlPoints, List<Vector2> densePoints, string operationKind)
        {
            _lastSplineDense = densePoints;   // keep for the Find-turning-angle tool

            double imageLength = ToImageLength(GeometryCalculations.ArcLength(densePoints));
            _imageSplineLength = imageLength;
            _hasImageSplineLength = true;
            RecomputeScaledResults();

            // Turn/Length carries a length in its denominator, so it is measured
            // against the image-pixel length like everything else that is stored.
            double totalTurning = GeometryCalculations.SumTurningAnglesOpen(densePoints);
            TurningAngleArcRatioResult = Math.Round(
                imageLength > 1e-5 ? totalTurning / imageLength : 0, 1);

            SChordArcRatioResult = Math.Round(CalculateSChordArcRatio(densePoints, controlPoints), 1);

            // Captured before the commit, which empties the accumulator.
            var output = new List<UIElement>(CurrentOperation);

            CommitCurrentOperation(new SplineOperation
            {
                OperationKind = operationKind,
                TurningAngleArcRatio = TurningAngleArcRatioResult,
                SChordArcRatio = SChordArcRatioResult,
                SplineLengthImagePixels = imageLength,

                // The curve is what was measured and what ImagePoints keeps; these are
                // the points it was drawn through.
                ControlImagePoints = ToImagePoints(controlPoints) ?? new List<Point>()
            }, densePoints);

            _splinePoints.Clear();
            _splinePreview = null;

            return output;
        }

        // ---- Freehand spline ----
        // A stroke dragged along the curve instead of clicked point by point. The raw
        // stroke is not quite the curve: a hand shakes, and SumTurningAnglesOpen counts
        // every tremor as a turn. So the stroke is resampled to even spacing, lightly
        // smoothed, and reduced to control points, and a centripetal Catmull-Rom is run
        // through those. How lightly is the user's call — see FreehandSmoothing. The
        // default errs towards the drawn path, because a curve that has wandered off
        // the contour is measuring the wrong thing however tidy its numbers look.

        private readonly List<Vector2> _freehandStroke = new List<Vector2>();
        private Polyline _freehandPreview = null;
        private bool _freehandDrawing = false;

        // Closer samples than this are the mouse reporting, not the hand moving.
        private const double FreehandMinSpacing = 2.0;

        // Drawing speed varies, so raw samples bunch up wherever the hand slowed.
        // Even spacing first, in canvas pixels, so the smoothing below covers the same
        // length of curve everywhere along it.
        private const double FreehandResampleStep = 4.0;

        // How much of what the hand did counts as signal, 0-10 from the panel. There is
        // no correct answer to that in general: it depends on how much real detail the
        // contour carries at the magnification being worked at, which is the user's
        // judgement and not something this code can infer. Low keeps the drawn path,
        // noise and all; high trades detail away for a calmer curve.
        private double _freehandSmoothing = 2.0;
        public double FreehandSmoothing
        {
            get => _freehandSmoothing;
            set => SetField(ref _freehandSmoothing, Math.Max(0, Math.Min(10, value)));
        }

        // Gaussian width in resampled samples. This is the only step that moves a point
        // off the traced path, so it is kept small: at the default it spans about 4 px
        // of curve. Measured on contours carrying real detail, a wide one erases the
        // detail outright — at sigma 4 a crenulated edge lost 10 px of position and
        // 11% of its length.
        private double FreehandSmoothSigma => _freehandSmoothing * 0.5;

        // Douglas-Peucker tolerance, in canvas pixels. Never zero: some thinning is
        // needed or every sample becomes a control point and the spline chases the
        // noise between them. Unlike the Gaussian, this only ever DISCARDS points, so
        // the ones it keeps still sit exactly where the user drew.
        private double FreehandSimplifyEpsilon => 2.0 + _freehandSmoothing;

        // Below this a drag was a stray click rather than a traced curve.
        private const int FreehandMinStrokePoints = 8;

        /// <summary>True when a left-drag should draw a freehand spline.</summary>
        public bool FreehandSplineReady =>
            CurrentMethod == CurvatureMethod.NPointSpline
            && _splineAlgorithm == SplineAlgorithm.Freehand
            && !FindTurningAngleMode;

        /// <summary>True between the press and the release of a freehand stroke.</summary>
        public bool IsFreehandStrokeActive => _freehandDrawing;

        /// Starts a stroke and returns the live preview to put on the canvas. The
        /// preview is mutated in place as the drag goes on, so it is added once.
        public List<UIElement> BeginFreehandStroke(Vector2 mousePos)
        {
            var output = new List<UIElement>();
            if (!FreehandSplineReady) return output;

            // A new curve supersedes whatever the probe tool was pointing at.
            _lastSplineDense = null;
            _splinePoints.Clear();
            CurrentOperation.Clear();
            ClearElementsToRemove();

            _freehandStroke.Clear();
            _freehandStroke.Add(mousePos);
            _freehandDrawing = true;
            CurrentStep = 1;

            _freehandPreview = new Polyline
            {
                Stroke = this.LineColor,
                StrokeThickness = this.LineThickness
            };
            _freehandPreview.Points.Add(new Point(mousePos.X, mousePos.Y));

            CurrentOperation.Add(_freehandPreview);
            output.Add(_freehandPreview);
            return output;
        }

        /// <summary>Extends the stroke while the button is held.</summary>
        public void ProcessFreehandDrag(Vector2 mousePos)
        {
            if (!_freehandDrawing || _freehandPreview == null) return;

            Vector2 last = _freehandStroke[_freehandStroke.Count - 1];
            Vector2 step = mousePos - last;
            if (step.Magnitude() < FreehandMinSpacing) return;

            _freehandStroke.Add(mousePos);
            _freehandPreview.Points.Add(new Point(mousePos.X, mousePos.Y));
        }

        /// Ends the stroke, replaces it with the smoothed spline, and records the
        /// measurements. Returns the elements to put on the canvas.
        public List<UIElement> FinishFreehandStroke()
        {
            var output = new List<UIElement>();
            if (!_freehandDrawing) return output;

            _freehandDrawing = false;
            CurrentStep = 0;

            List<Vector2> control = StrokeToControlPoints(_freehandStroke);

            // Too short, or too straight to have three points left: nothing to measure.
            if (_freehandStroke.Count < FreehandMinStrokePoints || control.Count < 3)
            {
                CancelFreehandStroke();
                return output;
            }

            // The traced stroke was only ever a preview. What gets measured is the
            // smoothed curve, so that is what stays on screen.
            if (_freehandPreview != null)
            {
                AddElementsToRemove(_freehandPreview);
                CurrentOperation.Remove(_freehandPreview);
                _freehandPreview = null;
            }

            var curve = MakeCatmullRomPath(control);
            if (curve == null)
            {
                CancelFreehandStroke();
                return output;
            }

            CurrentOperation.Add(curve);

            _splinePoints.Clear();
            _splinePoints.AddRange(control);
            _freehandStroke.Clear();

            return CommitSpline(control, SplineFitting.GetCatmullRomPoints(control, 50),
                "Freehand Spline");
        }

        /// <summary>Abandons an in-progress stroke and takes its preview with it.</summary>
        public void CancelFreehandStroke()
        {
            _freehandDrawing = false;

            if (_freehandPreview != null)
            {
                AddElementsToRemove(_freehandPreview);
                CurrentOperation.Remove(_freehandPreview);
                _freehandPreview = null;
            }

            _freehandStroke.Clear();
            _splinePoints.Clear();
            CurrentStep = 0;
        }

        /// Reduces a traced stroke to the control points a user would have clicked:
        /// even spacing, then smoothing to take out the tremor, then simplification.
        private List<Vector2> StrokeToControlPoints(List<Vector2> stroke)
        {
            List<Vector2> even = ResampleByArcLength(stroke, FreehandResampleStep);
            List<Vector2> smoothed = GaussianSmooth(even, FreehandSmoothSigma);

            var asPoints = new List<Point>(smoothed.Count);
            foreach (Vector2 v in smoothed)
                asPoints.Add(new Point(v.X, v.Y));

            List<Point> simplified = GeometryCalculations.DouglasPeucker(asPoints, FreehandSimplifyEpsilon);

            var control = new List<Vector2>(simplified.Count);
            foreach (Point p in simplified)
                control.Add(new Vector2(p));

            return control;
        }

        /// <summary>Walks the stroke and emits a point every 'step' pixels of it.</summary>
        private static List<Vector2> ResampleByArcLength(List<Vector2> pts, double step)
        {
            var result = new List<Vector2>();
            if (pts == null || pts.Count == 0 || step <= 0) return result;
            if (pts.Count == 1) { result.Add(pts[0]); return result; }

            var dist = new double[pts.Count];
            for (int i = 1; i < pts.Count; i++)
                dist[i] = dist[i - 1] + (pts[i] - pts[i - 1]).Magnitude();

            double total = dist[pts.Count - 1];
            if (total < step)
            {
                result.Add(pts[0]);
                result.Add(pts[pts.Count - 1]);
                return result;
            }

            int seg = 0;
            for (double d = 0; d <= total; d += step)
            {
                while (seg < pts.Count - 2 && dist[seg + 1] < d) seg++;

                double span = dist[seg + 1] - dist[seg];
                double t = span > 1e-12 ? (d - dist[seg]) / span : 0;
                if (t < 0) t = 0; else if (t > 1) t = 1;

                result.Add(pts[seg] + (pts[seg + 1] - pts[seg]) * t);
            }

            Vector2 last = pts[pts.Count - 1];
            if ((last - result[result.Count - 1]).Magnitude() > 1e-9)
                result.Add(last);

            return result;
        }

        /// Gaussian blur along the sequence of points. Ends are clamped rather than
        /// wrapped or zeroed, so the curve keeps the endpoints the user drew.
        private static List<Vector2> GaussianSmooth(List<Vector2> pts, double sigma)
        {
            var result = new List<Vector2>(pts.Count);
            if (pts.Count < 3 || sigma <= 0)
            {
                result.AddRange(pts);
                return result;
            }

            int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
            var weights = new double[radius * 2 + 1];
            double norm = 0;
            for (int k = -radius; k <= radius; k++)
            {
                double weight = Math.Exp(-(k * k) / (2.0 * sigma * sigma));
                weights[k + radius] = weight;
                norm += weight;
            }

            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                double x = 0, y = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int j = Math.Min(n - 1, Math.Max(0, i + k));
                    double weight = weights[k + radius];
                    x += pts[j].X * weight;
                    y += pts[j].Y * weight;
                }
                result.Add(new Vector2(x / norm, y / norm));
            }

            return result;
        }

        // ---- Find turning angle (probe the most recent spline) ----

        // Dense samples of the last spline, retained for the probe tool.
        private List<Vector2> _lastSplineDense = null;

        // Index on _lastSplineDense the oval is currently centred on.
        private int _turningIndex = 0;

        // Half-width of the probed section, in dense-polyline samples.
        private int _turningAngleWindow = 5;
        public int TurningAngleWindow
        {
            get => _turningAngleWindow;
            set
            {
                int clamped = Math.Max(1, Math.Min(50, value));
                if (!SetField(ref _turningAngleWindow, clamped)) return;
                if (FindTurningAngleMode && _lastSplineDense != null && _lastSplineDense.Count >= 3)
                {
                    UpdateWindowOval(_turningIndex);
                    FindTurningAngleDisplay =
                        FormatTurningReadout(_lastSplineDense, _turningIndex, _turningAngleWindow);
                }
            }
        }

        private string _findTurningAngleDisplay = "";
        public string FindTurningAngleDisplay
        {
            get => _findTurningAngleDisplay;
            set => SetField(ref _findTurningAngleDisplay, value);
        }

        private bool _findTurningAngleMode = false;
        public bool FindTurningAngleMode
        {
            get => _findTurningAngleMode;
            set
            {
                // No spline to probe: refuse and snap the CheckBox back.
                if (value && (_lastSplineDense == null || _lastSplineDense.Count < 3))
                {
                    OnPropertyChanged(nameof(FindTurningAngleMode));
                    return;
                }

                if (!SetField(ref _findTurningAngleMode, value)) return;
                if (value) BeginFindTurningAngle();
                else TurningWindowClear?.Invoke(_windowOval);
                OnTipChanged?.Invoke();
            }
        }

        // MainWindow wires these to add/remove the oval on the canvas.
        public event Action<UIElement> TurningWindowReady;
        public event Action<UIElement> TurningWindowClear;

        private Ellipse _windowOval;
        private System.Windows.Media.RotateTransform _windowOvalRotate;

        private Ellipse EnsureWindowOval()
        {
            if (_windowOval == null)
            {
                _windowOvalRotate = new System.Windows.Media.RotateTransform(0);
                _windowOval = new Ellipse
                {
                    Stroke = Brushes.LightBlue,
                    StrokeThickness = 2,
                    Fill = null,
                    IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = _windowOvalRotate
                };
            }
            return _windowOval;
        }

        private void BeginFindTurningAngle()
        {
            if (_lastSplineDense == null || _lastSplineDense.Count < 3) return;
            TurningWindowReady?.Invoke(EnsureWindowOval());
            _turningIndex = 0;
            UpdateWindowOval(_turningIndex);
        }

        private List<UIElement> ProcessFindTurningAngleClick(Vector2 mousePos)
        {
            ClearElementsToRemove();
            if (_lastSplineDense == null || _lastSplineDense.Count < 3)
                return new List<UIElement>();

            if (!TryProjectOntoSpline(mousePos, out _, out int idx))
                return new List<UIElement>();

            _turningIndex = idx;
            UpdateWindowOval(idx);
            FindTurningAngleDisplay =
                FormatTurningReadout(_lastSplineDense, idx, _turningAngleWindow);
            return new List<UIElement>();
        }

        // Returns the closest point on the spline and its nearest dense-vertex index.
        private bool TryProjectOntoSpline(Vector2 mouse, out Vector2 onCurve, out int nearestIndex)
        {
            onCurve = mouse;
            nearestIndex = -1;
            var pts = _lastSplineDense;
            if (pts == null || pts.Count < 2) return false;

            double bestDist2 = double.MaxValue;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                Vector2 ab = b - a;
                double len2 = ab.X * ab.X + ab.Y * ab.Y;
                double t = len2 > 1e-9 ? ((mouse - a) | ab) / len2 : 0.0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                Vector2 proj = a + ab * t;
                double dx = mouse.X - proj.X, dy = mouse.Y - proj.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestDist2)
                {
                    bestDist2 = d2;
                    onCurve = proj;
                    nearestIndex = (t < 0.5) ? i : i + 1;
                }
            }
            return nearestIndex >= 0;
        }

        // Clamped sample span [i0, i1] centred on index, half-width `window`.
        private static (int i0, int i1) TurningWindowBounds(int count, int index, int window)
        {
            int i0 = Math.Max(0, index - window);
            int i1 = Math.Min(count - 1, index + window);
            if (i0 == index) i0 = Math.Max(0, index - 1);
            if (i1 == index) i1 = Math.Min(count - 1, index + 1);
            return (i0, i1);
        }

        // Turning angle and arc length over the probed span, in canvas pixels; the
        // caller converts the length before dividing.
        private static (double angleDeg, double arcLenCanvas) LocalTurningAngleArcLength(
            List<Vector2> pts, int index, int window)
        {
            int n = pts.Count;
            if (n < 3) return (0, 0);

            var (i0, i1) = TurningWindowBounds(n, index, window);
            if (i1 - i0 < 2) return (0, 0); // need at least one interior vertex

            double arc = 0;
            for (int k = i0 + 1; k <= i1; k++)
                arc += (pts[k] - pts[k - 1]).Magnitude();

            double totalTurning = 0;
            for (int k = i0 + 1; k <= i1 - 1; k++)
            {
                Vector2 seg1 = pts[k] - pts[k - 1];
                Vector2 seg2 = pts[k + 1] - pts[k];
                if (seg1.Magnitude() < 1e-5 || seg2.Magnitude() < 1e-5) continue;
                totalTurning += Math.Abs(Vector2.AngleBetween(seg1, seg2));
            }

            return (totalTurning, arc);
        }

        // Hover readout in °/px, matching the committed Turn/Length column's units:
        // the span's length is converted to image pixels so the live number and the
        // stored one are on the same scale.
        private string FormatTurningReadout(List<Vector2> pts, int index, int window)
        {
            var (angleDeg, arcLenCanvas) = LocalTurningAngleArcLength(pts, index, window);
            double arcLen = ToImageLength(arcLenCanvas);
            if (arcLen < 1e-9) return "";
            return $"{angleDeg / arcLen:F2}\u00B0/px";
        }

        // Sizes and rotates the oval to enclose the probed span.
        private void UpdateWindowOval(int index)
        {
            if (_windowOval == null || _lastSplineDense == null) return;
            int n = _lastSplineDense.Count;
            if (n < 2) return;

            var (i0, i1) = TurningWindowBounds(n, index, _turningAngleWindow);
            Vector2 a = _lastSplineDense[i0];
            Vector2 b = _lastSplineDense[i1];
            Vector2 chord = b - a;
            double chordLen = chord.Magnitude();
            if (chordLen < 1e-6) return;

            Vector2 unit = new Vector2(chord.X / chordLen, chord.Y / chordLen);
            Vector2 normal = new Vector2(-unit.Y, unit.X);

            double bow = 0;
            for (int i = i0; i <= i1; i++)
            {
                double dev = Math.Abs((_lastSplineDense[i] - a) | normal);
                if (dev > bow) bow = dev;
            }

            const double pad = 14.0;
            double major = Math.Max(16.0, chordLen + pad * 2);
            double minor = Math.Max(16.0, bow * 2 + pad * 2);

            Vector2 center = (a + b) * 0.5;
            _windowOval.Width = major;
            _windowOval.Height = minor;
            Canvas.SetLeft(_windowOval, center.X - major / 2);
            Canvas.SetTop(_windowOval, center.Y - minor / 2);
            _windowOvalRotate.Angle = Math.Atan2(chord.Y, chord.X) * 180.0 / Math.PI;
        }

        private Path MakeCatmullRomPath(List<Vector2> controlPoints)
        {
            if (controlPoints.Count < 2) return null;

            var pts = SplineFitting.GetCatmullRomPoints(controlPoints, 20);
            if (pts.Count < 2) return null;

            var figure = new PathFigure { IsClosed = false, StartPoint = new Point(pts[0].X, pts[0].Y) };
            var polyline = new PolyLineSegment();
            for (int i = 1; i < pts.Count; i++)
                polyline.Points.Add(new Point(pts[i].X, pts[i].Y));
            figure.Segments.Add(polyline);

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return new Path { Stroke = this.LineColor, StrokeThickness = this.LineThickness, Data = geometry };
        }

        private Path MakeSchneiderBezierPath(List<Vector2> controlPoints, double tolerance = 2.0)
        {
            if (controlPoints == null || controlPoints.Count < 2) return null;

            var segments = SplineFitting.FitSchneiderBezier(controlPoints, tolerance);
            if (segments.Count == 0) return null;

            var figure = new PathFigure
            {
                StartPoint = new Point(segments[0].P0.X, segments[0].P0.Y),
                IsClosed = false
            };

            var pathSegments = new PathSegmentCollection();
            foreach (var seg in segments)
            {
                pathSegments.Add(new BezierSegment(
                    new Point(seg.P1.X, seg.P1.Y),
                    new Point(seg.P2.X, seg.P2.Y),
                    new Point(seg.P3.X, seg.P3.Y),
                    true));
            }

            figure.Segments = pathSegments;
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return new Path { Stroke = this.LineColor, StrokeThickness = this.LineThickness, Data = geometry };
        }
        #endregion

        #region Operation averages
        // Live per-type averages from history. The parabola formula is excluded
        // (not a single number to average).

        private IEnumerable<CircularArcOperation> CircularArcOps => OperationsOfKind<CircularArcOperation>();
        private IEnumerable<ParabolaOperation> ParabolaOps => OperationsOfKind<ParabolaOperation>();
        private IEnumerable<SplineOperation> SplineOps => OperationsOfKind<SplineOperation>();

        // Circular arc
        public string AvgCentralAngleResult => FormatAverage(CircularArcOps.Select(o => o.CentralAngle));
        public string AvgChordArcRatioResult => FormatAverage(CircularArcOps.Select(o => o.ChordArcRatio));
        public string AvgAspectRatioResult => FormatAverage(CircularArcOps.Select(o => o.AspectRatio));
        public string AvgCircularRadiusScaledResult => FormatScaledLengthAverage(CircularArcOps.Select(o => o.RadiusImagePixels));
        // Parabolic arc (formula excluded)
        public string AvgPChordArcRatioResult => FormatAverage(ParabolaOps.Select(o => o.PChordArcRatio));
        public string AvgRiseSpanRatioResult => FormatAverage(ParabolaOps.Select(o => o.RiseSpanRatio));
        public string AvgVertexCurvatureResult => FormatAverage(ParabolaOps.Select(o => o.VertexCurvature));
        public string AvgParabolicRadiusScaledResult => FormatScaledLengthAverage(ParabolaOps.Select(o => o.VertexRadiusImagePixels));
        // n-point spline (Catmull-Rom and Bézier combined, matching n_spline)
        public string AvgTurningAngleArcRatioResult => FormatAverage(SplineOps.Select(o => o.TurningAngleArcRatio));
        public string AvgSChordArcRatioResult => FormatAverage(SplineOps.Select(o => o.SChordArcRatio));
        public string AvgSplineLengthScaledResult => FormatScaledLengthAverage(SplineOps.Select(o => o.SplineLengthImagePixels));

        protected override void RecomputeAverages()
        {
            OnPropertyChanged(nameof(AvgCentralAngleResult));
            OnPropertyChanged(nameof(AvgChordArcRatioResult));
            OnPropertyChanged(nameof(AvgAspectRatioResult));
            OnPropertyChanged(nameof(AvgPChordArcRatioResult));
            OnPropertyChanged(nameof(AvgRiseSpanRatioResult));
            OnPropertyChanged(nameof(AvgVertexCurvatureResult));
            OnPropertyChanged(nameof(AvgTurningAngleArcRatioResult));
            OnPropertyChanged(nameof(AvgSChordArcRatioResult));
            OnPropertyChanged(nameof(AvgSplineLengthScaledResult));
            OnPropertyChanged(nameof(AvgCircularRadiusScaledResult));
            OnPropertyChanged(nameof(AvgParabolicRadiusScaledResult));
        }
        #endregion

        #region results and tips
        private double CalculateSChordArcRatio(List<Vector2> densePoints, List<Vector2> controlPoints)
        {
            double arcLength = GeometryCalculations.ArcLength(densePoints);
            double chordLength = (controlPoints[controlPoints.Count - 1] - controlPoints[0]).Magnitude();
            return GeometryCalculations.ArcChordRatio(arcLength, chordLength);
        }

        private static readonly string[] CircularArcTips = BuildTips(
            "💡 Approximate a curve as the arc of a circle. First click each endpoint of the arc, then click its midpoint.",
            "💡 Central angle measures the angle between the radii that define the circular arc. Higher angles correspond to larger arcs.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Rise/span ratio measures how tall an arc is relative to its width.");

        private static readonly string[] ParabolicArcTips = BuildTips(
            "💡 Approximate a curve as a parabolic arc. First click each endpoint of the arc, then click its midpoint.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Rise/span ratio measures how tall an arc is relative to its width.",
            "💡 Vertex curvature describes sharpness of the curve at its peak. This is the 'm' of 'y=mx^2'.");

        private static readonly string[] CatmullRomTips = BuildTips(
            "💡 Draw a curve of any shape using any number of points. Press 'Enter' to finish drawing.",
            "💡 Catmull-Rom splines use local smoothing and must pass through every clicked point. This operation draws a centripetal Catmull-Rom spline.",
            "💡 Bézier splines use global smoothing and may not pass through every clicked point. Points are used to approximate a smooth curve.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Turn/Length (Turning angle - spline length ratio) measures how sharply the curve bends, on average, along its length.");

        private static readonly string[] BezierTips = BuildTips(
            "💡 Bézier splines use global smoothing and may not pass through every clicked point. Points are used to approximate a smooth curve.",
            "💡 This operation uses Schneider's Bézier fitting to convert points into one or more smooth cubic Bézier segments.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Turn/Length (Turning angle - spline length ratio) measures how sharply the curve bends, on average, along its length.");

        private static readonly string[] FreehandTips = BuildTips(
            "💡 Click and hold, then drag along the curve. The spline is finished the moment you release.",
            "💡 Smoothing to a low value filters hand tremor out of the traced stroke. High smoothing settings pull the spline away from fine detail and shorten it.",
            "💡 Set Smoothing to 0 to measure the path exactly as drawn. Every wobble then counts as curvature, so Turn/Length will read higher than the same shape clicked.",
            "💡 Draw in one steady pass. Retracing or pausing mid-stroke adds detail the measurements will count.",
            "💡 Chord/arc ratio approaches 1 for shallow arcs and decreases as the arc becomes more curved.",
            "💡 Turn/Length (Turning angle - spline length ratio) measures how sharply the curve bends, on average, along its length.");

        private static readonly string[] NoMethodTips = BuildTips(
            "💡 Select a curvature method to begin.");

        public override string[] GetTips()
        {
            if (IsCircularArcSelected) return CircularArcTips;
            if (IsParabolicArcSelected) return ParabolicArcTips;
            if (IsNPointSplineSelected)
            {
                if (IsFreehandSelected) return FreehandTips;
                return IsCatmullRomSelected ? CatmullRomTips : BezierTips;
            }
            return NoMethodTips;
        }

        #endregion

    }
}