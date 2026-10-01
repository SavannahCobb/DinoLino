using DinoLino.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using DinoLino.Utilities.Operations;
using System.Collections.Generic;

namespace DinoLino
{
    /// <summary>
    /// Workshop sidebar: the Geometric Data workbook and the 2D Outlines images, each
    /// covering every specimen in the session, plus the sidebar's visibility toggle.
    /// One mode's table on its own is handled by its tab in the operation history.
    /// </summary>
    public partial class MainWindow
    {
        // =====================
        // Sidebar visibility
        // =====================

        // Which panels the Settings menu has switched on. These match the initial
        // IsChecked values of UI_SeeWorkshop and UI_SeeDirectory, and the row heights
        // set in the XAML so the first paint needs no layout pass.
        private bool _workshopVisible = true;
        private bool _directoryVisible = true;

        // Whether the sidebar column itself is showing. It is present whenever at
        // least one of the two panels is.
        private bool _sidebarVisible = true;

        // Remembered panel width so hiding and re-showing preserves the user's resize.
        private double _sidebarWidth = 260;

        // Width of the sidebar's GridSplitter column.
        private const double SidebarSplitterWidth = 4;

        /// <summary>Shows or hides the Batch Workshop panel.</summary>
        private void SetWorkshopVisible(bool visible)
        {
            if (visible == _workshopVisible) return;
            _workshopVisible = visible;
            UpdateSidebarLayout();
        }

        /// <summary>Shows or hides the Directory panel.</summary>
        private void SetDirectoryVisible(bool visible)
        {
            if (visible == _directoryVisible) return;
            _directoryVisible = visible;

            // Fill the tree the first time the panel is shown rather than at startup.
            if (visible && UI_DirectoryTree.Items.Count == 0)
                RebuildDirectoryRoots();

            UpdateSidebarLayout();
        }

        /// <summary>Hides the Batch Workshop panel and unchecks its Settings menu item.</summary>
        private void Workshop_Minimize(object sender, RoutedEventArgs e)
        {
            UI_SeeWorkshop.IsChecked = false;
            SetWorkshopVisible(false);
        }

        /// <summary>Hides the Directory panel and unchecks its Settings menu item.</summary>
        private void Directory_Minimize(object sender, RoutedEventArgs e)
        {
            UI_SeeDirectory.IsChecked = false;
            SetDirectoryVisible(false);
        }

        /// Applies the current toggles to the sidebar: which panels are shown, how the
        /// rows divide the column, and whether the column exists at all.
        private void UpdateSidebarLayout()
        {
            UI_WorkshopPanel.Visibility = _workshopVisible ? Visibility.Visible : Visibility.Collapsed;
            UI_DirectoryPanel.Visibility = _directoryVisible ? Visibility.Visible : Visibility.Collapsed;

            // The divider only means anything with a panel on each side of it.
            UI_SidebarDivider.Visibility =
                (_workshopVisible && _directoryVisible) ? Visibility.Visible : Visibility.Collapsed;

            // With the Directory below it the Workshop takes only the height it needs;
            // on its own it fills the column, leaving the blank space underneath.
            UI_WorkshopRow.Height = !_workshopVisible
                ? new GridLength(0)
                : _directoryVisible ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

            UI_DirectoryRow.Height = _directoryVisible
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);

            SetSidebarVisible(_workshopVisible || _directoryVisible);
        }

        /// Adds or removes the sidebar column, resizing the window rather than the
        /// workspace so the image keeps its size.
        private void SetSidebarVisible(bool visible)
        {
            if (visible == _sidebarVisible) return;
            _sidebarVisible = visible;

            if (visible)
            {
                // Grow the window first so the workspace keeps its current width.
                ResizeWindowForSidebar(_sidebarWidth + SidebarSplitterWidth);

                UI_SidebarColumn.MinWidth = 180;
                UI_SidebarColumn.MaxWidth = 500;
                UI_SidebarColumn.Width = new GridLength(_sidebarWidth);
                UI_SidebarSplitterColumn.Width = new GridLength(SidebarSplitterWidth);

                UI_Sidebar.Visibility = Visibility.Visible;
                UI_SidebarSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                // Remember the current width, including any resize the user made.
                if (UI_SidebarColumn.ActualWidth > 0)
                    _sidebarWidth = UI_SidebarColumn.ActualWidth;

                UI_Sidebar.Visibility = Visibility.Collapsed;
                UI_SidebarSplitter.Visibility = Visibility.Collapsed;

                // MinWidth must be cleared before the column can collapse to zero.
                UI_SidebarColumn.MinWidth = 0;
                UI_SidebarColumn.Width = new GridLength(0);
                UI_SidebarSplitterColumn.Width = new GridLength(0);

                ResizeWindowForSidebar(-(_sidebarWidth + SidebarSplitterWidth));
            }
        }

