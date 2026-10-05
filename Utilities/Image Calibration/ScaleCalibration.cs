using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DinoLino.Utilities;

// This one file holds the scale data layer (ScaleState, ScaleCalibration and
// ScaleSource, in DinoLino.Utilities) and the workspace scalebar that reads from it
// (in DinoLino).

namespace DinoLino.Utilities
{
    /// <summary>
    /// One specimen's pixel-to-unit conversion. Immutable, and constructible only
    /// from a usable measurement, so a set state always carries a valid ratio.
    /// </summary>
    public readonly struct ScaleState : IEquatable<ScaleState>
    {
        /// <summary>Shortest calibration line, in image pixels, worth trusting.</summary>
        public const double MinimumLinePixels = 2.0;

        public bool IsSet { get; }

        /// Real-world units in one image pixel. Image pixels rather than canvas
        /// pixels because the canvas coordinate space depends on window size and on
        /// the dimensions of whichever image is displayed.
        public double UnitsPerImagePixel { get; }

        public string Unit { get; }

        /// True when this calibration was copied from another specimen rather than
        /// measured on this one's own image. The ratio is no less usable for it, but a
        /// reader is entitled to know which specimens were actually measured, so the
        /// provenance travels with the calibration and is saved with the project.
        public bool Inherited { get; }

        private ScaleState(double unitsPerImagePixel, string unit, bool inherited)
        {
            IsSet = true;
            UnitsPerImagePixel = unitsPerImagePixel;
            Unit = unit;
            Inherited = inherited;
        }

        /// <summary>An uncalibrated specimen, which is also the default value.</summary>
        public static ScaleState None => default;

        /// The calibration a saved project describes. A project stores the ratio
        /// itself rather than the line it was measured from, so this is the way back
        /// in for a value that has already been checked once.
        public static ScaleState FromUnitsPerImagePixel(
            double unitsPerImagePixel, string unit, bool inherited = false)
        {
            if (unitsPerImagePixel <= 0
                || double.IsNaN(unitsPerImagePixel)
                || double.IsInfinity(unitsPerImagePixel)) return None;

            if (string.IsNullOrEmpty(unit)) return None;

            return new ScaleState(unitsPerImagePixel, unit, inherited);
        }

        /// The calibration a measured line describes, or None when the line is too
        /// short or the entered length is not positive.
        public static ScaleState FromLine(double imagePixelLength, double realLength, string unit)
        {
            if (imagePixelLength < MinimumLinePixels) return None;
            if (realLength <= 0 || double.IsNaN(realLength) || double.IsInfinity(realLength)) return None;
            if (string.IsNullOrEmpty(unit)) return None;

            // A measured line is measured, so re-measuring a specimen that had been
            // given another's scale clears the borrowed mark by itself.
            return new ScaleState(realLength / imagePixelLength, unit, false);
        }

        /// The same ratio and unit, marked as taken from another specimen. An
        /// uncalibrated state has nothing to pass on and stays uncalibrated.
        public ScaleState AsInherited() =>
            IsSet ? new ScaleState(UnitsPerImagePixel, Unit, true) : None;

        /// Converts a length already measured in image pixels. Image pixels are what
        /// every stored measurement uses, so this needs nothing but the calibration
        /// itself: no window, no zoom, no specimen has to be on screen.
        public double ToUnitsFromImage(double imagePixelLength) =>
            imagePixelLength * UnitsPerImagePixel;

        /// <summary>Converts an area already measured in square image pixels.</summary>
        public double ToUnitsAreaFromImage(double imagePixelArea) =>
            imagePixelArea * UnitsPerImagePixel * UnitsPerImagePixel;

        public bool Equals(ScaleState other) =>
            IsSet == other.IsSet
            && UnitsPerImagePixel == other.UnitsPerImagePixel
            && Unit == other.Unit
            && Inherited == other.Inherited;

        public override bool Equals(object obj) => obj is ScaleState s && Equals(s);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = IsSet.GetHashCode();
                hash = (hash * 397) ^ UnitsPerImagePixel.GetHashCode();
                hash = (hash * 397) ^ (Unit?.GetHashCode() ?? 0);
                hash = (hash * 397) ^ Inherited.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(ScaleState a, ScaleState b) => a.Equals(b);
        public static bool operator !=(ScaleState a, ScaleState b) => !a.Equals(b);
    }

    /// <summary>
    /// The loaded specimen's scale calibration, and the conversions every mode uses
    /// to turn measurements into real-world units.
    /// </summary>
    public class ScaleCalibration : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private ScaleState _state = ScaleState.None;
        private Specimen _owner;

        // How many canvas pixels one image pixel currently occupies on screen. The
        // host keeps this current as the workspace is laid out and resized.
        private double _canvasPerImagePixel = 1.0;

