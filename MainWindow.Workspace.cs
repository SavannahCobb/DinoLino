using DinoLino.DataTypes;
using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DinoLino
{
    /// <summary>
    /// Working-image lifecycle, workspace transforms, image export, picture adjustment, and scale calibration.
    /// </summary>
    public partial class MainWindow
    {
        // =====================
        // Image adjustment
        // =====================

        private BitmapSource _originalImageSource;
        private ImageAdjuster _imageAdjuster = new ImageAdjuster();

        // The loaded specimen's correction values, which the dialog reopens with. They
        // live on the specimen, so opening another image starts from zero and coming
        // back to this one restores what it was last set to.
        public PictureCorrections PictureCorrections = new PictureCorrections();

        // =====================
        // Scale capture
        // =====================

        private bool _scaleMode = false;
        private int _scaleClicks = 0;
        private Line _scaleLine;

        // Marker that follows the cursor while scale capture is active, so the
        // user can see that the tool is armed.
        private Ellipse _scaleCueDot;

        // =====================
        // Workspace reset
        // =====================

        /// <summary>
        /// Clears the active operation history and the current workspace visuals.
        /// </summary>
        private void ClearAllOperations()
        {
            UndoRedoManager?.Clear();
            ClearWorkspace();
        }

        /// <summary>
        /// Removes workspace overlays, restores the cursor, and resets the active mode.
        /// </summary>
        private void ClearWorkspace()
        {
            // A hard reset invalidates any half-finished calibration line, so end
            // the capture and remove its cue before wiping the canvas.
            CancelScaleCapture();
            CancelAlignCapture();

            UI_WorkCanvas.Children.Clear();
            AddElementToWorkSpace(UI_DotCursor);
            UI_DotCursor.SetPosition(0, 0);

            // Clear any outline-specific preview that depends on the previous workspace state.
            OutlineMode?.ClearEFDPreview();

            CurrentWorkMode.Reset();
        }

        /// <summary>
        /// Ensures an element is attached to the workspace canvas and not to another parent.
        /// </summary>
        private void AddElementToWorkSpace(UIElement element)
        {
            if (element == null) return;

            if (element is FrameworkElement fe && fe.Parent is Panel logicalPanel)
            {
                logicalPanel.Children.Remove(element);
            }
            else
            {
                // Some workspace elements may still be attached through the visual tree.
                var visualParent = VisualTreeHelper.GetParent(element);
                if (visualParent is Panel visualPanel)
                    visualPanel.Children.Remove(element);
            }

            if (!UI_WorkCanvas.Children.Contains(element))
                UI_WorkCanvas.Children.Add(element);
        }

        // =====================
        // Zoom
        // =====================

        // The smallest and largest zoom values the user can reach.
        // 0.05 = 5%; 16.0 = 1600%.
        private const double MinimumWorkSpaceZoom = 0.05;
        private const double MaximumWorkSpaceZoom = 16.0;

        // Stops LostKeyboardFocus from trying to parse the percentage text while this
        // code is programmatically refreshing it.
        private bool _updatingZoomPercentText;

        /// <summary>
        /// Returns value constrained to the inclusive range minimum through maximum.
        /// Kept here instead of Math.Clamp so this works with older .NET targets.
        /// </summary>
        private static double LimitZoom(double value, double minimum, double maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        /// <summary>
        /// Gets the ScaleTransform used by UI_WorkImage's existing RenderTransform.
        ///
        /// This expects UI_WorkImage.RenderTransform to be a TransformGroup containing
        /// one ScaleTransform and one TranslateTransform, which is also what the
        /// existing ZoomElement / GetTranslateTransform code should be using.
        /// </summary>
        private ScaleTransform GetWorkSpaceScaleTransform()
        {
            TransformGroup group = UI_WorkImage.RenderTransform as TransformGroup;

            if (group == null)
            {
                throw new InvalidOperationException(
                    "UI_WorkImage.RenderTransform must be a TransformGroup containing " +
                    "a ScaleTransform and a TranslateTransform.");
            }

            foreach (Transform transform in group.Children)
            {
                ScaleTransform scale = transform as ScaleTransform;

                if (scale != null)
                    return scale;
            }

            throw new InvalidOperationException(
                "UI_WorkImage.RenderTransform does not contain a ScaleTransform.");
        }

        /// <summary>
        /// Returns the current zoom factor: 1.0 is 100%, 0.5 is 50%, and 2.0 is 200%.
        /// </summary>
        private double GetWorkSpaceZoomFactor()
        {
            return GetWorkSpaceScaleTransform().ScaleX;
        }

        /// <summary>
        /// Updates the visible percentage field to match the current image scale.
        /// </summary>
        private void UpdateZoomPercentBox()
        {
            if (UI_ZoomPercent == null)
                return;

            _updatingZoomPercentText = true;

            try
            {
                double percent = GetWorkSpaceZoomFactor() * 100.0;

                // Examples: 100, 66.67, 250. Avoids displaying unnecessary zeroes.
                UI_ZoomPercent.Text = percent.ToString("0.##");
            }
            finally
            {
                _updatingZoomPercentText = false;
            }
        }

        /// <summary>
        /// Copies the image transforms to every visual that must track the image, then
        /// redraws view-dependent items.
        ///
        /// Add other CopyTransforms calls here only if those elements need to move and
        /// zoom with the image. UI_WorkBorder already does in the existing code.
        /// </summary>
        private void RefreshWorkSpaceZoomVisuals()
        {
            UI_WorkBorder.CopyTransforms(UI_WorkImage);

            // If these layers use their own transforms and must follow the image,
            // uncomment/adapt the appropriate lines:
            //
            // UI_WorkCanvas.CopyTransforms(UI_WorkImage);
            // UI_LabelCanvas.CopyTransforms(UI_WorkImage);

            // The bar is a length on screen, so magnifying the image lengthens it.
            RedrawScaleBar();

            // Keep the new percentage control synchronized with wheel zoom, fit,
            // reset, and direct percentage entry.
            UpdateZoomPercentBox();
        }

        /// <summary>
        /// Changes the image scale while keeping the image point under anchorInImage
        /// at the same displayed position. This is useful for percentage entry and
        /// programmatic zoom commands.
        ///
        /// anchorInImage must be expressed in UI_WorkImage coordinates.
        /// </summary>
        private void SetWorkSpaceZoom(double requestedScale, Point anchorInImage)
        {
            double newScale = LimitZoom(
                requestedScale,
                MinimumWorkSpaceZoom,
                MaximumWorkSpaceZoom);

            ScaleTransform scale = GetWorkSpaceScaleTransform();
            TranslateTransform translate = UI_WorkImage.GetTranslateTransform();

            double oldScale = scale.ScaleX;

            // Guard against an invalid or zero transform scale. The normal image state
            // should always be positive, but this avoids a divide-by-zero failure.
            if (oldScale <= 0)
                oldScale = 1.0;

            // If the requested scale is already in use, nothing needs moving. Refresh
            // the field anyway, in case it contained incomplete or invalid user text.
            if (Math.Abs(newScale - scale.ScaleX) < 0.000001)
            {
                RefreshWorkSpaceZoomVisuals();
                return;
            }

            /*
             * Preserve the image point beneath the anchor:
             *
             * imagePoint = (anchor - translation) / oldScale
             * newTranslation = anchor - imagePoint * newScale
             *
             * This prevents a percentage change from appearing to jump to a different
             * part of the photograph.
             */
            double imageX = (anchorInImage.X - translate.X) / oldScale;
            double imageY = (anchorInImage.Y - translate.Y) / oldScale;

            scale.ScaleX = newScale;
            scale.ScaleY = newScale;

            translate.X = anchorInImage.X - imageX * newScale;
            translate.Y = anchorInImage.Y - imageY * newScale;

            RefreshWorkSpaceZoomVisuals();
        }

        /// <summary>
        /// Returns the centre of the visible workspace expressed in UI_WorkImage
        /// coordinates. Explicit zoom commands use this as their stable anchor.
        /// </summary>
        private Point GetWorkSpaceCentreInImageCoordinates()
        {
            Point centreInWorkSpace = new Point(
                UI_WorkSpace.ActualWidth / 2.0,
                UI_WorkSpace.ActualHeight / 2.0);

            return UI_WorkSpace.TranslatePoint(centreInWorkSpace, UI_WorkImage);
        }

        /// <summary>
        /// Applies a scale that displays the entire image inside the workspace while
        /// preserving the image aspect ratio, then centres it in the workspace.
        /// </summary>
        private void FitWorkSpaceImageToWindow()
        {
            // Leave a small visible gap between the image and workspace edges.
            const double fitMargin = 16.0;

            double availableWidth = UI_WorkSpace.ActualWidth - fitMargin * 2.0;
            double availableHeight = UI_WorkSpace.ActualHeight - fitMargin * 2.0;

            // ActualWidth and ActualHeight are the untransformed layout dimensions.
            // They must be non-zero before fit can be calculated.
            double imageWidth = UI_WorkImage.ActualWidth;
            double imageHeight = UI_WorkImage.ActualHeight;

            if (availableWidth <= 0 ||
                availableHeight <= 0 ||
                imageWidth <= 0 ||
                imageHeight <= 0)
            {
                return;
            }

            // The smaller factor ensures both image dimensions fit in the available
            // viewport and keeps the photograph's aspect ratio intact.
            double fitScale = Math.Min(
                availableWidth / imageWidth,
                availableHeight / imageHeight);

            fitScale = LimitZoom(
                fitScale,
                MinimumWorkSpaceZoom,
                MaximumWorkSpaceZoom);

            ScaleTransform scale = GetWorkSpaceScaleTransform();
            TranslateTransform translate = UI_WorkImage.GetTranslateTransform();

            scale.ScaleX = fitScale;
            scale.ScaleY = fitScale;

            // Place the scaled image in the middle of the workspace.
            translate.X = (UI_WorkSpace.ActualWidth - imageWidth * fitScale) / 2.0;
            translate.Y = (UI_WorkSpace.ActualHeight - imageHeight * fitScale) / 2.0;

            RefreshWorkSpaceZoomVisuals();
        }

        /// <summary>
        /// Handles mouse-wheel zoom. The existing ZoomElement implementation remains
        /// responsible for its current wheel increment and cursor-centred behavior.
        /// </summary>
        private void UpdateWorkSpaceZoom(double delta, Point relativeTo)
        {
            UI_WorkImage.ZoomElement(delta, relativeTo);

            RefreshWorkSpaceZoomVisuals();
        }

        /// <summary>
        /// Restores the image's existing default zoom behavior, normally 100% with no
        /// pan offset, and refreshes the explicit zoom controls afterwards.
        /// </summary>
        private void ResetWorkSpaceZoom()
        {
            UI_WorkImage.ResetZoom();

            RefreshWorkSpaceZoomVisuals();
        }

        /// <summary>
        /// Commits a number typed into the Zoom percentage box. The user can type
        /// either "125" or "125%".
        /// </summary>
        private void ApplyZoomPercentText()
        {
            if (UI_ZoomPercent == null)
                return;

            string text = UI_ZoomPercent.Text;

            if (text == null)
            {
                UpdateZoomPercentBox();
                return;
            }

            text = text.Trim();

            // Allow users to type either "150" or "150%".
            if (text.EndsWith("%"))
                text = text.Substring(0, text.Length - 1).Trim();

            double percentage;

            bool validPercentage = double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture,
                out percentage);

            // Zero and negative zooms do not make sense. Invalid text is replaced by
            // the actual current zoom instead of leaving misleading text in the box.
            if (!validPercentage || percentage <= 0)
            {
                UpdateZoomPercentBox();
                return;
            }

            double requestedScale = percentage / 100.0;

            SetWorkSpaceZoom(
                requestedScale,
                GetWorkSpaceCentreInImageCoordinates());
        }

        /// <summary>
        /// Enter applies the typed percentage. Escape discards edits and restores the
        /// currently active zoom percentage.
        /// </summary>
        private void ZoomPercent_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyZoomPercentText();

                // Return focus to the workspace so measurement/drawing shortcuts work
                // immediately after entering a zoom value.
                UI_WorkCanvas.Focus();

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                UpdateZoomPercentBox();

                UI_WorkCanvas.Focus();

                e.Handled = true;
            }
        }

        /// <summary>
        /// Applying on focus loss also covers clicking Fit, 100%, or another control
        /// after typing a value, without requiring the user to press Enter first.
        /// </summary>
        private void ZoomPercent_LostKeyboardFocus(
            object sender,
            KeyboardFocusChangedEventArgs e)
        {
            if (_updatingZoomPercentText)
                return;

            ApplyZoomPercentText();
        }

        /// <summary>
        /// Fits the whole image into the available workspace.
        /// </summary>
        private void ZoomFit_Click(object sender, RoutedEventArgs e)
        {
            FitWorkSpaceImageToWindow();

            UI_WorkCanvas.Focus();
        }

        /// <summary>
        /// Restores the existing 100% / reset view behavior.
        /// </summary>
        private void Zoom100_Click(object sender, RoutedEventArgs e)
        {
            ResetWorkSpaceZoom();

            UI_WorkCanvas.Focus();
        }


        // =====================
        // Open image
        // =====================

        private void Menu_OpenImage(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                // Start in the Directory panel's working folder when one is set.
                InitialDirectory = DialogInitialDirectory,

                // Several images may be picked at once, the way Explorer picks them:
                // Shift or Ctrl with a click, or a box dragged round them.
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            string[] chosen = openFileDialog.FileNames;

            // One file opens exactly as it always has.
            if (chosen.Length <= 1)
            {
                OpenImageFromPath(openFileDialog.FileName);
                return;
            }

            OpenImagesFromPaths(chosen);
        }

        /// Loads an image file as a new specimen. Shared by the File menu and the
        /// Directory panel, so it validates the file rather than trusting the caller.
        internal void OpenImageFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            BitmapImage bmp;
            try
            {
                // OnLoad reads the file up front and releases the handle, so the image
                // can still be renamed or deleted from the Directory panel afterwards.
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);
                bmp.EndInit();
                bmp.Freeze();
            }
            catch (Exception ex)
            {
                AppLog.WriteException("Could not open image " + System.IO.Path.GetFileName(path), ex);

                MessageBox.Show(this,
                    $"Could not open this image:\n{ex.Message}",
                    "Open Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AppLog.Write($"Opened image {System.IO.Path.GetFileName(path)} ({bmp.PixelWidth} x {bmp.PixelHeight})");

            // Only stash the outgoing specimen once the new image has actually loaded.
            if (SpecimenManager.HasOpenedImage)
                UndoRedoManager.StashActiveSpecimen(SpecimenManager.CurrentSpecimen, SpecimenManager.DisplayName);

            SetWorkspaceImage(
                bmp, System.IO.Path.GetFileName(path), registerAsNewSpecimen: true,
                sourcePath: FullPath(path));

            // A 2D image does not use the 3D reposition workflow.
            _workingImageIsModelCapture = false;
            _activeMesh = null;
            _activeModelName = null;
            UI_MenuReposition3D.IsEnabled = false;
        }

        /// The absolute form of a path, or the path itself when it cannot be resolved.
        /// Stored on the specimen so a saved session can find the photograph again.
        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
        /// <summary>
        /// Loads an image into the workspace and refreshes all state derived from it.
        /// When a new specimen is opened, preserves an unmodified image copy so
        /// Reset Transform can restore its original orientation.
        /// </summary>
        private void SetWorkspaceImage(
            BitmapSource bmp,
            string specimenName,
            bool registerAsNewSpecimen,
            string sourcePath = null)
        {
            // Before the specimen changes underneath it.
            CloseAdjustmentWindow();

            // Only replace the stored original when a genuinely new image/specimen
            // enters the workspace. Calls made for rotate, flip, or reset must not
            // overwrite this preserved source.
            if (registerAsNewSpecimen && bmp != null)
            {
                _originalImageSource = bmp.CloneCurrentValue();

                if (_originalImageSource.CanFreeze)
                    _originalImageSource.Freeze();
            }

            WorkingImage = bmp;
            UI_WorkImage.Source = WorkingImage;

            if (registerAsNewSpecimen)
                SpecimenManager.OnImageOpened(bmp, specimenName, sourcePath);

            // Scale, alignment and picture corrections all belong to the specimen and
            // come back with it.
            ScaleCalibration.BindTo(SpecimenManager.CurrentSpecimen);
            ImageAlignment.BindTo(SpecimenManager.CurrentSpecimen);
            ActiveAlignment.Bind(ImageAlignment);
            PictureCorrections.BindTo(SpecimenManager.CurrentSpecimen);

            ResetWorkSpaceZoom();
            ClearWorkspace();
            RefreshAllScalePlaceholders();

            _imageAdjuster.CacheImage(WorkingImage);
            SetImageToolsEnabled(true);
            OutlineMode.SourceImage = WorkingImage;

            // After the outline source is set, since rendering corrections replaces it
            // with the corrected pixels. Bound above, so this brings back the arriving
            // specimen's own corrections, and does nothing for one that has none.
            ReapplyImageAdjustments();

            InitialiseWorkSpaceZoomForLoadedImage();

            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(SyncOutlineImageTransform));
        }

        /// Empties the workspace entirely. Used when the loaded specimen is deleted and
        /// no other specimen still holds an image to fall back to.
        internal void ClearWorkspaceImage()
        {
            CloseAdjustmentWindow();

            WorkingImage = null;
            UI_WorkImage.Source = null;

            ScaleCalibration.BindTo(null);
            ImageAlignment.BindTo(null);
            PictureCorrections.BindTo(null);
            SetImageToolsEnabled(false);
            ResetWorkSpaceZoom();
            ClearWorkspace();
            RefreshAllScalePlaceholders();

            // OutlineMode treats a null source as "no image", which stops its pending
            // analysis and makes clicks no-op.
            OutlineMode.SourceImage = null;

            // A blank workspace is not a 3D capture either.
            _workingImageIsModelCapture = false;
            _activeMesh = null;
            _activeModelName = null;
            UI_MenuReposition3D.IsEnabled = false;
        }

        // Set while the mapping is being read a second time after layout, so that
        // reading cannot ask for a third.
        private bool _rereadingImageOrigin;
        private bool _imageOriginRereadQueued;

        /// <summary>
        /// Where the picture's top left corner falls on the canvas the modes draw on.
        /// </summary>
        /// <remarks>
        /// The canvas sits inside UI_WorkBorder, which takes its size from the picture
        /// through a binding and is centred in the same cell. SizeChanged is raised on
        /// the picture after that binding has been written but before the border has
        /// been arranged again, so for that moment the border is still centred at its
        /// old size and the distance from it to the picture is out by half the change
        /// in size: half the picture, when the picture has just appeared. An origin
        /// read then shifts every label by that much, lets a click store a position
        /// off the picture, and holds a dragged label at the wrong edge, since the edge
        /// is found through the same origin.
        ///
        /// While the two sizes disagree the origin is taken from where the canvas sits
        /// inside the border, which is where they come to rest, and the mapping is read
        /// once more when layout has finished.
        /// </remarks>
        private Point ImageOriginOnCanvas()
        {
            bool settled =
                Math.Abs(UI_WorkBorder.ActualWidth - UI_WorkImage.ActualWidth) < 0.5 &&
                Math.Abs(UI_WorkBorder.ActualHeight - UI_WorkImage.ActualHeight) < 0.5;

            if (settled)
                return UI_WorkImage.TranslatePoint(new Point(0, 0), UI_WorkCanvas);

            if (!_rereadingImageOrigin && !_imageOriginRereadQueued)
            {
                _imageOriginRereadQueued = true;

                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        _imageOriginRereadQueued = false;
                        _rereadingImageOrigin = true;

                        try
                        {
                            SyncOutlineImageTransform();
                        }
                        finally
                        {
                            _rereadingImageOrigin = false;
                        }
                    }));
            }

            Point inset = UI_WorkCanvas.TranslatePoint(new Point(0, 0), UI_WorkBorder);
            return new Point(-inset.X, -inset.Y);
        }

        /// <summary>
        /// Aligns outline-mode coordinates with the displayed image after layout completes.
        /// </summary>
        private void SyncOutlineImageTransform()
        {
            if (WorkingImage == null) return;

            double displayW = UI_WorkImage.ActualWidth;
            double displayH = UI_WorkImage.ActualHeight;
            if (displayW <= 0 || displayH <= 0) return;

            OutlineMode.ScaleX = displayW / WorkingImage.PixelWidth;
            OutlineMode.ScaleY = displayH / WorkingImage.PixelHeight;

            var imagePos = ImageOriginOnCanvas();
            OutlineMode.OffsetX = imagePos.X;
            OutlineMode.OffsetY = imagePos.Y;

            // Outlines are not the only geometry kept in image pixels: every mode
            // records the points behind its operations, so every mode is told how the
            // canvas it draws on maps onto the photograph.
            var imageTransform = new ViewTransform(
                displayW / WorkingImage.PixelWidth,
                displayH / WorkingImage.PixelHeight,
                imagePos.X,
                imagePos.Y);

            if (AllWorkModes != null)
            {
                foreach (var mode in AllWorkModes)
                {
                    if (mode != null) mode.ImageTransform = imageTransform;
                }
            }

            // The labels are placed from the mapping above, so they follow it whenever
            // the picture is laid out at a different size.
            RepositionLabels();

            if (ScaleCalibration.UpdateViewScale(displayW / WorkingImage.PixelWidth))
                RefreshAllScalePlaceholders();
        }

        // =====================
        // Image transforms
        // =====================

        private void Menu_FlipHorizontal(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new ScaleTransform(-1, 1));

        private void Menu_FlipVertical(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new ScaleTransform(1, -1));

        private void Menu_RotateRight(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new RotateTransform(90));

        private void Menu_RotateLeft(object sender, RoutedEventArgs e)
            => ApplyImageTransform(new RotateTransform(270));   // 270° clockwise equals 90° counter-clockwise.

        private void Menu_ResetTransform(object sender, RoutedEventArgs e)
        {
            if (_originalImageSource == null)
            {
                MessageBox.Show(
                    "There is no original image available to restore.",
                    "Reset Transform",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            SetWorkspaceImage(_originalImageSource, SpecimenManager.CurrentSpecimen.Name,false);

            InitialiseWorkSpaceZoomForLoadedImage();

            SyncOutlineImageTransform();
        }

        /// <summary>
        /// Applies a geometric transform to the active image and reloads the workspace state.
        /// </summary>
        private void ApplyImageTransform(Transform transform)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Read before the picture is replaced: the labels are carried through the
            // same turn, and that needs the size they were placed against.
            double turnedFromWidth = WorkingImage.PixelWidth;
            double turnedFromHeight = WorkingImage.PixelHeight;

            var transformed = new TransformedBitmap(WorkingImage, transform);

            // Re-encode the transformed bitmap so it can be cached and reused like a normal image source.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(transformed));
            using var stream = new System.IO.MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            var bmi = new BitmapImage();
            bmi.BeginInit();
            bmi.CacheOption = BitmapCacheOption.OnLoad;
            bmi.StreamSource = stream;
            bmi.EndInit();
            bmi.Freeze();

            WorkingImage = bmi;
            UI_WorkImage.Source = WorkingImage;

            // The turn belongs to the specimen rather than to this visit, so it is what
            // the specimen is from now on and what a later visit loads. The turned
            // pixels carry no corrections: those are stored separately and rendered
            // onto whatever the specimen holds, which is what keeps a return trip from
            // applying them twice.
            SpecimenManager.ReplaceImage(SpecimenManager.CurrentSpecimen, bmi);

            // A flip or rotation changes the image geometry, so existing overlays and scale calibration
            // must be rebuilt against the new image.
            ResetWorkSpaceZoom();
            ImageAlignment.Clear();
            ClearWorkspace();

            // The drawn operations go with the turn; the labels are carried through it,
            // since a label left at its old coordinates would sit on screen naming the
            // wrong feature rather than simply disappearing.
            MoveLabelsThrough(transform, turnedFromWidth, turnedFromHeight);

            RefreshAllScalePlaceholders();

            OutlineMode.SourceImage = WorkingImage;

            // WorkingImage carries the geometry only, so the adjuster is re-pointed at
            // the turned pixels and the corrections in force are rendered onto them
            // again. Without this the workspace falls back to the uncorrected image,
            // and the adjuster keeps handing out the untransformed one the next time a
            // slider moves.
            _imageAdjuster.CacheImage(WorkingImage);
            ReapplyImageAdjustments();

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(SyncOutlineImageTransform));
        }

        /// Renders the cached image with the correction values currently in force.
        /// Values of zero leave the image as it is, which is already what they mean.
        private void ReapplyImageAdjustments()
        {
            if (!_imageAdjuster.HasImage) return;
            if (!PictureCorrections.IsSet) return;

            _imageAdjuster.ApplyNow(
                PictureCorrections.Contrast / 100.0,
                PictureCorrections.Brightness / 100.0,
                PictureCorrections.Saturation / 100.0);
        }

        // =====================
        // Screenshot export
        // =====================

        private void Menu_Screenshot(object sender, RoutedEventArgs e)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var rtb = RenderWorkspaceToBitmap(2.0); // Supersample for a sharper export.
            if (rtb == null) return;

            var dlg = new SaveFileDialog
            {
                Title = "Save Screenshot",
                FileName = SanitizeFileName(SpecimenManager.DisplayName),
                Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|TIFF image (*.tif)|*.tif",
                DefaultExt = ".png",
                AddExtension = true
            };
            if (dlg.ShowDialog() != true) return;

            // Use the extension the user actually chose so typed filenames are respected.
            BitmapEncoder encoder = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
                ".tif" or ".tiff" => new TiffBitmapEncoder(),
                _ => new PngBitmapEncoder()
            };
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            try
            {
                using var stream = System.IO.File.Create(dlg.FileName);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save the screenshot:\n{ex.Message}",
                    "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Renders the workspace, including visible overlays, to a bitmap.
        /// </summary>
        private RenderTargetBitmap RenderWorkspaceToBitmap(double scale)
        {
            double w = UI_WorkSpace.ActualWidth, h = UI_WorkSpace.ActualHeight;
            if (w <= 0 || h <= 0) return null;

            var cursorVis = UI_DotCursor.Visibility;
            var ringVis = _brushRing != null ? _brushRing.Visibility : Visibility.Collapsed;

            // The bar belongs in the picture; the grip for resizing it does not.
            var gripVis = UI_ScaleBarGrip.Visibility;

            try
            {
                UI_DotCursor.Visibility = Visibility.Collapsed;
                if (_brushRing != null) _brushRing.Visibility = Visibility.Collapsed;
                UI_ScaleBarGrip.Visibility = Visibility.Collapsed;
                SyncLabelChrome(hideAll: true);
                UI_WorkSpace.UpdateLayout();

                var rtb = new RenderTargetBitmap(
                    (int)(w * scale), (int)(h * scale),
                    96 * scale, 96 * scale,
                    PixelFormats.Pbgra32);
                rtb.Render(UI_WorkSpace);
                return rtb;
            }
            finally
            {
                UI_DotCursor.Visibility = cursorVis;   // Restore the cursor even if rendering fails.
                if (_brushRing != null) _brushRing.Visibility = ringVis;
                UI_ScaleBarGrip.Visibility = gripVis;
                SyncLabelChrome(hideAll: false);
                UI_WorkSpace.UpdateLayout();
            }
        }

        /// <summary>
        /// Removes invalid filename characters and returns a safe default when needed.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "screenshot";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // =====================
        // Picture adjustment
        // =====================

        // The open adjustment dialog, whose sliders belong to whichever specimen was
        // loaded when it opened.
        private PictureAdjustmentWindow _adjustWindow;

        /// Closes the adjustment dialog. Its sliders speak for one specimen, so a
        /// dialog left open over a specimen switch would write the departing
        /// specimen's numbers into the arriving one at the next nudge of a slider.
        private void CloseAdjustmentWindow()
        {
            var open = _adjustWindow;
            _adjustWindow = null;
            open?.Close();
        }

        private void Menu_PictureAdjustment(object sender, RoutedEventArgs e)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // One dialog at a time: a second opened over the first would leave two sets
            // of sliders claiming to hold the specimen's values.
            if (_adjustWindow != null)
            {
                _adjustWindow.Activate();
                return;
            }

            PictureAdjustmentWindow adjustWindow = new PictureAdjustmentWindow(
                PictureCorrections.Contrast,
                PictureCorrections.Brightness,
                PictureCorrections.Saturation);

            adjustWindow.FontSize = _currentFontSize;
            adjustWindow.FontFamily = _currentFont;

            adjustWindow.OnAdjustmentChanged = (contrast, brightness, saturation) =>
            {
                // Written through to the loaded specimen, so the dialog opens on these
                // values again whenever that specimen is the one on screen.
                PictureCorrections.Set(contrast, brightness, saturation);

                // Values are stored as percentages in the dialog and converted to normalized adjustments here.
                _imageAdjuster.RequestAdjustment(
                    contrast / 100.0,
                    brightness / 100.0,
                    saturation / 100.0);
            };

            // Closing by any route — the title bar, or a specimen switch — leaves the
            // menu able to open a fresh one.
            adjustWindow.Closed += (s, args) =>
            {
                if (ReferenceEquals(_adjustWindow, adjustWindow))
                    _adjustWindow = null;
            };

            _adjustWindow = adjustWindow;
            adjustWindow.Show();
        }

        // =====================
        // Downsampling
        // =====================

        private void Menu_DownSample(object sender, RoutedEventArgs e)
        {
            if (WorkingImage == null)
            {
                MessageBox.Show("Please open an image first.", "No Image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            long originalPixelCount = (long)WorkingImage.PixelWidth * WorkingImage.PixelHeight;

            DownSampleWindow sampleWindow = new DownSampleWindow(originalPixelCount);
            sampleWindow.FontSize = _currentFontSize;
            sampleWindow.FontFamily = _currentFont;

            sampleWindow.OnPixelsChanged = targetPixels =>
            {
                var result = _imageAdjuster.DownSample(targetPixels);
                UI_WorkImage.Source = result ?? WorkingImage;
            };

            sampleWindow.Show();
        }

        // =====================
        // Global actions
        // =====================

        /// Whether clearing a specimen still stops to ask. Measuring a large sample means
        /// clearing a specimen often, and a dialog in that path costs more than it saves
        /// once the user knows what the command does — so they are allowed to switch it
        /// off. Kept between sessions only while File ▸ Save Settings is on, and brought
        /// back by File ▸ Restore Default Settings.
        private bool _askBeforeClearSpecimen = true;

        /// The one question asked before a specimen's measurements are discarded, wherever
        /// the command was invoked from. Returns false when the user changed their mind.
        ///
        /// The count and the name are in the text because the blast radius is the thing
        /// worth checking: the command takes every kind of measurement off the specimen,
        /// not only the ones the user happens to be looking at.
        private bool ConfirmClearSpecimen(int count)
        {
            if (!_askBeforeClearSpecimen) return true;

            bool dontAskAgain;
            bool confirmed = ConfirmPromptWindow.Show(
                this,
                "Clear Specimen Measurements",
                "Clear all " + count + " recorded operation(s) from "
                    + SpecimenManager.NameOf(SpecimenManager.CurrentSpecimen) + "?"
                    + "\n\nThis removes every measurement and every label for this specimen, not "
                    + "only the ones on screen, and cannot be undone. The image, name and scale "
                    + "are kept.",
                out dontAskAgain,
                confirmText: "Clear");

            // Only once they have actually said yes. Remembering it from a cancelled
            // prompt would let someone switch the guard off while declining to use it.
            if (confirmed && dontAskAgain) _askBeforeClearSpecimen = false;

            return confirmed;
        }

        /// Takes every measurement and every label off the loaded specimen. The Edit menu
        /// item, the sidebar button, Ctrl+Shift+C and the Batch Workshop editor all arrive
        /// at the same question and the same work, so none of them can drift from the
        /// others about what is asked, what is discarded, or what is refreshed afterwards.
        private void Menu_ClearSpecimen(object sender, RoutedEventArgs e)
        {
            if (UndoRedoManager == null) return;

            // No test for a loaded picture, so that this agrees with the same command in
            // the operation history window: a project whose image could not be found still
            // holds that specimen's measurements, and they are still the ones this clears.
            //
            // Nothing recorded, so nothing to ask about and nothing to discard — but the
            // picture can still be carrying a capture that was begun and not finished,
            // and this is what takes it off. Escape ends such a capture without putting
            // the tool back to its first click, so a start over has to come from here.
            if (UndoRedoManager.History.Count + UndoRedoManager.RedoStack.Count == 0)
            {
                ClearWorkspace();
                return;
            }

            // Everything else is the shared clear, which the operation history window
            // calls too.
            ClearLoadedSpecimen();
        }

        /// The loaded specimen's clear, for the operation history window to call instead
        /// of doing its own. It reads which specimen is loaded now rather than which one
        /// that window was opened over, and it does the whole job — the measurements, the
        /// drawings, a half-finished capture, the outline preview and the plot — so the
        /// two routes cannot drift apart again. Returns whether anything was cleared.
        internal bool ClearLoadedSpecimen()
        {
            if (UndoRedoManager == null) return false;

            // No test for a loaded picture. A project whose image could not be found still
            // holds that specimen's measurements, and they are still the ones this clears;
            // ClearWorkspace does nothing harmful with an empty workspace.
            int count = UndoRedoManager.History.Count + UndoRedoManager.RedoStack.Count;
            if (count == 0) return false;

            if (!ConfirmClearSpecimen(count)) return false;

            ClearAllOperations();
            RefreshPlotTab();
            return true;
        }

        /// Empties every piece of session state: the specimens, their measurements,
        /// their images, and everything derived from them.
        private void ResetSession()
        {
            // Ticks first: they name specimens that are about to be discarded, and
            // the Sample list redraws as soon as the roster changes.
            _sampleChecked.Clear();

            // Measurements before the specimens that owned them, so the modes are
            // still told to blank the panels showing those numbers.
            UndoRedoManager.ResetSession();
            SpecimenManager.ResetSession();

            // Session-scoped table state: group columns belong to specimens that are
            // gone, formula columns to measurements that no longer exist, hidden
            // columns to tables that are now empty, and the staged workbook sheets to
            // a history that no longer exists.
            SpecimenGroups.Clear();
            WorkshopFormulas.Clear();
            CustomTableSelection.Clear();
            WorkshopColumnFilter.RestoreAll();
            GeomOpHistoryWindow.ClearStagedSheets();

            // Committed silhouettes are held by specimen name, so leaving them would
            // attach one session's outlines to the next session's specimens.
            CommittedOutlineStore.Clear();

            // The workspace, its calibration, and the mesh kept in memory for a
            // reposition. ClearWorkspaceImage covers the rest of the 3D state.
            ClearWorkspaceImage();
            _activeModelPath = null;

            // Picture corrections belong to the specimens that have just gone, and
            // ClearWorkspaceImage has already unbound them, so the live values are
            // dropped rather than written anywhere.
            PictureCorrections.Clear();

            ClearPcaAnalysis();

            RebuildSampleList();
            UpdateAttemptCounter();
            UpdateClearSpecimenEnabled();
            RefreshPlotTab();
        }

        private void RefreshAllScalePlaceholders()
        {
            // Everything the bar turns on arrives here: a specimen loaded, a
            // calibration measured or lost, a window resized, an image turned. Whether
            // the bar can be shown at all moves with the scale, so that is settled here
            // too rather than only redrawn.
            UpdateScaleBarVisibility();

            // Whether a specimen's scale was measured or passed on is read off the
            // roster, so the roster is redrawn from here too: this is the one place
            // every change of calibration arrives at, and the mark would otherwise
            // stay as it was until something unrelated rebuilt the list.
            if (_sampleTabSelected) RebuildSampleList();

            if (AllWorkModes == null) return;
            foreach (var mode in AllWorkModes)
                mode.RefreshScalePlaceholders();
        }

        /// <summary>
        /// Removes all overlay elements except the cursor.
        /// </summary>
        private void RemovePendingElements()
        {
            foreach (UIElement element in CurrentWorkMode.ElementsToRemove)
                UI_WorkCanvas.Children.Remove(element);

            CurrentWorkMode.ClearElementsToRemove();
        }

        /// <summary>
        /// Clears workspace drawings while keeping the cursor overlay in place.
        /// </summary>
        private void ClearWorkspaceVisualsOnly()
        {
            for (int i = UI_WorkCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (UI_WorkCanvas.Children[i] != UI_DotCursor)
                    UI_WorkCanvas.Children.RemoveAt(i);
            }
        }

        // =====================
        // Scale calibration
        // =====================

        /// Arms scale-calibration capture: the next two workspace clicks define the
        /// calibration line. Invoked from Tools ▸ Set Scale (Menu_SetScale).
        internal void BeginScaleCapture()
        {
            if (WorkingImage == null) return;

            // Restarting always discards any half-finished capture.
            CancelScaleCapture();
            CancelAlignCapture();

            _scaleClicks = 0;
            _scaleMode = true;   // The next two workspace clicks define the calibration line.

            ShowScaleCue(new Vector2(Mouse.GetPosition(UI_WorkCanvas)));
        }

        /// <summary>
        /// Cancels an in-progress scale capture and removes its visuals.
        /// </summary>
        internal void CancelScaleCapture()
        {
            _scaleMode = false;
            _scaleClicks = 0;

            if (_scaleLine != null)
            {
                UI_WorkCanvas.Children.Remove(_scaleLine);
                _scaleLine = null;
            }

            EndScaleCue();
        }

        // =====================
        // Outline brush cue
        // =====================

        // The outline edit brushes shown at the size they will touch. One ring
        // serves all three: only ever one of them is armed.
        private Ellipse _brushRing;

        /// <summary>Softest fill that still reads as a disc over a photograph.</summary>
        private static readonly Brush BrushRingFill =
            new SolidColorBrush(Color.FromArgb(56, 255, 235, 59));

        private static readonly Brush BrushRingEdge =
            new SolidColorBrush(Color.FromArgb(210, 245, 190, 0));

        /// Shows where an outline edit brush would reach: a see-through yellow disc
        /// of the brush's own size, centred on the cursor. The radius is in canvas
        /// pixels, which is the space the brush works in, so the disc keeps covering
        /// what it will touch however far the image is zoomed.
        private void ShowBrushRing(Vector2 centre, double radiusCanvas)
        {
            if (radiusCanvas <= 0)
            {
                HideBrushRing();
                return;
            }

            if (_brushRing == null)
            {
                _brushRing = new Ellipse
                {
                    Fill = BrushRingFill,
                    Stroke = BrushRingEdge,
                    StrokeThickness = 1,

                    // A cue, not a target: clicks belong to the workspace under it.
                    IsHitTestVisible = false
                };

                // Above the outline it is about to edit, whatever else is on the canvas.
                Panel.SetZIndex(_brushRing, 1000);
            }

            // Clearing the workspace takes it off the canvas; it goes back on the
            // next time the cursor moves.
            if (_brushRing.Parent == null) AddElementToWorkSpace(_brushRing);

            double diameter = radiusCanvas * 2;
            _brushRing.Width = diameter;
            _brushRing.Height = diameter;
            _brushRing.SetPosition(centre.X - radiusCanvas, centre.Y - radiusCanvas);
            _brushRing.Visibility = Visibility.Visible;
        }

        /// <summary>Takes the brush cue off screen, without discarding it.</summary>
        internal void HideBrushRing()
        {
            if (_brushRing != null) _brushRing.Visibility = Visibility.Collapsed;
        }

        /// Places the cue dot at the given canvas position and switches to a
        /// crosshair cursor, signalling that scale capture has started.
        private void ShowScaleCue(Vector2 pos)
        {
            if (_scaleCueDot == null)
            {
                _scaleCueDot = new Ellipse
                {
                    Fill = Brushes.Yellow,   // Matches the dashed calibration line.
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    Width = 11,
                    Height = 11
                };
                AddElementToWorkSpace(_scaleCueDot);
            }

            MoveScaleCue(pos);
            UI_WorkSpace.Cursor = Cursors.Cross;
        }

        /// <summary>
        /// Keeps the cue dot centred on the cursor while scale capture is active.
        /// </summary>
        internal void MoveScaleCue(Vector2 pos)
        {
            _scaleCueDot?.SetPosition(pos.X - 5.5, pos.Y - 5.5);
        }

        /// <summary>
        /// Removes the cue dot and restores the normal workspace cursor.
        /// </summary>
        private void EndScaleCue()
        {
            if (_scaleCueDot != null)
            {
                UI_WorkCanvas.Children.Remove(_scaleCueDot);
                _scaleCueDot = null;
            }

            UI_WorkSpace.Cursor = null;
        }

        private void HandleScaleClick(Vector2 mousePos)
        {
            if (_scaleClicks == 0)
            {
                _scaleLine = new Line
                {
                    Stroke = Brushes.Yellow,
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    X1 = mousePos.X,
                    Y1 = mousePos.Y,
                    X2 = mousePos.X,
                    Y2 = mousePos.Y
                };
                AddElementToWorkSpace(_scaleLine);
                _scaleClicks = 1;
                return;
            }

            _scaleLine.X2 = mousePos.X;
            _scaleLine.Y2 = mousePos.Y;

            double dx = _scaleLine.X2 - _scaleLine.X1;
            double dy = _scaleLine.Y2 - _scaleLine.Y1;
            FinishScaleCapture(Math.Sqrt(dx * dx + dy * dy));
        }

        private void FinishScaleCapture(double pixelLength)
        {
            _scaleMode = false;
            _scaleClicks = 0;

            // Capture is over: remove the cue dot and restore the cursor before
            // the dialog appears.
            EndScaleCue();

            if (pixelLength < 1e-3)
            {
                if (_scaleLine != null)
                {
                    UI_WorkCanvas.Children.Remove(_scaleLine);
                    _scaleLine = null;
                }
                return;
            }

            var dlg = new ScaleWindow
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            if (dlg.ShowDialog() == true)
            {
                ScaleCalibration.SetFromLine(pixelLength, dlg.LengthValue, dlg.SelectedUnit);
                RefreshAllScalePlaceholders();

                // Every length and area in every table is converted with this specimen's
                // scale, so an open history window is showing pixels until it is told. Here
                // and not in RefreshAllScalePlaceholders, which also runs when the window
                // is merely resized — that changes no measurement.
                RefreshHistoryWindow();
            }

            if (_scaleLine != null)
            {
                UI_WorkCanvas.Children.Remove(_scaleLine);
                _scaleLine = null;
            }
        }
    }
}