        /// Widens or narrows the window by the sidebar's width so the workspace area
        /// is unaffected. Maximized windows are left alone, since they cannot grow.
        private void ResizeWindowForSidebar(double delta)
        {
            // Nothing to trade before the window is laid out: ActualWidth is still zero
            // and the size the XAML asks for has not been applied.
            if (!IsLoaded) return;
            if (WindowState != WindowState.Normal) return;

            double available = SystemParameters.WorkArea.Width;
            double target = ActualWidth + delta;

            // Never exceed the screen or shrink past the window's own minimum.
            if (target > available) target = available;
            if (target < MinWidth) target = MinWidth;

            Width = target;

            // Pull the window back on-screen if growing pushed its right edge off.
            double right = Left + Width;
            if (right > SystemParameters.WorkArea.Right)
                Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width);
        }

        // =====================
        // Exports
        // =====================

        // Every export covers all specimens: the archived records plus the live
        // history. One mode's table on its own is exported from its own tab in the
        // operation history, which is also where it can be edited; the sidebar writes
        // the whole workbook.

        /// Writes one xlsx holding the tables staged in the operation history, or every
        /// table when none have been staged.
        private void Workshop_ExportGeometric(object sender, RoutedEventArgs e)
        {
            if (UndoRedoManager == null) return;

            GeomOpHistoryWindow.ExportAllGeometricData(
                UndoRedoManager, SpecimenManager.DisplayName, TableScales());
        }

        // =====================
        // Row availability
        // =====================

        /// Enables the Geometric Data row only while the session holds a measurement
        /// some tab of the operation history would table. The test runs the same
        /// acceptance check the tables themselves use, so the row can never offer an
        /// empty workbook.
        internal void UpdateWorkshopButtonsEnabled()
        {
            // Every category the history has a tab for, EFA included: the workbook is
            // built from those tabs, so what fills one of them fills the workbook.
            bool any =
                HasWorkshopData(WorkshopCategory.Curvature)
                || HasWorkshopData(WorkshopCategory.Angle)
                || HasWorkshopData(WorkshopCategory.Shape)
                || HasWorkshopData(WorkshopCategory.OutlineMetadata)
                || HasWorkshopData(WorkshopCategory.Efa);

            UI_EditGeometric.IsEnabled = UI_ExportGeometric.IsEnabled = any;
        }

        /// True when any specimen of the session, archived or live, holds a
        /// measurement one of the category's column groups would table.
        private bool HasWorkshopData(WorkshopCategory category)
        {
            if (UndoRedoManager == null) return false;

            var groups = WorkshopTables.ColumnGroups(category, UndoRedoManager, TableScales());

            foreach (var record in UndoRedoManager.Archive)
            {
                if (AnyAccepted(groups, record.Operations)) return true;
            }

            return AnyAccepted(groups, UndoRedoManager.History);
        }

        private static bool AnyAccepted(
            List<WorkshopColumnGroup> groups, IReadOnlyList<WorkOperation> operations)
        {
            foreach (var op in operations)
            {
                foreach (var group in groups)
                {
                    if (group.Accepts(op)) return true;
                }
            }

            return false;
        }

        /// Enables the 2D Outlines row from the stored silhouettes. Those live in a
        /// folder rather than in session history, so this is refreshed where that
        /// folder can change instead of on every measurement.
        internal void UpdateOutlineGalleryEnabled()
        {
            bool any = OutlineShapeExporter.Survey().TracedCount > 0;

            UI_EditOutlines2D.IsEnabled = any;
            UI_ExportOutlines2D.IsEnabled = any;
        }

        // =====================
        // Editing
        // =====================

        /// Opens the folder of stored silhouettes, where they can be renamed,
        /// duplicated, and deleted. Only what survives there is exported.
        private void Workshop_EditOutlines2D(object sender, RoutedEventArgs e)
        {
            var window = new OutlineGalleryWindow
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            window.ShowDialog();

            UpdateOutlineGalleryEnabled();
        }

        /// Opens the operation history: one tab per mode, where a table is read, edited
        /// and exported, and where each tab can be staged for the workbook that the
        /// Geometric Data export writes.
        private void Workshop_EditGeometric(object sender, RoutedEventArgs e)
            => Menu_SeeHistory(this, new RoutedEventArgs());

        /// Brings everything that reads the tables up to date after a change made in the
        /// operation history window, and takes the drawings of anything it deleted off the
        /// picture. That window is not modal, so this runs on each change rather than when
        /// it closes, and the list is empty for a change that deleted nothing — adding or
        /// hiding a column alters what the plot and the variable pickers offer without
        /// removing a measurement.
        internal void OnHistoryChanged(IReadOnlyList<WorkOperation> removed)
        {
            if (removed != null)
            {
                foreach (var op in removed)
                {
                    if (op.Elements == null) continue;
                    foreach (var element in op.Elements)
                        UI_WorkCanvas.Children.Remove(element);
                }
            }

            UpdateAttemptCounter();
            UpdateDataDependentControls();

            // The plot reads the tables that have just changed, and unlike the counter
            // it does not follow the history on its own.
            RefreshPlotTab();
        }

        private void Workshop_ExportOutlines2D(object sender, RoutedEventArgs e)
        {
            var availability = OutlineShapeExporter.Survey();

            // Nothing to export until the user has stored at least one silhouette.
            if (availability.TracedCount == 0) return;

            var dialog = new OutlineExportWindow(availability)
            {
                Owner = this,
                FontSize = _currentFontSize,
                FontFamily = _currentFont
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                int written = OutlineShapeExporter.ExportAll(dialog.Options);

                MessageBox.Show(
                    this,
                    written == 1
                        ? $"1 outline exported to:\n{dialog.Options.Folder}"
                        : $"{written} outlines exported to:\n{dialog.Options.Folder}",
                    "Export 2D Outlines",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex) 
            {
                MessageBox.Show(
                        this,
                        $"Could not finish the export:\n{ex.Message}",
                        "Export failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
            }
        }
    }
}