        // =====================
        // Calibration state
        // =====================

        /// <summary>True when a valid pixel-to-unit conversion has been set.</summary>
        public bool IsCalibrated => _state.IsSet;

        /// <summary>True when this specimen's scale was copied from another.</summary>
        public bool IsInherited => _state.Inherited;

        /// <summary>Unit label chosen by the user, such as mm or µm.</summary>
        public string Unit => _state.Unit;

        /// <summary>Real-world units represented by one canvas pixel right now.</summary>
        public double UnitsPerPixel =>
            _state.IsSet ? _state.UnitsPerImagePixel / _canvasPerImagePixel : 0;

        /// The live calibration. Assigning writes through to the bound specimen, so
        /// what is displayed and what is stored cannot drift apart.
        public ScaleState State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                if (_owner != null) _owner.Calibration = value;
                ProjectSession.MarkChanged();
                NotifyAll();
            }
        }

        /// Makes a specimen's stored calibration the live one and the destination for
        /// every later change. Pass null when no specimen is loaded.
        public void BindTo(Specimen specimen)
        {
            _owner = specimen;
            _state = specimen?.Calibration ?? ScaleState.None;
            NotifyAll();
        }

        /// Records how large the image is being drawn, so canvas measurements can be
        /// converted whatever the window size. Returns true when the ratio moved,
        /// which is the host's cue to refresh anything showing a scaled value.
        public bool UpdateViewScale(double canvasPixelsPerImagePixel)
        {
            if (canvasPixelsPerImagePixel <= 0
                || double.IsNaN(canvasPixelsPerImagePixel)
                || double.IsInfinity(canvasPixelsPerImagePixel))
                return false;

            if (canvasPixelsPerImagePixel == _canvasPerImagePixel) return false;

            _canvasPerImagePixel = canvasPixelsPerImagePixel;
            OnPropertyChanged(nameof(UnitsPerPixel));
            return true;
        }

        /// <summary>Sets the calibration from a line measured in canvas pixels.</summary>
        public void SetFromLine(double canvasPixelLength, double realLength, string unit)
            => State = ScaleState.FromLine(canvasPixelLength / _canvasPerImagePixel, realLength, unit);

        public void Clear() => State = ScaleState.None;

        // =====================
        // Canvas to image space
        // =====================

        /// Converts a canvas-space length into image pixels, the unit every stored
        /// measurement uses. Image pixels do not move when the window is resized, so
        /// a value stored this way yields the same real-world number whenever it is
        /// read.
        public double CanvasToImageLength(double canvasLength) => canvasLength / _canvasPerImagePixel;

        /// <summary>Converts a canvas-space area into square image pixels.</summary>
        public double CanvasToImageArea(double canvasArea) =>
            canvasArea / (_canvasPerImagePixel * _canvasPerImagePixel);

        // =====================
        // Conversion helpers
        // =====================

        /// <summary>Converts a canvas-space length into calibrated units.</summary>
        public double ToUnits(double canvasLength) => canvasLength * UnitsPerPixel;

        /// <summary>Converts a canvas-space area into calibrated square units.</summary>
        public double ToUnitsArea(double canvasArea)
        {
            double perPixel = UnitsPerPixel;
            return canvasArea * perPixel * perPixel;
        }

        /// Converts a length already measured in image pixels, skipping the view
        /// ratio. Preferred for anything stored and converted later.
        public double ToUnitsFromImage(double imagePixelLength) =>
            _state.ToUnitsFromImage(imagePixelLength);

        /// <summary>Converts an area already measured in image pixels.</summary>
        public double ToUnitsAreaFromImage(double imagePixelArea) =>
            _state.ToUnitsAreaFromImage(imagePixelArea);

        /// Said plainly where the sidebar already reports the scale, so a borrowed
        /// calibration is visible while working rather than only on an audit.
        public string StatusText =>
            !IsCalibrated ? "Scale: not calibrated"
            : _state.Inherited ? "Scale: inherited"
            : "";

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsCalibrated));
            OnPropertyChanged(nameof(IsInherited));
            OnPropertyChanged(nameof(Unit));
            OnPropertyChanged(nameof(UnitsPerPixel));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>
    /// Which calibration a table's cells are to be read with, specimen by specimen.
    /// </summary>
    /// <remarks>
    /// A table holds one block of rows per specimen, and the specimens in it need not be
    /// calibrated alike. So the ratio and the unit have to come from the specimen whose
    /// measurements are being read, not from whichever specimen happens to be loaded:
    /// otherwise a specimen measured in micrometres has its pixel counts multiplied by
    /// another's millimetre ratio and labelled in millimetres, which is a wrong number
    /// and not merely a wrong label.
    ///
    /// Nothing is copied here. Each lookup reads the specimen as it stands, so a table
    /// rebuilt after a scale changes is built from the new one.
    /// </remarks>
    public sealed class ScaleSource
    {
        private readonly SpecimenManager _specimens;
        private readonly ScaleCalibration _live;

        public ScaleSource(SpecimenManager specimens, ScaleCalibration live)
        {
            _specimens = specimens;
            _live = live;
        }

        /// One specimen's calibration, found by the creation order its record carries.
        /// A null record is the specimen on screen, whose live calibration is the one
        /// being edited and so the one to read.
        public ScaleState For(SpecimenRecord record)
        {
            if (record == null) return _live?.State ?? ScaleState.None;
            if (_specimens == null) return ScaleState.None;

            var owner = _specimens.Specimens
                .FirstOrDefault(s => s != null && s.Ordinal == record.Ordinal);

            return owner?.Calibration ?? ScaleState.None;
        }

        /// The calibration the cells now being filled belong to. A table is built one
        /// specimen's block at a time, and the column definitions read this as they go;
        /// they are called from nowhere else, which is what makes one slot enough.
        public ScaleState Current { get; private set; }

        /// Whether the specimen whose cells are now being filled has an alignment set.
        /// Read from the specimen as it stands, exactly as its calibration is.
        public bool CurrentAligned { get; private set; }

        /// True when the specimen has an alignment. A null record is the specimen on
        /// screen, whose live alignment is the one to read.
        public bool AlignedFor(SpecimenRecord record)
        {
            if (record == null) return ActiveAlignment.Current.IsAligned;
            if (_specimens == null) return false;

            var owner = _specimens.Specimens
                .FirstOrDefault(s => s != null && s.Ordinal == record.Ordinal);

            return owner != null && owner.Alignment.IsSet;
        }

        /// <summary>Points this at the block about to be filled.</summary>
        public void Select(SpecimenRecord record)
        {
            Current = For(record);
            CurrentAligned = AlignedFor(record);
        }
    }

    /// <summary>What to do about a specimen that already has a measured scale.</summary>
    public enum ScaleOverwriteAnswer
    {
        /// <summary>Put the question to the user.</summary>
        Ask,

        /// <summary>Replace a measured scale with the one being passed on.</summary>
        Overwrite,

        /// <summary>Leave a measured scale where it is.</summary>
        Skip
    }

    /// <summary>
    /// Asks what to do about the specimens in a copy-forward that already carry a scale
    /// measured on their own image.
    /// </summary>
    /// <remarks>
    /// Built in code rather than from XAML so it sits beside the scale it asks about, and
    /// because a plain message box cannot carry the checkbox that stops it asking again.
    /// </remarks>
    public class ScaleApplyWindow : Window
    {
        private readonly CheckBox _remember;
        private ScaleOverwriteAnswer? _answer;

        /// Puts the question. Returns what to do, or null when the user cancelled, in
        /// which case not one specimen is to be touched.
        public static ScaleOverwriteAnswer? Ask(
            Window owner, string sourceName, ScaleState source,
            int targetCount, int measuredCount,
            FontFamily font, double fontSize, out bool remember)
        {
            var window = new ScaleApplyWindow(sourceName, source, targetCount, measuredCount)
            {
                // Set before the window is shown: WPF refuses an owner once a window is
                // modal, and a pop-up without one outlives the window it belongs to.
                Owner = owner
            };

            if (font != null) window.FontFamily = font;
            if (fontSize >= 1) window.FontSize = fontSize;

            window.ShowDialog();

            remember = window._remember.IsChecked == true;
            return window._answer;
        }

        private ScaleApplyWindow(
            string sourceName, ScaleState source, int targetCount, int measuredCount)
        {
            Title = "Apply Scale";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            MaxWidth = 460;

            var body = new StackPanel { Margin = new Thickness(16) };

            body.Children.Add(Paragraph(Taking(sourceName, source, targetCount)));
            body.Children.Add(Paragraph(Already(measuredCount)));

            _remember = new CheckBox
            {
                Content = "Remember this choice and stop asking",
                Margin = new Thickness(0, 4, 0, 14),
                ToolTip = "Kept for the rest of this session, and between sessions when "
                        + "File \u25b8 Save Settings is on."
            };

            body.Children.Add(_remember);
            body.Children.Add(Buttons());

            Content = body;
        }

        private static TextBlock Paragraph(string text) =>
            new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };

        private static string Taking(string sourceName, ScaleState source, int targetCount)
        {
            string ratio = source.UnitsPerImagePixel.ToString("G4", CultureInfo.InvariantCulture);

            string count = targetCount == 1
                ? "1 specimen is"
                : targetCount + " specimens are";

            return count + " to take the scale from " + (sourceName ?? "this specimen")
                 + " \u2014 " + ratio + " " + (source.Unit ?? "") + " per image pixel.";
        }

        private static string Already(int measuredCount) =>
            measuredCount == 1
                ? "1 of them already has a scale measured on its own image. Overwrite that "
                  + "one as well, or leave it as it is?"
                : measuredCount + " of them already have a scale measured on their own "
                  + "images. Overwrite those as well, or leave them as they are?";

        private StackPanel Buttons()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            // Leaving them is the default, so a press of Enter cannot overwrite a reading
            // somebody took; Escape cancels, and cancelling changes nothing at all.
            row.Children.Add(Choice("Cancel", null, isCancel: true));
            row.Children.Add(Choice("Leave Them", ScaleOverwriteAnswer.Skip, isDefault: true));
            row.Children.Add(Choice("Overwrite", ScaleOverwriteAnswer.Overwrite));

            return row;
        }

        private Button Choice(
            string label, ScaleOverwriteAnswer? answer,
            bool isDefault = false, bool isCancel = false)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 92,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(8, 3, 8, 3),
                IsDefault = isDefault,
                IsCancel = isCancel
            };

            button.Click += (s, e) =>
            {
                _answer = answer;
                Close();
            };

            return button;
        }
    }
}

