using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;

namespace DinoLino
{
    /// Menu handlers, tip rotation, undo/redo bindings, and shared view toggles for the
    /// main window.
    public partial class MainWindow
    {
        // ---- Dialogs ----

        private void Menu_About(object sender, RoutedEventArgs e)
        {
            // Set here rather than left to PopupChrome: a window shown with
            // ShowDialog is modal before its Loaded handler runs, and WPF will not
            // take an owner after that.
            var about = new AboutWindow
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };
            about.ShowDialog();
        }

        private void Menu_UserGuide(object sender, RoutedEventArgs e)
        {
            var userguide = new UserGuideWindow
            {
                Owner = this,
                FontFamily = _currentFont,
                FontSize = _currentFontSize
            };
            userguide.ShowDialog();
        }

        /// Help ▸ View Log. Not modal, so the log can stay open beside the work it is
        /// being read against.
        private void Menu_ViewLog(object sender, RoutedEventArgs e)
        {
            var log = new LogWindow
            {
                Owner = this,
                FontFamily = _currentFont,
                FontSize = _currentFontSize
            };
            log.Show();
        }

        private async void Menu_CheckForUpdates(
    object sender,
    RoutedEventArgs e)
        {
            UpdateCheckResult result = await UpdateChecker.CheckAsync();

            if (!result.WasSuccessful)
            {
                MessageBox.Show(
                    result.ErrorMessage ?? "The update check could not be completed.",
                    "Check for Updates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            if (!result.UpdateAvailable)
            {
                MessageBox.Show(
                    $"You are using the latest version of DinoLino.\n\n" +
                    $"Current version: {result.CurrentVersion}",
                    "Check for Updates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            MessageBoxResult choice = MessageBox.Show(
                $"A new version of DinoLino is available.\n\n" +
                $"Installed version: {result.CurrentVersion}\n" +
                $"Latest version: {result.LatestVersion}\n\n" +
                $"Would you like to open the download page?",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (choice == MessageBoxResult.Yes &&
                !string.IsNullOrWhiteSpace(result.ReleasePageUrl))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = result.ReleasePageUrl,
                    UseShellExecute = true
                });
            }
        }

        // ---- Undo / Redo ----

        /// True while the current mode has an operation begun and not finished. Undo and
        /// redo are held off until it is completed.
        private bool OperationUnderWay =>
            CurrentWorkMode != null && CurrentWorkMode.HasUnfinishedOperation;

        private void Menu_Undo(object sender, RoutedEventArgs e)
        {
            if (OperationUnderWay) return;

            var result = UndoRedoManager.Undo();
            if (result == null) return;

            // Remove the visual elements that were added by the undone operation.
            foreach (var el in result.Elements)
                UI_WorkCanvas.Children.Remove(el);
        }

        private void Menu_Redo(object sender, RoutedEventArgs e)
        {
            if (OperationUnderWay) return;

            var result = UndoRedoManager.Redo();
            if (result == null) return;

            // Re-add the visuals associated with the redone operation.
            foreach (var el in result.Elements)
                AddElementToWorkSpace(el);
        }

        /// Sets whether Undo and Redo can be chosen: there must be something to undo or
        /// redo, and no operation part way through.
        private void BindUndoRedoMenuItems()
        {
            bool free = !OperationUnderWay;

            UI_MenuUndo.IsEnabled = free && UndoRedoManager.CanUndo;
            UI_MenuRedo.IsEnabled = free && UndoRedoManager.CanRedo;
        }

        // The items are only seen with the Edit menu open, so they are brought up to
        // date as it opens rather than on every click in the workspace.
        private void MenuEdit_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            BindUndoRedoMenuItems();
        }

        // ---- History ----

        // The one operation history window. It is not modal and it edits, so a second
        // copy would show stale tables beside the live one and the two would disagree
        // about which tables are staged for the workbook.
        private GeomOpHistoryWindow _historyWindow;

        private void Menu_SeeHistory(object sender, RoutedEventArgs e)
        {
            if (_historyWindow != null)
            {
                _historyWindow.Activate();
                return;
            }

            // The window edits as well as reads now, so it is given the clear the rest of
            // the program uses, a way to tell the workspace what it took, and the name of
            // the loaded specimen as something to read rather than a value to keep.
            _historyWindow = new GeomOpHistoryWindow(
                UndoRedoManager, () => SpecimenManager.DisplayName, TableScales(),
                ClearLoadedSpecimen, OnHistoryChanged)
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            _historyWindow.Closed += (s, args) => _historyWindow = null;
            _historyWindow.Show();
        }

