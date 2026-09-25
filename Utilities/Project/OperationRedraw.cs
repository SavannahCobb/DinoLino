using DinoLino.DataTypes;
using DinoLino.Utilities.Modes;
using DinoLino.Utilities.Operations;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Draws a committed operation again from the geometry stored with it. The points
    /// are kept in image pixels, so a measurement taken in one window can be drawn
    /// again in another of any size.
    ///
    /// What comes back is the shape that was measured: the chord and bisector of an
    /// arc and the arc itself, the curve of a spline, a triangle, a drawn shape, a
    /// line, an outline. The working marks of the original drawing — the radius lines
    /// of a circular arc, for instance — measured nothing and are not redrawn.
    /// </summary>
    public static class OperationRedraw
    {
        /// <summary>How a redrawn measurement is stroked: the mode's color and width.</summary>
        private readonly struct LineStyle
        {
            public LineStyle(Brush color, double thickness)
            {
                Color = color ?? Brushes.OrangeRed;
                Thickness = thickness > 0 ? thickness : 2;
            }

            public Brush Color { get; }
            public double Thickness { get; }
        }

        /// <summary>Samples used to draw a parabola, matching the drawing it replaces.</summary>
        private const int ParabolaSamples = 64;

        /// <summary>
        /// The elements that show one operation, or an empty list when it carries no
        /// geometry — an operation saved before geometry was recorded, for instance.
        /// </summary>
        public static List<UIElement> Build(
            WorkOperation operation, ViewTransform transform, Brush lineColor, double thickness)
        {
            var elements = new List<UIElement>();

            if (operation == null || operation.ImagePoints == null || operation.ImagePoints.Count == 0)
                return elements;

            // Without a way onto the canvas the points mean nothing there, and drawing
            // them where they fall would put the measurement in the wrong place.
            if (!transform.IsValid) return elements;

            var style = new LineStyle(lineColor, thickness);

            var points = new List<Point>(operation.ImagePoints.Count);
            foreach (var point in operation.ImagePoints)
                points.Add(transform.ImageToCanvas(point));

            if (operation is CircularArcOperation) DrawCircularArc(points, style, elements);
            else if (operation is ParabolaOperation) DrawParabolicArc(points, style, elements);
            else if (operation is SplineOperation) DrawCurve(points, style, elements);
            else if (operation is GetAngleOperation) DrawTriangle(points, style, elements);
            else if (operation is ShapeOperation) DrawShape((ShapeOperation)operation, points, style, elements);
            else if (operation is LineOperation) DrawLine(points, style, elements);
            else if (operation is OutlineOperation) DrawOutline(points, style, elements);

            return elements;
        }

        // ---- Curvature ----

        // The chord between the first two clicks, the bisector out to the third, and
        // the arc through all three.
        private static void DrawCircularArc(List<Point> points, LineStyle style, List<UIElement> elements)
        {
            if (points.Count < 3) return;

            Point a = points[0], b = points[1], c = points[2];

            elements.Add(MakeLine(a, b, style));
            elements.Add(MakeLine(Midpoint(a, b), c, style));

            Point centre;
            double radius;
            if (!Circumcircle(a, b, c, out centre, out radius)) return;

            var figure = new PathFigure { StartPoint = a, IsClosed = false };

            figure.Segments.Add(new ArcSegment
            {
                Point = b,
                Size = new Size(radius, radius),
                SweepDirection = SweepOf(a, b, c),
                IsLargeArc = IsLargeArc(centre, a, b, c)
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            elements.Add(new Path { Data = geometry, Stroke = style.Color, StrokeThickness = style.Thickness });
        }

        private static void DrawParabolicArc(List<Point> points, LineStyle style, List<UIElement> elements)
        {
            if (points.Count < 3) return;

            Point a = points[0], b = points[1], c = points[2];

            elements.Add(MakeLine(a, b, style));
            elements.Add(MakeLine(Midpoint(a, b), c, style));

            var origin = new Vector2(a.X, a.Y);
            var end = new Vector2(b.X, b.Y);
            var through = new Vector2(c.X, c.Y);

            Vector2 xAxis, yAxis;
            double chordLength;
            if (!GeometryCalculations.BuildLocalBasis(origin, end, out xAxis, out yAxis, out chordLength)) return;

            Vector2 delta = through - origin;
            var local = new Vector2((delta | xAxis) / chordLength, (delta | yAxis) / chordLength);

            double pa, pb, pc;
            (pa, pb, pc) = GeometryCalculations.SolveParabola(0, 0, 1, 0, local.X, local.Y);

            if (pa == 0 && pb == 0 && pc == 0) return;

            var curve = new List<Point>(ParabolaSamples + 1);

            for (int i = 0; i <= ParabolaSamples; i++)
            {
                double t = (double)i / ParabolaSamples;
                double height = pa * t * t + pb * t + pc;

                curve.Add(new Point(
                    origin.X + xAxis.X * t * chordLength + yAxis.X * height * chordLength,
                    origin.Y + xAxis.Y * t * chordLength + yAxis.Y * height * chordLength));
            }

            elements.Add(MakePolyline(curve, style));
        }

        private static void DrawCurve(List<Point> points, LineStyle style, List<UIElement> elements)
        {
            if (points.Count < 2) return;

            elements.Add(MakePolyline(points, style));
        }

        // ---- Triangle ----

        private static void DrawTriangle(List<Point> points, LineStyle style, List<UIElement> elements)
        {
            if (points.Count < 3) return;

            Point a = points[0], b = points[1], c = points[2];

            elements.Add(MakeLine(a, b, style));
            elements.Add(MakeLine(b, c, style));
            elements.Add(MakeLine(c, a, style));

            elements.Add(MakeLabel("A", a, style));
            elements.Add(MakeLabel("B", b, style));
            elements.Add(MakeLabel("C", c, style));
        }

        // ---- Draw mode ----

        private static void DrawShape(
            ShapeOperation operation, List<Point> points, LineStyle style, List<UIElement> elements)
        {
            if (points.Count < 2) return;

            double left = Math.Min(points[0].X, points[1].X);
            double top = Math.Min(points[0].Y, points[1].Y);
            double width = Math.Abs(points[1].X - points[0].X);
            double height = Math.Abs(points[1].Y - points[0].Y);

            bool round = operation.ShapeKind == DrawMode.ShapeConstraint.Ellipse
                         || operation.ShapeKind == DrawMode.ShapeConstraint.Circle;

            Shape shape = round ? (Shape)new Ellipse() : new Rectangle();
            shape.Stroke = style.Color;
            shape.StrokeThickness = style.Thickness;
            shape.Width = width;
            shape.Height = height;

            Canvas.SetLeft(shape, left);
            Canvas.SetTop(shape, top);

            elements.Add(shape);
        }

        private static void DrawLine(List<Point> points, LineStyle style, List<UIElement> elements)
        {
            if (points.Count < 2) return;

            elements.Add(MakeLine(points[0], points[1], style));
        }

        // ---- Outline ----

        private static void DrawOutline(List<Point> points, LineStyle style, List<UIElement> elements)
        {
            if (points.Count < 3) return;

            var outline = OutlineVisuals.CreateOutlinePolyline(
                style.Color, thickness: style.Thickness);
            foreach (var point in points) outline.Points.Add(point);

            elements.Add(outline);
        }

        // ---- Pieces ----

        private static Line MakeLine(Point a, Point b, LineStyle style) => new Line
        {
            Stroke = style.Color,
            StrokeThickness = style.Thickness,
            X1 = a.X,
            Y1 = a.Y,
            X2 = b.X,
            Y2 = b.Y
        };

        private static Polyline MakePolyline(IEnumerable<Point> points, LineStyle style)
        {
            var polyline = new Polyline { Stroke = style.Color, StrokeThickness = style.Thickness };
            foreach (var point in points) polyline.Points.Add(point);

            return polyline;
        }

        // Offset and size follow the labels the modes draw, so a reopened triangle
        // reads the way it did when it was measured.
        private static TextBlock MakeLabel(string text, Point at, LineStyle style)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = style.Color,
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center
            };

            Canvas.SetLeft(label, at.X + 5);
            Canvas.SetTop(label, at.Y + 5);
            return label;
        }

        private static Point Midpoint(Point a, Point b) =>
            new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        /// The circle through three points, or false when they fall on a line.
        private static bool Circumcircle(Point a, Point b, Point c, out Point centre, out double radius)
        {
            centre = new Point();
            radius = 0;

            double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            if (Math.Abs(d) < 1e-9) return false;

            double aSquared = a.X * a.X + a.Y * a.Y;
            double bSquared = b.X * b.X + b.Y * b.Y;
            double cSquared = c.X * c.X + c.Y * c.Y;

            centre = new Point(
                (aSquared * (b.Y - c.Y) + bSquared * (c.Y - a.Y) + cSquared * (a.Y - b.Y)) / d,
                (aSquared * (c.X - b.X) + bSquared * (a.X - c.X) + cSquared * (b.X - a.X)) / d);

            double dx = centre.X - a.X, dy = centre.Y - a.Y;
            radius = Math.Sqrt(dx * dx + dy * dy);

            return radius > 0 && !double.IsInfinity(radius);
        }

        // Which way round the arc runs, decided by the side the third point falls on.
        private static SweepDirection SweepOf(Point a, Point b, Point through)
        {
            double cross = (through.X - a.X) * (b.Y - a.Y) - (through.Y - a.Y) * (b.X - a.X);
            return cross > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        }

        // The arc passes the half circle when its centre lies inside the triangle the
        // three points make.
        private static bool IsLargeArc(Point centre, Point a, Point b, Point through) =>
            GeometryCalculations.IsPointInTriangle(
                new Vector2(centre.X, centre.Y),
                new Vector2(a.X, a.Y),
                new Vector2(b.X, b.Y),
                new Vector2(through.X, through.Y));
    }
}