namespace DinoLino
{
    public partial class MainWindow
    {
        // =====================
        // Scalebar
        // =====================
        // The Tools menu handler (Menu_ViewScaleBar) lives in MainWindow.Menus.cs with
        // the rest of the menu handlers and calls UpdateScaleBarVisibility below.

        /// <summary>Segments the bar is divided into, which is one fewer than its labels.</summary>
        private const int ScaleBarIntervals = 10;

        private const double ScaleBarHeight = 12.0;
        private const double ScaleBarLabelRow = 14.0;
        private const double ScaleBarTickHeight = 6.0;
        private const double ScaleBarPad = 10.0;

        // Room one label needs: its digits at the size they are drawn, plus a gap so
        // two neighbours do not touch. A division that puts more digits on the bar
        // needs more room for them, which is why the shortest bar worth drawing is
        // worked out per division rather than fixed.
        private const double ScaleBarDigitWidth = 6.5;
        private const double ScaleBarLabelGap = 5.0;

        // The units Tools, Scale, Set Scale offers, largest first, each with the power
        // of ten it stands at against the metre. Every one is a power of ten, so moving
        // a reading between them shifts the exponent instead of looking up a ratio.
        private static readonly string[] ScaleBarUnitNames =
        {
            "km", "hm", "dam", "m", "dm", "cm", "mm", "µm", "nm"
        };

