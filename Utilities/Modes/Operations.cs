using DinoLino.Utilities.Modes;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities.Operations
{
    /// <summary>
    /// Base type for an operation stored in undo/redo history.
    /// Each operation keeps the visuals it created and any metadata needed to restore the mode state.
    /// </summary>
    public abstract class WorkOperation
    {
        public string OperationKind { get; set; }
        public List<UIElement> Elements { get; set; } = new List<UIElement>();
        public WorkMode SourceMode { get; set; }

        /// The points that define this operation, in image pixels: the space every
        /// stored measurement uses, so they mean the same thing whatever the window
        /// size or the zoom. The elements above are the drawing as it sits on the
        /// canvas today; these are the geometry behind it, which is what lets the same
        /// operation be drawn again later.
        ///
        /// What the points are depends on the kind of operation: the three clicks of a
        /// circular or parabolic arc (chord start, chord end, bisector end), the curve
        /// itself for a spline, the three vertices of a triangle, two opposite corners
        /// for a drawn shape, the two ends of a line, and the vertices of an outline.
        public List<Point> ImagePoints { get; set; } = new List<Point>();

        /// <summary>
        /// Restores the mode-specific metadata saved with this operation.
        /// </summary>
        public abstract void ApplyMetadataToMode();

        /// False for an entry that measures nothing, such as a text note. Undo restores
        /// a panel from the newest entry that has a reading to give it, so an entry that
        /// has none must say so rather than leave the panel showing an undone result.
        public virtual bool CarriesMetadata => true;
    }

    /// <summary>
    /// History entry for a circular-arc measurement.
    /// </summary>
    public class CircularArcOperation : WorkOperation
    {
        public double CentralAngle { get; set; }
        public double AspectRatio { get; set; }
        public double ChordArcRatio { get; set; }
        public double RadiusImagePixels { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is CurvatureMode mode)
            {
                mode.CentralAngleResult = CentralAngle;
                mode.AspectRatioResult = AspectRatio;
                mode.ChordArcRatioResult = ChordArcRatio;
                mode.RestoreCircularArcRadius(RadiusImagePixels);
            }
        }
    }

    /// <summary>
    /// History entry for a parabola measurement.
    /// </summary>
    public class ParabolaOperation : WorkOperation
    {
        public string XYFunction { get; set; }
        public double RiseSpanRatio { get; set; }
        public double PChordArcRatio { get; set; }
        public double VertexCurvature { get; set; }
        public double VertexRadiusImagePixels { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is CurvatureMode mode)
            {
                mode.XYFunctionResult = XYFunction;
                mode.RiseSpanRatioResult = RiseSpanRatio;
                mode.PChordArcRatioResult = PChordArcRatio;
                mode.VertexCurvatureResult = VertexCurvature;
                mode.RestoreParabolicVertexRadius(VertexRadiusImagePixels);
            }
        }
    }

    /// <summary>
    /// History entry for an n-point spline measurement.
    /// </summary>
    public class SplineOperation : WorkOperation
    {
        /// The points the user placed, in image pixels: the clicked points of a
        /// Catmull-Rom or Bezier spline, or the ones a freehand stroke was reduced to.
        /// ImagePoints holds the curve they produced, which is what was measured.
        public List<Point> ControlImagePoints { get; set; } = new List<Point>();

        public double TurningAngleArcRatio { get; set; }
        public double SChordArcRatio { get; set; }

        // Measured in image pixels, which do not change with window size, so a value
        // stored here converts to the same real-world number whenever it is read.
        public double SplineLengthImagePixels { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is CurvatureMode mode)
            {
                mode.TurningAngleArcRatioResult = TurningAngleArcRatio;
                mode.SChordArcRatioResult = SChordArcRatio;
                mode.RestoreScaledMeasurements(SplineLengthImagePixels);
            }
        }
    }

    /// <summary>
    /// History entry for a triangle angle measurement.
    /// </summary>
    public class GetAngleOperation : WorkOperation
    {
        public double AngleA { get; set; }
        public double AngleB { get; set; }
        public double AngleC { get; set; }
        public double TriAspectRatio { get; set; }

        // Measured in square image pixels.
        public double TriAreaImagePixels { get; set; }

        public object RelativeArea { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is GetAngleMode mode)
            {
                mode.AngleAResult = AngleA;
                mode.AngleBResult = AngleB;
                mode.AngleCResult = AngleC;
                mode.TriAspectRatioResult = TriAspectRatio;
                mode.RelativeAreaResult = RelativeArea;
                mode.RestoreScaledMeasurements(TriAreaImagePixels);
            }
        }
    }

    /// History entry for a line measured against the specimen's own axis, rather
    /// than against another line or against the image.
    public class AxisAngleOperation : WorkOperation
    {
        /// Clockwise angle from the specimen's X axis to the drawn line, in degrees
        /// within [0, 360). Measured against the image's own axis when the specimen
        /// has no alignment, which is what MeasuredAgainstAxis records.
        public double AxisAngleDegrees { get; set; }

        /// <summary>True when the specimen was aligned at the time of measuring.</summary>
        public bool MeasuredAgainstAxis { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is GetAngleMode mode) mode.RestoreAxisAngle(this);
        }
    }

    /// <summary>
    /// History entry for a drawn shape measurement.
    /// </summary>
    public class ShapeOperation : WorkOperation
    {
        /// Which constrained shape this was. A rectangle and an ellipse are not the
        /// same measurement, so the kind travels with the operation: the Batch
        /// Workshop gives each kind its own columns and its own attempt numbering,
        /// and the on-image counter tallies them separately.
        public DrawMode.ShapeConstraint ShapeKind { get; set; }

        public double DrawAspectRatio { get; set; }

        /// Area against the previous shape OF THE SAME KIND, or "N/A" when this is
        /// the first of its kind. Boxed as a double or that string.
        public object RelativeArea { get; set; }

        // Measured in square image pixels.
        public double ShapeAreaImagePixels { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is DrawMode mode)
            {
                mode.DrawAspectRatioResult = DrawAspectRatio;
                mode.RelativeAreaResult = RelativeArea;
                mode.RestoreShapeMeasurement(ShapeAreaImagePixels);
            }
        }
    }

    /// <summary>
    /// History entry for a line measurement.
    /// </summary>
    public class LineOperation : WorkOperation
    {
        // Measured in image pixels.
        public double LineLengthImagePixels { get; set; }

        /// Extent of the line along the specimen's X axis, in image pixels, taken from
        /// the orientation set by Tools ▸ Align ▸ Align Specimen. An unaligned specimen falls back
        /// to the image's own axes, which is what ImageAlignment hands back when no
        /// orientation has been drawn.
        public double LineDeltaXImagePixels { get; set; }

        /// <summary>Extent along the specimen's Y axis, in image pixels.</summary>
        public double LineDeltaYImagePixels { get; set; }

        public object LineLengthRatio { get; set; }
        public object LineAngle { get; set; }
        public double HeadingDegrees { get; set; }

        /// True when the specimen carried an orientation as the line was measured, and
        /// so whether the two extents above are on its axes or on the image's.
        public bool MeasuredAgainstAxis { get; set; }

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is DrawMode mode)
            {
                mode.LineLengthRatioResult = LineLengthRatio;
                mode.LineAngleResult = LineAngle;
                mode.RestoreLineMeasurement(
                    LineLengthImagePixels, LineDeltaXImagePixels, LineDeltaYImagePixels);
            }
        }
    }

    /// <summary>What a label puts on the picture.</summary>
    public enum AnnotationKind
    {
        /// <summary>Words the user types.</summary>
        Text,

        /// <summary>A filled circle.</summary>
        Point,

        /// <summary>A filled five-pointed star.</summary>
        Star
    }

    /// A label the user has put over the image: words, a dot or a star. It shares the
    /// history with the measurements, so Undo reaches it, Redo brings it back and it
    /// travels with its specimen, but it measures nothing: no table has a column for it,
    /// no counter tallies it, and it is drawn on a layer of its own so it is not swept
    /// away with the drawn operations when See Previous Operations is off.
    public class AnnotationOperation : WorkOperation
    {
        /// <summary>Which of the three this label is.</summary>
        public AnnotationKind Kind { get; set; } = AnnotationKind.Text;

        /// <summary>What the user typed. Empty for a dot or a star.</summary>
        public string Text { get; set; } = "";

        /// Where on the picture the label is pinned, in image pixels: the first letter
        /// for words, the middle for a dot or a star. Image pixels rather than canvas
        /// ones so a label keeps its place on the feature it names however the window is
        /// resized or the view zoomed.
        public double ImageX { get; set; }

        /// <summary>The other half of the label's position, in image pixels.</summary>
        public double ImageY { get; set; }

        /// How large the label is drawn, set by dragging its corner: the height of a
        /// letter for words, the width for a dot or a star. Kept with the label rather
        /// than taken from a setting, so two labels on the same picture can be sized
        /// against what each of them points at.
        public double Size { get; set; } = 13.0;

        /// The typeface the words are drawn in, by name, as the setting under Settings,
        /// Font read when the label was made. Empty falls back to that setting, which is
        /// what a label from a build that did not record it is read back as. Unused by a
        /// dot or a star, which have no words to draw.
        public string FontName { get; set; } = "";

        /// Nothing to put on a panel: a label is not a measurement, and the mode that
        /// placed it shows no reading for it.
        public override void ApplyMetadataToMode() { }

        /// <inheritdoc />
        public override bool CarriesMetadata => false;
    }

    /// <summary>
    /// History entry for outline analysis metadata.
    /// </summary>
    public class OutlineOperation : WorkOperation
    {
        public double AspectRatio { get; set; }
        public double Circularity { get; set; }
        public double Solidity { get; set; }
        public double SumTurningAngles { get; set; }
        public double TurningAngleLength { get; set; }
        public double Convexity { get; set; }

        // Flattened coefficient array: [a1, b1, c1, d1, a2, b2, c2, d2, ...].
        public double[] EFDCoefficients { get; set; }

        // Measured in image pixels, which do not change with window size, so a value
        // stored here converts to the same real-world number whenever it is read.
        public double PerimeterImagePixels { get; set; }
        public double AreaImagePixels { get; set; }
        public double MaxLengthImagePixels { get; set; }
        public double MaxWidthImagePixels { get; set; }
        // Vertex spacing the metrics above were measured at, in image pixels, and
        // the vertex count it produced. Recorded because every number on this
        // operation is conditional on them.
        public double MeasurementSpacingImagePixels { get; set; }
        public int MeasurementPointCount { get; set; }

        public bool HasMetadata { get; set; }

        // Stored text shown by the outline panel so undo/redo can restore the exact UI state.
        public string MetadataSummary { get; set; } = "";
        public string NormalizationWarning { get; set; } = "";

        public override void ApplyMetadataToMode()
        {
            if (SourceMode is OutlineMode mode)
            {
                mode.AspectRatioResult = AspectRatio;
                mode.CircularityResult = Circularity;
                mode.SolidityResult = Solidity;
                mode.ConvexityResult = Convexity;
                mode.SumTurningAnglesResult = SumTurningAngles;
                mode.TurningAngleLengthResult = TurningAngleLength;
                mode.EFDCoefficientsResult = EFDCoefficients;

                // Restore the scaled measurements and summary text only when metadata exists.
                if (HasMetadata)
                {
                    mode.RestoreScaledMeasurements(
                        PerimeterImagePixels, AreaImagePixels,
                        MaxLengthImagePixels, MaxWidthImagePixels,
                        MeasurementSpacingImagePixels, MeasurementPointCount);
                    mode.MetadataSummary = MetadataSummary;
                    mode.NormalizationWarning = NormalizationWarning;
                }
            }
        }
    }
}