        // Waits out a burst of refresh requests before redrawing once.
        private DispatcherTimer _historyRefreshTimer;

        /// Redraws the operation history window, if it is open, for the changes that never
        /// reach the undo history and so cannot announce themselves to it: a scale
        /// measured, a group assigned, a specimen renamed, an outline's metrics generated,
        /// a project opened or reset.
        ///
        /// Several requests arrive for one change — a measurement raises two properties, a
        /// rename one per keystroke — and a table of a hundred columns is not cheap to
        /// draw, so the requests are collapsed and the window is redrawn once the burst is
        /// over. Nothing reads the window in between, so the wait costs nothing.
        internal void RefreshHistoryWindow()
        {
            if (_historyWindow == null) return;

            if (_historyRefreshTimer == null)
            {
                _historyRefreshTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };

                _historyRefreshTimer.Tick += (s, e) =>
                {
                    _historyRefreshTimer.Stop();
                    _historyWindow?.RefreshTables();
                };
            }

            // Restarting rather than starting: each new request pushes the redraw back, so
            // a name being typed redraws once at the end instead of once per letter.
            _historyRefreshTimer.Stop();
            _historyRefreshTimer.Start();
        }

        private void Menu_ExportHistory(object sender, RoutedEventArgs e)
        {
            GeomOpHistoryWindow.ExportAllOperationHistory(
                UndoRedoManager,
                SpecimenManager.DisplayName,
                TableScales());
        }

        // ---- Image cache ----

        /// Removes every cached specimen image in one step, after confirming with the
        /// user.
        private void Menu_ClearImageCache(object sender, RoutedEventArgs e)
        {
            int cachedCount = SpecimenManager.CachedImageCount;

            if (cachedCount == 0)
            {
                MessageBox.Show(
                    this,
                    "There are no cached images to clear.",
                    "Clear Image Cache",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                this,
                $"Remove all {cachedCount} cached image(s)?\n\n" +
                "The specimen \u25b2/\u25bc arrows will no longer cycle through those images, " +
                "and they cannot be brought back without re-opening their files.\n\n" +
                "Specimen names and all measurements are kept, so the History window and " +
                "exported tables are unaffected.",
                "Clear Image Cache",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            SpecimenManager.ClearAllImages();
        }

        /// <summary>Opens the cache roster so images can be removed one at a time.</summary>
        private void Menu_EditImageCache(object sender, RoutedEventArgs e)
        {
            var window = new EditImageCacheWindow(SpecimenManager)
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            window.ShowDialog();
        }

        // ---- Scale calibration ----

        /// Arms the scale-calibration capture (Tools ▸ Set Scale). The capture
        /// itself lives with the workspace code, which owns BeginScaleCapture.
        private void Menu_SetScale(object sender, RoutedEventArgs e)
        {
            BeginScaleCapture();
        }


        // ---- Passing one specimen's scale to others ----

        /// A standing answer to the overwrite question, for a user who has asked to stop
        /// being asked. Ask is the state in which the question is still put.
        private ScaleOverwriteAnswer _scaleOverwrite = ScaleOverwriteAnswer.Ask;

        /// Both items need a scale to pass on, and the second needs somewhere to pass it
        /// to. The ticks in Directory ▸ Sample change without announcing it, so what is
        /// on offer is worked out as the submenu opens rather than kept in step with them.
        private void MenuScale_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            bool haveScale = ScaleCalibration.IsCalibrated;

            UI_MenuApplyScaleAll.IsEnabled = haveScale && ScaleTargetsAll().Count > 0;
            UI_MenuApplyScaleSelected.IsEnabled = haveScale && ScaleTargetsTicked().Count > 0;

            UI_MenuApplyScaleSelected.ToolTip = !haveScale
                ? "Set a scale on this specimen first."
                : ScaleTargetsTicked().Count > 0
                    ? "Give this specimen's scale to the ones ticked in Directory ▸ Sample."
                    : "Tick the specimens in Directory ▸ Sample first.";

            UI_MenuApplyScaleAll.ToolTip = haveScale
                ? "Give every other specimen this specimen's scale. Anything already measured on its own image is asked about first."
                : "Set a scale on this specimen first.";
        }

        private void Menu_ApplyScaleAll(object sender, RoutedEventArgs e)
            => ApplyScaleToSpecimens(ScaleTargetsAll());

        private void Menu_ApplyScaleSelected(object sender, RoutedEventArgs e)
            => ApplyScaleToSpecimens(ScaleTargetsTicked());

        /// Every specimen that could take a scale: one that stands for a picture, has not
        /// been deleted, and is not the one the scale is coming from. Copying onto the
        /// source would mark its own measured scale as borrowed.
        private List<Specimen> ScaleTargetsAll() =>
            SpecimenManager.Specimens
                .Where(s => s != null
                         && s.FileName != null
                         && !s.Deleted
                         && !ReferenceEquals(s, SpecimenManager.CurrentSpecimen))
                .ToList();

        /// <summary>The same, narrowed to what is ticked in Directory ▸ Sample.</summary>
        private List<Specimen> ScaleTargetsTicked() =>
            ScaleTargetsAll().Where(_sampleChecked.Contains).ToList();

        /// Gives the loaded specimen's scale to the specimens named, marked as inherited
        /// so it stays clear which specimens were actually measured.
        private void ApplyScaleToSpecimens(List<Specimen> targets)
        {
            var source = ScaleCalibration.State;
            if (!source.IsSet || targets == null || targets.Count == 0) return;

            // A scale measured on a specimen's own image is a reading somebody took, so it
            // is not written over without being asked about. One already borrowed is.
            var measured = targets
                .Where(t => t.Calibration.IsSet && !t.Calibration.Inherited)
                .ToList();

            var answer = _scaleOverwrite;

            if (measured.Count > 0 && answer == ScaleOverwriteAnswer.Ask)
            {
                bool remember;

                var asked = ScaleApplyWindow.Ask(
                    this, SpecimenManager.NameOf(SpecimenManager.CurrentSpecimen),
                    source, targets.Count, measured.Count, _currentFont, _currentFontSize,
                    out remember);

                // Cancelled: not one specimen is touched.
                if (asked == null) return;

                answer = asked.Value;
                if (remember) _scaleOverwrite = answer;
            }

            var taking = answer == ScaleOverwriteAnswer.Overwrite
                ? targets
                : targets.Where(t => !measured.Contains(t)).ToList();

            var borrowed = source.AsInherited();
            int changed = 0;

            foreach (var target in taking)
            {
                if (target.Calibration == borrowed) continue;

                target.Calibration = borrowed;
                changed++;
            }

            if (changed > 0) ProjectSession.MarkChanged();

            // The roster is where an inherited scale is read off, so it is redrawn if the
            // user is looking at it. It is rebuilt on its own when that tab is opened.
            if (_sampleTabSelected) RebuildSampleList();

            ReportScaleApplied(changed, taking.Count, targets.Count - taking.Count);
        }

        private void ReportScaleApplied(int changed, int applied, int kept)
        {
            string what = changed == 0
                ? applied == 0
                    ? "No specimen took the scale."
                    : "Every specimen chosen already had this scale."
                : changed == 1
                    ? "1 specimen took this specimen's scale."
                    : changed + " specimens took this specimen's scale.";

            if (kept > 0)
            {
                what += kept == 1
                    ? "\n\n1 specimen kept the scale measured on its own image."
                    : "\n\n" + kept + " specimens kept the scale measured on their own images.";
            }

            MessageBox.Show(this, what, "Apply Scale",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---- Alignment ----

        /// Arms the axis-drawing capture (Tools ▸ Align ▸ Align Specimen, and the control
        /// panel's Align button). The capture itself lives in the alignment file,
        /// which owns BeginAlignCapture.
        private void Menu_AlignImage(object sender, RoutedEventArgs e)
        {
            BeginAlignCapture();
        }

        // ---- Image tool availability ----

        /// Enables or disables the tools that need a loaded image, across every
        /// entry point each one has. Called wherever the working image arrives or
        /// is cleared, so no tool is offered when there is nothing to measure and
        /// the menu items cannot drift apart from the buttons.
        internal void SetImageToolsEnabled(bool enabled)
        {
            UI_MenuAlign.IsEnabled = enabled;
            UI_AlignButton.IsEnabled = enabled;

            // The compass goes off with the picture and comes back with the next one.
            RefreshImageAxes();

            UI_MenuScale.IsEnabled = enabled;
            UI_ScaleButton.IsEnabled = enabled;

            UI_MenuScreenshot.IsEnabled = enabled;
            UI_MenuPictureCorrections.IsEnabled = enabled;
            UI_MenuDecimate.IsEnabled = enabled;
            UI_MenuTransform.IsEnabled = enabled;
        }

        /// Enables Clear Specimen Measurements only while the loaded specimen has something
        /// to clear. Undone operations count: they are still on the specimen's
        /// record and the button discards them along with the rest.
        internal void UpdateClearSpecimenEnabled()
        {
            bool anything = UndoRedoManager.CanUndo || UndoRedoManager.CanRedo;

            // One rule for both ways in, so the menu item and the button are never
            // offering different answers to the same question.
            UI_ClearSpecimenButton.IsEnabled = anything;
            UI_MenuClearSpecimen.IsEnabled = anything;
        }

        /// Refreshes every control whose availability depends on what the session
        /// has recorded. Call this wherever measurements are added or removed.
        internal void UpdateDataDependentControls()
        {
            UpdateClearSpecimenEnabled();
            UpdateWorkshopButtonsEnabled();

            // Everything that changes what the tables hold arrives here, including the
            // outline metrics, which are stamped on after the operation was recorded and so
            // never reach the history window on their own.
            RefreshHistoryWindow();
        }

        // ---- Tips ----

        private bool _tipsVisible = true;
        private DispatcherTimer _tipCycleTimer;
        private int _tipIndex = 0;

        private void Menu_SeeTips(object sender, RoutedEventArgs e)
        {
            _tipsVisible = UI_SeeTips.IsChecked;
            UI_TipsBar.Visibility = _tipsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TipCycle_Tick(object sender, EventArgs e)
        {
            var tips = CurrentWorkMode?.GetTips();
            if (tips == null || tips.Length <= 1)
            {
                _tipCycleTimer.Stop();
                return;
            }

            _tipIndex = (_tipIndex + 1) % tips.Length;
            FadeTip(tips[_tipIndex]);
        }

        /// Displays the first tip for the active mode and starts cycling if more tips
        /// exist.
        public void UpdateTip()
        {
            if (!_tipsVisible) return;

            _tipCycleTimer.Stop();
            _tipIndex = 0;

            var tips = CurrentWorkMode?.GetTips();
            if (tips == null || tips.Length == 0 || string.IsNullOrEmpty(tips[0]))
            {
                UI_TipText.Text = string.Empty;
                return;
            }

            ShowTip(tips[0]);

            if (tips.Length > 1)
                _tipCycleTimer.Start();
        }

        private void ShowTip(string text)
        {
            UI_TipText.Text = text;
            UI_TipText.Opacity = 1;
        }

        private void FadeTip(string newText)
        {
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(800));
            fadeOut.Completed += (s, e) =>
            {
                UI_TipText.Text = newText;

                // Fade the new tip back in after the old one disappears.
                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(800));
                UI_TipText.BeginAnimation(TextBlock.OpacityProperty, fadeIn);
            };

            UI_TipText.BeginAnimation(TextBlock.OpacityProperty, fadeOut);
        }

        // ---- Workspace display ----

        private void Menu_SeePrevOps(object sender, RoutedEventArgs e)
        {
            bool isChecked = UI_SeePrevOps.IsChecked;

            // Apply the setting to every work mode so the workspace behaves consistently.
            foreach (var mode in AllWorkModes)
                mode.SeePreviousOperations = isChecked;

            // Refresh the visible workspace so the change takes effect immediately.
            ClearWorkspaceVisualsOnly();

            if (isChecked)
            {
                foreach (var operation in UndoRedoManager.History)
                    foreach (var el in operation.Elements)
                        AddElementToWorkSpace(el);
            }
        }

        /// Toggles the image-axes compass. The overlay itself lives in the alignment
        /// file, which owns SetImageAxesVisible.
        private void Menu_SeeImageAxes(object sender, RoutedEventArgs e) => RefreshImageAxes();

        /// The compass follows the tick and the picture together. It describes a
        /// specimen's orientation, so it means nothing with no specimen on screen — and
        /// its menu item sits inside Tools ▸ Align, which is greyed out until a picture
        /// is loaded, so a compass left showing over an empty workspace could not
        /// otherwise be dismissed. The tick itself is left alone either way: it is the
        /// user's standing preference, not a property of what happens to be loaded.
        private void RefreshImageAxes() =>
            SetImageAxesVisible(UI_SeeImageAxes.IsChecked && WorkingImage != null);

        /// Toggles the scalebar. The overlay itself lives in the calibration file,
        /// which owns UpdateScaleBarVisibility and decides whether the bar can be shown
        /// at all.
        private void Menu_ViewScaleBar(object sender, RoutedEventArgs e)
        {
            UpdateScaleBarVisibility();
        }

        /// Toggles the mini-map overview panel. The panel itself lives in
        /// MainWindow_Navigation.cs, which owns SetMiniMapVisible.
        private void Menu_SeeMiniMap(object sender, RoutedEventArgs e)
        {
            SetMiniMapVisible(UI_SeeMiniMap.IsChecked);
        }

        /// Toggles the Workshop sidebar. The sidebar itself lives in
        /// MainWindow_Workshop.cs, which owns SetWorkshopVisible.
        private void Menu_SeeWorkshop(object sender, RoutedEventArgs e)
        {
            SetWorkshopVisible(UI_SeeWorkshop.IsChecked);
        }

        /// Toggles the Directory panel. The browser itself lives in
        /// MainWindow_Directory.cs; the sidebar layout lives in MainWindow_Workshop.cs.
        private void Menu_SeeDirectory(object sender, RoutedEventArgs e)
        {
            SetDirectoryVisible(UI_SeeDirectory.IsChecked);
        }

        /// Shows or hides the creature artwork at the bottom of the control panel.
        private void Menu_SeeRex(object sender, RoutedEventArgs e)
        {
            UI_CreatureArtContainer.Visibility =
                UI_SeeRex.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Menu_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                menuItem.IsSubmenuOpen = false;
            }
        }

        /// The menu names one drawing color for the whole workspace, so the brush goes
        /// to every work mode rather than only the tab that happens to be open. A mode
        /// that never heard the choice would otherwise keep drawing in the color it
        /// started with, which is what a tab switched to after picking a color did.
        /// Opens the Line Options window, or brings back the one already open. It is
        /// modeless, so a change can be judged against the image it is drawn on.
        private void Menu_LineOptions(object sender, RoutedEventArgs e)
        {
            if (_lineOptionsWindow != null)
            {
                _lineOptionsWindow.Activate();
                return;
            }

            var window = new LineOptionsWindow(_lineColorTag, _lineThickness)
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            window.OnColorChanged = choice => ApplyLineColor(choice);
            window.OnThicknessChanged = thickness => ApplyLineThickness(thickness);

            window.Closed += (s, args) =>
            {
                if (ReferenceEquals(_lineOptionsWindow, window)) _lineOptionsWindow = null;
            };

            _lineOptionsWindow = window;
            window.Show();
        }

        // ---- Settings ----

        /// Toggles whether the settings choices are kept for later sessions. The reading
        /// and writing lives in MainWindow_Settings.cs, which owns SetSaveSettings.
        private void Menu_SaveSettings(object sender, RoutedEventArgs e)
        {
            SetSaveSettings(UI_MenuSaveSettings.IsChecked);
        }

        /// Returns every View setting to how the program first opens. The defaults
        /// themselves live in MainWindow_Settings.cs, which owns RestoreDefaultSettings.
        private void Menu_RestoreDefaults(object sender, RoutedEventArgs e)
        {
            RestoreDefaultSettings();
        }

        // ---- Font ----

        private void Menu_Font(object sender, RoutedEventArgs e)
        {
            var fontWindow = new FontWindow(_currentFontSize, _currentFont)
            {
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            fontWindow.OnFontSizeChanged = size =>
            {
                ApplyFontSize(size);
                fontWindow.FontSize = size;
            };

            fontWindow.OnFontFamilyChanged = family =>
            {
                ApplyFontFamily(family);
                fontWindow.FontFamily = family;
            };

            // Use a modeless window so font changes can be previewed live in the main UI.
            fontWindow.Show();
        }

        /// Applies a font size to every part of the main window that follows the
        /// Settings ▸ Font setting.
        private void ApplyFontSize(double size)
        {
            _currentFontSize = size;
            TextElement.SetFontSize(UI_ControlPanel, size);
            TextElement.SetFontSize(UI_WorkshopPanel, size);
            UI_TipText.FontSize = size;

            // Set on the counter overlay itself rather than row by row, so rows
            // added to it later (the per-shape tallies, and anything after them)
            // scale without another edit here. The rows carry no local FontSize,
            // which is what lets this inherit down to them.
            TextElement.SetFontSize(UI_AttemptCounter, size);

            // The modes letter what they draw in this size, and the letters already on
            // screen are drawn again to match.
            foreach (var mode in AllWorkModes)
                mode.LabelFontSize = size;

            RestyleShownOperations();
        }

        /// <summary>Applies a font family everywhere the size setting reaches.</summary>
        private void ApplyFontFamily(FontFamily family)
        {
            _currentFont = family;
            TextElement.SetFontFamily(UI_ControlPanel, family);
            TextElement.SetFontFamily(UI_WorkshopPanel, family);
            UI_TipText.FontFamily = family;
            TextElement.SetFontFamily(UI_AttemptCounter, family);

            foreach (var mode in AllWorkModes)
                mode.LabelFont = family;

            RestyleShownOperations();
        }
    }
}