        private static readonly int[] ScaleBarUnitPowers = { 3, 2, 1, 0, -1, -2, -3, -6, -9 };

        // What one division may count, times a power of ten. Ten of a unit is as often
        // far wider than the view as it is too small to see, so the division carries
        // the difference. One, two and five are the multiples a reader can still count
        // in their head.
        private static readonly double[] ScaleBarStepMantissas = { 1, 2, 5 };

        // Divisions the grip may be pulled to. More of them than the bar picks for
        // itself, so dragging moves in small steps instead of jumping between three
        // lengths a decade, and each still counts in numbers a reader can follow.
        private static readonly double[] ScaleBarDragMantissas =
        {
            1, 1.5, 2, 2.5, 3, 4, 5, 6, 8
        };

        private const int ScaleBarMinStepPower = -6;
        private const int ScaleBarMaxStepPower = 6;

        // Divisions worth naming a unit around. Below one they read as fractions and
        // above a thousand as a wall of digits, so a unit that cannot take a division
        // in between is the wrong unit for this zoom. The window is a thousand wide
        // because that is the widest gap on the ladder, between mm and um.
        private const double ScaleBarMinWholeStep = 1.0;
        private const double ScaleBarMaxWholeStep = 1000.0;

        // A bar reading plainly 0 to 10 is worth having at this length, even where a
        // stepped reading would run longer.
        private const double ScaleBarPlainLength = 180.0;

        // Shortest and longest the grip will pull the bar to. The low end keeps a bar
        // that has been dragged shut from vanishing; the high end is the view itself.
        private const double ScaleBarMinDragLength = 80.0;

        // Where the pointer and the overlay stood when the drag began, so the bar
        // follows the cursor instead of jumping its corner under it.
        private Point _scaleBarDragPointer;
        private Point _scaleBarDragOrigin;
        private bool _scaleBarDragging;

        // The length the grip was last pulled to, or null while the bar is sizing itself
        // to the view. A length in screen pixels rather than in units, so zooming keeps
        // the bar the size it was asked to be and changes what it spans instead, which
        // is the behaviour the unit ladder is there for.
        private double? _scaleBarTargetLength;

