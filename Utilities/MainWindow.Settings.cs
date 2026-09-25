using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DinoLino
{
    /// <summary>
    /// What File ▸ Save Settings and File ▸ Restore Default Settings do: reading the
    /// stored View settings, applying them, and writing them back.
    /// </summary>
    public partial class MainWindow
    {
        // What View > Line Options last chose. Kept here rather than read back off a
        // menu, so the window can open on the values in force.
        private string _lineColorTag = UserSettings.DefaultLineColor;
        private double _lineThickness = WorkMode.DefaultLineThickness;

        private LineOptionsWindow _lineOptionsWindow;

        // =====================
        // Startup
        // =====================

        /// Applies whatever was kept from the user's last session. Called at the end of
        /// the constructor, before the first frame is drawn, so the defaults never
        /// flash past on their way to what the user chose.
        private void ApplyUserSettings()
        {
            var settings = UserSettings.Load();

            UI_MenuSaveSettings.IsChecked = settings.SaveSettings;

            // With the switch off, the window keeps the defaults the XAML has already
            // given it, whatever else the file happens to hold.
            if (!settings.SaveSettings) return;

            ApplySettings(settings);
        }

        // =====================
        // Commands
        // =====================

        /// Switching on records what is on screen straight away; switching off forgets
        /// it there and then, rather than waiting for a close that may never come.
        private void SetSaveSettings(bool keep)
        {
            if (keep)
                SaveUserSettings();
            else
                UserSettings.Delete();
        }

        /// Returns the Settings menu to how the program first opens, and forgets anything
        /// stored. The Save Settings switch is left as the user set it: with it on, the
        /// defaults are simply what gets kept from here.
        private void RestoreDefaultSettings()
        {
            // The window shows the values it opened on, which a reset has just moved
            // out from under it.
            if (_lineOptionsWindow != null) _lineOptionsWindow.Close();

            // An unset line color asks for every mode to be left on the color it
            // already has, which is what an ordinary start wants and what a reset does
            // not: a stale color would outlive the tick that named it. So the default
            // is spelled out here.
            ApplySettings(new UserSettings { LineColor = UserSettings.DefaultLineColor });

            UserSettings.Delete();
        }

        // =====================
        // Applying
        // =====================

        private void ApplySettings(UserSettings settings)
        {
            UI_SeeTips.IsChecked = settings.SeeTips;
            Menu_SeeTips(UI_SeeTips, new RoutedEventArgs());

            UI_SeePrevOps.IsChecked = settings.SeePreviousOperations;
            Menu_SeePrevOps(UI_SeePrevOps, new RoutedEventArgs());

            UI_SeeAttempts.IsChecked = settings.SeeOperationCount;
            Menu_SeeAttempts(UI_SeeAttempts, new RoutedEventArgs());

            UI_SeeImageAxes.IsChecked = settings.SeeImageAxes;
            Menu_SeeImageAxes(UI_SeeImageAxes, new RoutedEventArgs());

            UI_SeeMiniMap.IsChecked = settings.SeeNavigationWindow;
            Menu_SeeMiniMap(UI_SeeMiniMap, new RoutedEventArgs());

            UI_SeeWorkshop.IsChecked = settings.SeeBatchWorkshop;
            Menu_SeeWorkshop(UI_SeeWorkshop, new RoutedEventArgs());

            UI_SeeDirectory.IsChecked = settings.SeeDirectory;
            Menu_SeeDirectory(UI_SeeDirectory, new RoutedEventArgs());

            UI_SeeRex.IsChecked = settings.SeeRex;
            Menu_SeeRex(UI_SeeRex, new RoutedEventArgs());

            ApplyLineColor(LinePalette.Find(settings.LineColor));
            ApplyLineThickness(settings.LineThickness);

            ApplyFontSize(settings.FontSize);
            ApplyFontFamily(new FontFamily(settings.FontFamily));
        }

        /// Hands a color to every work mode, so all four tabs agree from the moment it
        /// is chosen. A color the window does not offer is ignored, as is none at all.
        private void ApplyLineColor(LineColorChoice choice)
        {
            if (choice == null) return;

            _lineColorTag = choice.Tag;

            foreach (var mode in AllWorkModes)
                mode.LineColor = choice.Brush;

            RestyleShownOperations();
        }

        /// <summary>Hands a stroke width to every work mode.</summary>
        private void ApplyLineThickness(double thickness)
        {
            _lineThickness = WorkMode.ClipThickness(thickness);

            foreach (var mode in AllWorkModes)
                mode.LineThickness = _lineThickness;

            RestyleShownOperations();
        }

        /// Draws the measurements already on screen again in the new style, so a
        /// choice can be judged against the image rather than only against the next
        /// thing drawn. With previous operations hidden there is nothing on screen to
        /// restyle, and redrawing would clear a measurement half-made.
        private void RestyleShownOperations()
        {
            if (UI_SeePrevOps == null || !UI_SeePrevOps.IsChecked) return;

            // A measurement part way through lives on the canvas and not yet in the
            // history a rebuild draws from, so it would be wiped rather than restyled.
            if (CurrentWorkMode != null && !CurrentWorkMode.IsStartingNewOperation) return;

            RebuildOperationVisuals();
        }

        // =====================
        // Recording
        // =====================

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            // A cancelled close leaves the user still working, so nothing on screen is
            // final yet.
            if (e.Cancel) return;

            if (UI_MenuSaveSettings.IsChecked) SaveUserSettings();
        }

        /// Records what the Settings menu is showing at this moment.
        private void SaveUserSettings()
        {
            new UserSettings
            {
                SaveSettings = UI_MenuSaveSettings.IsChecked,

                SeeTips = UI_SeeTips.IsChecked,
                SeePreviousOperations = UI_SeePrevOps.IsChecked,
                SeeOperationCount = UI_SeeAttempts.IsChecked,
                SeeImageAxes = UI_SeeImageAxes.IsChecked,
                SeeNavigationWindow = UI_SeeMiniMap.IsChecked,
                SeeBatchWorkshop = UI_SeeWorkshop.IsChecked,
                SeeDirectory = UI_SeeDirectory.IsChecked,
                SeeRex = UI_SeeRex.IsChecked,

                LineColor = _lineColorTag,
                LineThickness = _lineThickness,

                FontFamily = _currentFont?.Source,
                FontSize = _currentFontSize
            }
            .Save();
        }

    }
}