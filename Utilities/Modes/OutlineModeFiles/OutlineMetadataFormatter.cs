using DinoLino.DataTypes;
using System.Text;

namespace DinoLino.Utilities.Modes
{
    /// <summary>
    /// Formats outline measurements for display and copy/export text.
    /// </summary>
    public static class OutlineMetadataFormatter
    {
        // =====================
        // Warnings
        // =====================

        /// <summary>
        /// Builds the warning text for outlines whose first harmonic is unstable or degenerate.
        /// </summary>
        public static string BuildNormalizationWarning(
            EfdNormalizationStatus status, double firstHarmonicAxisRatio,
            EfdRotationReference rotationReference, bool fellBackToFirstHarmonic)
        {
            // Asked to rotate onto an axis the specimen does not carry. Said before
            // anything else, because it is the reason the numbers are not what was
            // asked for and it is the one the user can act on.
            if (fellBackToFirstHarmonic)
                return "⚠ No axis drawn on this specimen, so rotation was normalized to the " +
                       "first harmonic instead. Draw one with Tools, Align, Align Specimen and measure again.";

            switch (status)
            {
                case EfdNormalizationStatus.NearlyCircular:
                    // Near-circular outlines have an unstable principal axis, so normalized EFDs can vary.
                    // A drawn axis fixes the rotation, which leaves only the start point turning on it.
                    return rotationReference == EfdRotationReference.DrawnAxis
                        ? $"⚠ Near-circular first harmonic (axis ratio {firstHarmonicAxisRatio:F2}); " +
                          "rotation comes from the drawn axis and is unaffected, but the start point is " +
                          "unstable — normalized coefficients may not be comparable across specimens."
                        : $"⚠ Near-circular first harmonic (axis ratio {firstHarmonicAxisRatio:F2}); " +
                          "rotation/start-point alignment is unstable — normalized coefficients may not be comparable across specimens.";

                case EfdNormalizationStatus.Degenerate:
                    // A zero-length first harmonic means the outline has no usable orientation or scale basis.
                    return "⚠ First harmonic ~0; orientation and scale can't be defined for this outline.";

                default:
                    return "";
            }
        }

        // =====================
        // Summary text
        // =====================

        /// <summary>
        /// Builds the multi-line metadata summary shown to users or copied to the clipboard.
        /// </summary>
        public static string BuildSummary(
            double aspectRatio,
            double maxLength,
            double maxWidth,
            string unit,
            double perimeter,
            double area,
            double circularity,
            double solidity,
            double convexity,
            double turningAnglePerLength,
            double vertexSpacing,
            int vertexCount,
            int harmonics,
            double[] efdCoefficients,
            EfdNormalizationStatus normalizationStatus,
            double firstHarmonicAxisRatio,
            EfdRotationReference rotationReference = EfdRotationReference.FirstHarmonic,
            double? drawnAxisDegrees = null)
        {
            var sb = new StringBuilder();

            if (normalizationStatus != EfdNormalizationStatus.Ok)
            {
                // Surface the warning inline so the summary clearly explains why normalized values may be unstable.
                sb.AppendLine(rotationReference == EfdRotationReference.DrawnAxis
                    ? $"  ⚠ Start point ambiguous (1st-harmonic axis ratio {firstHarmonicAxisRatio:F2}); rotation itself is fixed by the drawn axis."
                    : $"  ⚠ Orientation ambiguous (1st-harmonic axis ratio {firstHarmonicAxisRatio:F2}); normalized rotation may be unstable.");
            }

            sb.AppendLine($"Aspect Ratio:       {aspectRatio:F3}");
            sb.AppendLine($"Max Length:         {maxLength:F2} {unit}");
            sb.AppendLine($"Max Width:          {maxWidth:F2} {unit}");
            sb.AppendLine($"Perimeter:          {perimeter:F2} {unit}");
            sb.AppendLine($"Area:               {area:F2} {unit}\u00B2");
            sb.AppendLine($"Circularity:        {circularity:F4}");
            sb.AppendLine($"Solidity:           {solidity:F4}");
            sb.AppendLine($"Convexity:          {convexity:F4}");
            sb.AppendLine($"Turn/Length:        {turningAnglePerLength:F4}");
            sb.AppendLine($"Vertex Spacing:     {vertexSpacing:F3} {unit} ({vertexCount} points)");
            sb.AppendLine($"Rotation:           {RotationText(rotationReference, drawnAxisDegrees)}");
            sb.AppendLine($"EFD harmonics ({harmonics}):");

            // Each harmonic contributes four coefficients: a, b, c, d.
            for (int h = 0; h < harmonics; h++)
            {
                int k = h * 4;
                sb.AppendLine($"  n={h + 1}: a={efdCoefficients[k]:F4} b={efdCoefficients[k + 1]:F4} c={efdCoefficients[k + 2]:F4} d={efdCoefficients[k + 3]:F4}");
            }

            return sb.ToString();
        }

        /// Names the direction the coefficients were turned onto +X, and the angle
        /// when that came from a drawn axis, so one set of numbers can be told from a
        /// set normalized the other way or against an axis since redrawn.
        private static string RotationText(EfdRotationReference reference, double? drawnAxisDegrees) =>
            reference == EfdRotationReference.DrawnAxis && drawnAxisDegrees.HasValue
                ? $"drawn axis ({drawnAxisDegrees.Value:F1}\u00B0)"
                : "first harmonic";
    }
}