        // The bar as last drawn: where the grip drag starts from, since the reading is
        // snapped and the drawn length is not whatever the pointer last asked for.
        private double _scaleBarDrawnLength;

        private double _scaleBarGripPointer;
        private double _scaleBarGripLength;
        private bool _scaleBarGripping;

        /// Shows the bar when it is both asked for and possible, and greys the menu
        /// item when it is not. A specimen either carries a scale or does not, so this
        /// is not a preference to be remembered the way the Settings switches are: it
        /// follows the specimen on screen.
        internal void UpdateScaleBarVisibility()
        {
            if (UI_MenuViewScaleBar == null || UI_ScaleBar == null) return;

            bool calibrated = ScaleCalibration.IsCalibrated;
            UI_MenuViewScaleBar.IsEnabled = calibrated;

            bool show = calibrated && UI_MenuViewScaleBar.IsChecked;
            UI_ScaleBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // Drawn on the way in rather than left as it was, since the scale and the
            // zoom both move while it is hidden.
            if (show) RedrawScaleBar();
        }

        /// Redraws the bar for the calibration and the zoom in force. A bar that cannot
        /// be drawn says why in its place, so the overlay is never blank without a
        /// reason.
        private void RedrawScaleBar()
        {
            if (UI_ScaleBar == null || UI_ScaleBarCanvas == null) return;
            if (UI_ScaleBar.Visibility != Visibility.Visible) return;

            UI_ScaleBarCanvas.Children.Clear();
            UI_ScaleBarNote.Visibility = Visibility.Collapsed;

            if (!ScaleCalibration.IsCalibrated || ScaleCalibration.UnitsPerPixel <= 0)
            {
                ShowScaleBarNote("No scale set for this specimen. Use Tools, Scale, Set Scale.");
                return;
            }

            // A scale set from outside the program can name a unit the bar has no rung
            // for, and it is the unit rather than the scale that is the trouble. Said
            // apart from the line above so it does not read as no scale at all.
            if (ScaleBarPixelsPerUnit(ScaleCalibration.Unit) <= 0)
            {
                ShowScaleBarNote("The scalebar reads in metric units, and this specimen "
                                 + "is scaled in " + ScaleCalibration.Unit + ".");
                return;
            }

            if (!ChooseScaleBar(out string unit, out double step))
            {
                ShowScaleBarNote("No scalebar fits this view at this scale. Zoom, or widen the window.");
                return;
            }

            DrawScaleBar(ScaleBarPixelsPerUnit(unit) * step, unit, step);
        }

        // Eleven labels over ten segments, the segments alternating filled and open so
        // the divisions can be counted at a glance, and a tick rising out of the bar
        // under each number so it can be read against the exact place it marks. The
        // unit is captioned on the bar, which is what a screenshot carries away.
        private void DrawScaleBar(double perDivision, string unit, double step)
        {
            double length = perDivision * ScaleBarIntervals;
            double barTop = ScaleBarLabelRow + ScaleBarTickHeight;

            // Each number is centred on its own tick, so the first hangs back past the
            // bar's left end and the last past its right. Both are measured before
            // anything is placed: the bar starts far enough in, and the unit sits far
            // enough out, for the whole of each to land inside the panel.
            var labels = new TextBlock[ScaleBarIntervals + 1];
            for (int i = 0; i <= ScaleBarIntervals; i++)
                labels[i] = ScaleBarText(FormatScaleBarLabel(i * step, step), 10, FontWeights.Normal);

            double left = Math.Max(ScaleBarPad, labels[0].DesiredSize.Width / 2);

            for (int i = 0; i < ScaleBarIntervals; i++)
            {
                var segment = new Rectangle
                {
                    Width = perDivision,
                    Height = ScaleBarHeight,
                    Fill = i % 2 == 0 ? Brushes.Black : Brushes.White,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1
                };

                Canvas.SetLeft(segment, left + i * perDivision);
                Canvas.SetTop(segment, barTop);
                UI_ScaleBarCanvas.Children.Add(segment);
            }

            for (int i = 0; i <= ScaleBarIntervals; i++)
            {
                double x = left + i * perDivision;

                // Run a pixel into the bar rather than stopping on its edge, so the
                // tick and the division it marks read as one mark.
                var tick = new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = ScaleBarLabelRow,
                    Y2 = barTop + 1,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    SnapsToDevicePixels = true
                };

                UI_ScaleBarCanvas.Children.Add(tick);

                Canvas.SetLeft(labels[i], x - labels[i].DesiredSize.Width / 2);
                Canvas.SetTop(labels[i], 0);
                UI_ScaleBarCanvas.Children.Add(labels[i]);
            }

            var caption = ScaleBarText(unit, 11, FontWeights.Bold);

            double captionLeft = left + length
                + Math.Max(6.0, labels[ScaleBarIntervals].DesiredSize.Width / 2 + 4.0);

            Canvas.SetLeft(caption, captionLeft);
            Canvas.SetTop(caption, barTop - 1);
            UI_ScaleBarCanvas.Children.Add(caption);

            UI_ScaleBarCanvas.Width = captionLeft + caption.DesiredSize.Width + ScaleBarPad;
            UI_ScaleBarCanvas.Height = barTop + ScaleBarHeight + 2;

            _scaleBarDrawnLength = length;
        }

        // Measured as it is made, since where a number goes depends on how wide it is.
        private static TextBlock ScaleBarText(string text, double size, FontWeight weight)
        {
            var block = new TextBlock
            {
                Text = text,
                Foreground = Brushes.Black,
                FontSize = size,
                FontWeight = weight
            };

            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return block;
        }

        // As many decimals as the division in use and no more, so a bar counting whole
        // units is not labelled 0.0, 1.0, 2.0.
        private static string FormatScaleBarLabel(double value, double step)
        {
            int decimals = 0;
            double scaled = step;

            while (decimals < 6 && Math.Abs(scaled - Math.Round(scaled)) > 1e-9)
            {
                scaled *= 10;
                decimals++;
            }

            return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                                  CultureInfo.CurrentCulture);
        }

        private void ShowScaleBarNote(string text)
        {
            UI_ScaleBarNote.Text = text;
            UI_ScaleBarNote.Visibility = Visibility.Visible;
            UI_ScaleBarCanvas.Width = 0;
            UI_ScaleBarCanvas.Height = 0;

            // There is no bar on screen, so there is no length for the grip to be
            // pulled from. Left at the last drawn one it would arm a length measured
            // against a bar the user cannot see.
            _scaleBarDrawnLength = 0;
        }

        // Pixels on screen that one unit of the named kind covers. The calibration
        // gives units per canvas pixel for the layout the image is drawn at, and the
        // zoom multiplies on top of that, because zoom is a render transform the layout
        // never sees. Zero when there is no scale to read, or when the unit is off the
        // ladder, which tells the caller not to draw in it rather than to draw it wrong.
        private double ScaleBarPixelsPerUnit(string unit)
        {
            if (!ScaleCalibration.IsCalibrated) return 0;

            double unitsPerPixel = ScaleCalibration.UnitsPerPixel;
            if (unitsPerPixel <= 0) return 0;

            double factor = ScaleBarUnitFactor(unit, ScaleCalibration.Unit);
            if (factor <= 0) return 0;

            double zoom = ScaleBarZoom();
            if (zoom <= 0) return 0;

            return factor * zoom / unitsPerPixel;
        }

        // The magnification the image is drawn at, or zero when it cannot be read. One
        // is the unzoomed view, which is where the bar is held to the specimen's own
        // unit.
        private double ScaleBarZoom()
        {
            double zoom = Math.Abs(UI_WorkImage.GetScaleTransform().ScaleX);
            return zoom > 0 && !double.IsNaN(zoom) && !double.IsInfinity(zoom) ? zoom : 0;
        }

        // How many of the calibration's units one of the named units makes. Zero when
        // either name is off the ladder.
        private static double ScaleBarUnitFactor(string from, string to)
        {
            int? fromPower = ScaleBarPowerOf(from);
            int? toPower = ScaleBarPowerOf(to);

            if (fromPower == null || toPower == null) return 0;

            return Math.Pow(10, fromPower.Value - toPower.Value);
        }

        private static int? ScaleBarPowerOf(string unit)
        {
            if (string.IsNullOrEmpty(unit)) return null;

            for (int i = 0; i < ScaleBarUnitNames.Length; i++)
            {
                if (string.Equals(ScaleBarUnitNames[i], unit, StringComparison.Ordinal))
                    return ScaleBarUnitPowers[i];
            }

            return null;
        }

        /// The unit and division the bar is drawn in. Unzoomed it reads in the unit the
        /// specimen was scaled in, which is the reading that was asked for and the one
        /// every other zoom is judged against. Zoomed, the ladder is free to move, so
        /// going in reads in a smaller unit and going out in a larger one. False when
        /// nothing fits, which leaves the caller to say so.
        private bool ChooseScaleBar(out string unit, out double step)
        {
            string scaled = ScaleCalibration.Unit;
            double? target = _scaleBarTargetLength;

            if (ScaleBarZoom() == 1.0)
            {
                // Unzoomed the unit is the specimen's own, so there is only a division
                // to look for, and it may be a fraction of the unit.
                unit = scaled;
                return ChooseScaleBarStep(ScaleBarPixelsPerUnit(unit), false, target, out step);
            }

            if (!ChooseScaleBarUnit(out unit, out step))
            {
                // No unit on the ladder takes a whole division at this zoom, so the one
                // the specimen was scaled in is used with whatever division fits.
                unit = scaled;
                return ChooseScaleBarStep(ScaleBarPixelsPerUnit(unit), false, target, out step);
            }

            // The unit above was chosen as though the bar were sizing itself, which is
            // what keeps it suited to the zoom and is why the length asked for has no
            // say in it. The division is then taken again against that length, fractions
            // allowed, so the grip reaches anything the scale can honestly show.
            if (target != null)
                return ChooseScaleBarStep(ScaleBarPixelsPerUnit(unit), false, target, out step);

            return true;
        }

        // The unit the bar would size itself to at this zoom, with the whole division
        // that names it. Only whole divisions name a unit: one that needs a fraction of
        // itself is the wrong unit for this zoom, and the next rung down is the right one.
        private bool ChooseScaleBarUnit(out string unit, out double step)
        {
            unit = null;
            step = 1;

            double bestLength = 0;
            string plainUnit = null;
            double plainLength = 0;

            // Largest unit first, so of two readings of the same length the one whose
            // division counts fewest units wins: naming the same bar 0 to 10 cm rather
            // than 0 to 100 mm is what moves the unit as the view is zoomed.
            foreach (string candidate in ScaleBarUnitNames)
            {
                double perUnit = ScaleBarPixelsPerUnit(candidate);
                if (!ChooseScaleBarStep(perUnit, true, null, out double candidateStep)) continue;

                double length = perUnit * candidateStep * ScaleBarIntervals;

                if (candidateStep == 1 && length >= ScaleBarPlainLength && length > plainLength)
                {
                    plainUnit = candidate;
                    plainLength = length;
                }

                // Two namings of one length differ only in the last bits, so a hair
                // longer does not count as longer and the larger unit keeps the tie.
                if (length <= bestLength * (1 + 1e-9)) continue;

                unit = candidate;
                step = candidateStep;
                bestLength = length;
            }

            // A reading that counts 0 to 10 is the plainest there is, so it is taken
            // wherever one is long enough to read, even over a longer stepped bar.
            if (plainUnit != null)
            {
                unit = plainUnit;
                step = 1;
            }

            return unit != null;
        }

        // How much of the unit one division counts. A division of one is preferred
        // wherever it fits, so the bar reads plainly 0 to 10 whenever the scale allows
        // it; otherwise the longest reading that still fits is taken. False when
        // nothing fits, which leaves the caller to say so rather than draw a bar that
        // cannot be read.
        private bool ChooseScaleBarStep(
            double perUnit, bool wholeDivisionsOnly, double? target, out double step)
        {
            step = 1;
            if (perUnit <= 0) return false;

            double available = ScaleBarAvailableWidth();

            // A plain reading of 0 to 10 is preferred wherever it fits, but only while
            // the bar is sizing itself: a length asked for by dragging outranks it.
            double plain = perUnit * ScaleBarIntervals;
            if (target == null
                && plain >= ScaleBarMinLengthFor(1) && plain <= available) return true;

            // The grip is offered the finer set, since its whole purpose is to land on
            // a length rather than on the one reading the bar would have chosen.
            double[] mantissas = target == null ? ScaleBarStepMantissas : ScaleBarDragMantissas;

            double best = 0;
            double bestScore = 0;

            for (int power = ScaleBarMinStepPower; power <= ScaleBarMaxStepPower; power++)
            {
                foreach (double mantissa in mantissas)
                {
                    double candidate = mantissa * Math.Pow(10, power);

                    if (wholeDivisionsOnly
                        && (candidate < ScaleBarMinWholeStep || candidate >= ScaleBarMaxWholeStep))
                        continue;

                    double length = perUnit * candidate * ScaleBarIntervals;

                    if (length < ScaleBarMinLengthFor(candidate) || length > available) continue;

                    double score = ScaleBarScore(length, target);
                    if (score <= bestScore) continue;

                    best = candidate;
                    bestScore = score;
                }
            }

            if (best > 0)
            {
                step = best;
                return true;
            }

            // A unit being searched over drops out here and another is tried instead.
            if (wholeDivisionsOnly) return false;

            // Nothing leaves the numbers all the room they would like. The divisions on
            // offer step by two or two and a half, so at some zooms none of them does;
            // the one that comes closest is drawn rather than no bar at all.
            double closest = 0;
            double bestShare = 0;

            for (int power = ScaleBarMinStepPower; power <= ScaleBarMaxStepPower; power++)
            {
                foreach (double mantissa in mantissas)
                {
                    double candidate = mantissa * Math.Pow(10, power);
                    double length = perUnit * candidate * ScaleBarIntervals;

                    if (length <= 0 || length > available) continue;

                    double share = length / ScaleBarMinLengthFor(candidate);
                    if (share <= bestShare) continue;

                    closest = candidate;
                    bestShare = share;
                }
            }

            if (closest <= 0) return false;

            step = closest;
            return true;
        }

        // How well a length suits the bar, larger scoring better. Dragged to a length,
        // the reading nearest it wins; left alone, the longest reading wins, since a
        // longer bar is read the more finely. Nearness is judged as a ratio rather than
        // a difference, so a reading half the length asked for and one twice it are
        // equally far off.
        private static double ScaleBarScore(double length, double? target)
        {
            if (target == null || target.Value <= 0) return length;

            double ratio = length / target.Value;
            return ratio > 1 ? 1.0 / ratio : ratio;
        }

        // The shortest bar this division can be labelled on without its numbers
        // touching. The largest of the eleven is the one that has to fit, and every
        // division gets the same room as that one.
        private static double ScaleBarMinLengthFor(double step)
        {
            string widest = FormatScaleBarLabel(step * ScaleBarIntervals, step);
            return ScaleBarIntervals * (widest.Length * ScaleBarDigitWidth + ScaleBarLabelGap);
        }

        // Room the bar has across the workspace, less the margin it sits in and the
        // space its unit caption needs.
        private double ScaleBarAvailableWidth()
        {
            double width = UI_WorkSpace.ActualWidth;
            return width > 180 ? width - 115 : 0;
        }

        /// Pulls the bar longer or shorter. Only the sideways travel is read, so the
        /// bar keeps its height, and the reading is snapped to one the scale can
        /// actually show: a bar of any length the pointer happened to stop at would be
        /// a bar that lies. Double-clicking hands the length back to the view.
        private void ScaleBarGrip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2)
            {
                _scaleBarTargetLength = null;
                RedrawScaleBar();
                e.Handled = true;
                return;
            }

            // Nothing drawn, nothing to pull. The double-click above still works, since
            // handing the length back to the view is the one thing worth doing here.
            if (_scaleBarDrawnLength <= 0)
            {
                e.Handled = true;
                return;
            }

            _scaleBarGripPointer = e.GetPosition(UI_WorkSpace).X;
            _scaleBarGripLength = _scaleBarDrawnLength;
            _scaleBarGripping = UI_ScaleBarGrip.CaptureMouse();
            e.Handled = true;
        }

        private void ScaleBarGrip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_scaleBarGripping) return;

            double asked = _scaleBarGripLength + (e.GetPosition(UI_WorkSpace).X - _scaleBarGripPointer);
            double available = ScaleBarAvailableWidth();
            if (available <= 0) return;

            // Readings sit a quarter apart at the closest, so most of a drag's moves ask
            // for a length already being shown. Redrawing for those would rebuild the
            // bar dozens of times a second and invalidate layout with it, which WPF
            // answers with another move on the captured element.
            double wanted = Math.Min(Math.Max(asked, ScaleBarMinDragLength), available);
            if (_scaleBarTargetLength == wanted) return;

            _scaleBarTargetLength = wanted;
            RedrawScaleBar();
        }

        private void ScaleBarGrip_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_scaleBarGripping) return;

            _scaleBarGripping = false;
            UI_ScaleBarGrip.ReleaseMouseCapture();
            e.Handled = true;
        }

        /// <summary>Ends a grip drag the button never finished.</summary>
        private void ScaleBarGrip_LostCapture(object sender, MouseEventArgs e)
        {
            _scaleBarGripping = false;
        }

        private void ScaleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // A press on the grip resizes rather than moves. The grip marks its own
            // events handled, so this is a second line rather than the only one.
            if (UI_ScaleBarGrip.IsMouseOver) return;

            _scaleBarDragPointer = e.GetPosition(UI_WorkSpace);
            _scaleBarDragOrigin = new Point(UI_ScaleBarTransform.X, UI_ScaleBarTransform.Y);
            _scaleBarDragging = UI_ScaleBar.CaptureMouse();
            e.Handled = true;
        }

        private void ScaleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_scaleBarDragging) return;

            Point now = e.GetPosition(UI_WorkSpace);
            UI_ScaleBarTransform.X = _scaleBarDragOrigin.X + (now.X - _scaleBarDragPointer.X);
            UI_ScaleBarTransform.Y = _scaleBarDragOrigin.Y + (now.Y - _scaleBarDragPointer.Y);
        }

        private void ScaleBar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_scaleBarDragging) return;

            _scaleBarDragging = false;
            UI_ScaleBar.ReleaseMouseCapture();
            e.Handled = true;
        }

        /// Ends a drag the button never finished, such as one interrupted by a dialog.
        /// Without this the bar would keep following the cursor across the workspace.
        private void ScaleBar_LostCapture(object sender, MouseEventArgs e)
        {
            _scaleBarDragging = false;
        }
    }
}
