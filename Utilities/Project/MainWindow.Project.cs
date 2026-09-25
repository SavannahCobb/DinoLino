using DinoLino.Utilities;
using DinoLino.Utilities.Modes;
using DinoLino.Utilities.Operations;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DinoLino
{
    /// <summary>
    /// New, Open, Save and Save As for a whole session. A project holds every
    /// specimen with its picture, calibration and measurements, and the table state
    /// that goes with them, so a piece of work can be put down and picked up again.
    ///
    /// The reading and writing belong to ProjectFile; what is here is the two
    /// directions between that file and the running program — gathering the session
    /// into the shape the file expects, and building the session back out of it.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>How often the session is kept for the user, in minutes.</summary>
        private const double AutoSaveMinutes = 3;

        private DispatcherTimer _autoSaveTimer;

        // True while a kept copy is being written, so a slow one cannot be started
        // again on top of itself.
        private bool _autoSaving;

        // Writes that failed in a row. One failure is usually a folder briefly held
        // open by something else and worth another try; several in a row are not.
        private int _autoSaveFailures;

        /// <summary>Failed writes in a row before the attempts stop.</summary>
        private const int AutoSaveFailureLimit = 3;

        // The offer to recover is made once, on the first frame drawn.
        private bool _recoveryOffered;

        // Set when a kept copy could not be opened. It is the only copy there is, so
        // it stays on disk for the next start rather than being cleared away.
        private bool _keepRecoveryFile;

        // =====================
        // Wiring
        // =====================

        /// Called once from the constructor: keeps the title current and marks the
        /// session as holding something the file does not.
        private void InitializeProjectSession()
        {
            ProjectSession.Changed += UpdateProjectTitle;

            // Every measurement, and every specimen opened, deleted or moved between,
            // changes what a project file would hold.
            UndoRedoManager.PropertyChanged += (s, e) => ProjectSession.MarkChanged();
            SpecimenManager.PropertyChanged += (s, e) => ProjectSession.MarkChanged();

            Closing += Project_Closing;

            // The session left behind by a start that never closed tidily. Offered on
            // the first frame, when the workspace is there to put it back into.
            ContentRendered += Project_ContentRendered;

            _autoSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(AutoSaveMinutes)
            };

            _autoSaveTimer.Tick += (s, e) => KeepSessionSafe();
            _autoSaveTimer.Start();

            // Whatever setting up the window did is not work the user can lose, and
            // this also puts the starting title in place.
            ProjectSession.Reset();
        }

        private void UpdateProjectTitle()
        {
            Title = ProjectSession.Title;
        }

        /// Closing asks nothing. Anything the project file does not hold is kept for
        /// the user instead, and offered back the next time the program opens; a
        /// session with nothing outstanding leaves nothing behind.
        private void Project_Closing(object sender, CancelEventArgs e)
        {
            if (e.Cancel) return;

            _autoSaveTimer?.Stop();

            if (ProjectSession.HasUnsavedChanges && SpecimenManager.HasOpenedImage)
                KeepSessionSafe();
            else if (!_keepRecoveryFile)
                AutoSave.Discard();
        }

        // =====================
        // Menu
        // =====================

        private void Menu_NewProject(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscardingUnsavedWork("Starting a new project")) return;

            ResetSession();
            ProjectSession.Reset();
            AutoSave.Discard();
        }

        private void Menu_OpenProject(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscardingUnsavedWork("Opening another project")) return;

            var dialog = new OpenFileDialog
            {
                Title = "Open Project",
                Filter = ProjectFile.Filter,
                DefaultExt = ProjectFile.Extension,
                CheckFileExists = true,
                InitialDirectory = DialogInitialDirectory
            };

            if (dialog.ShowDialog() != true) return;

            OpenProject(dialog.FileName);
        }

        private void Menu_SaveProject(object sender, RoutedEventArgs e) => SaveProject(saveAs: false);

        private void Menu_SaveProjectAs(object sender, RoutedEventArgs e) => SaveProject(saveAs: true);

        // =====================
        // Saving
        // =====================

        /// Writes the session out, asking for a file when there is not one yet or when
        /// the user asked for a new one. Returns false when nothing was written, which
        /// is what tells a prompt not to go ahead and discard the work.
        private bool SaveProject(bool saveAs)
        {
            string path = ProjectSession.FilePath;

            if (saveAs || string.IsNullOrEmpty(path))
            {
                var dialog = new SaveFileDialog
                {
                    Title = saveAs ? "Save Project As" : "Save Project",
                    Filter = ProjectFile.Filter,
                    DefaultExt = ProjectFile.Extension,
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = SuggestedProjectFileName(),
                    InitialDirectory = DialogInitialDirectory
                };

                if (dialog.ShowDialog() != true) return false;
                path = dialog.FileName;
            }

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                ProjectFile.Save(path, GatherProject());
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;

                MessageBox.Show(this,
                    "This project could not be saved:\n" + ex.Message,
                    "Save Project", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            ProjectSession.MarkSaved(path);

            // The project file now holds everything the kept copy did.
            AutoSave.Discard();

            return true;
        }

        /// A first name for an unsaved project: the loaded specimen's, so a file
        /// saved without thinking about it is still recognisable.
        private string SuggestedProjectFileName()
        {
            if (ProjectSession.HasFile) return System.IO.Path.GetFileName(ProjectSession.FilePath);

            string name = SanitizeFileName(SpecimenManager.DisplayName);
            return string.IsNullOrEmpty(name) ? "project" : name;
        }

        /// <summary>The whole session, in the shape a project file holds it.</summary>
        private ProjectData GatherProject()
        {
            var data = new ProjectData();
            var live = SpecimenManager.CurrentSpecimen;

            // The roster always holds one record, filled by the first image opened.
            // Before that there is no specimen, only the place one will go.
            var roster = SpecimenManager.HasOpenedImage
                ? SpecimenManager.Specimens
                : new List<Specimen>();

            foreach (var specimen in roster)
            {
                if (specimen.Deleted) continue;

                var saved = new ProjectSpecimen
                {
                    Ordinal = specimen.Ordinal,

                    // What the tables call this specimen, rather than the stored name,
                    // so an automatically numbered one keeps the name its rows carry.
                    Name = SpecimenManager.NameOf(specimen),

                    FileName = specimen.FileName,
                    SourcePath = specimen.SourcePath,
                    ImageEdited = specimen.ImageEdited,
                    Image = specimen.Image,
                    ModelPath = specimen.ModelPath ?? specimen.PendingModelPath,
                    ModelOrientation = specimen.ModelOrientation,
                    Calibration = specimen.Calibration,
                    Alignment = specimen.Alignment,
                    Corrections = specimen.Corrections
                };

                saved.Operations.AddRange(OperationsOf(specimen, live));

                foreach (string column in SpecimenGroups.Columns)
                {
                    string value = SpecimenGroups.ValueFor(column, specimen);
                    if (!string.IsNullOrEmpty(value)) saved.Groups[column] = value;
                }

                if (ReferenceEquals(specimen, live)) data.CurrentOrdinal = specimen.Ordinal;

                data.Specimens.Add(saved);
            }

            data.GroupColumns.AddRange(SpecimenGroups.Columns);

            foreach (var column in WorkshopFormulas.All)
            {
                data.Formulas.Add(new ProjectFormula
                {
                    TableKey = column.TableKey,
                    Name = column.Name,
                    Text = column.Text
                });
            }

            foreach (var variable in CustomTableSelection.Columns)
            {
                data.CustomVariables.Add(new ProjectCustomVariable
                {
                    Category = variable.Category.ToString(),
                    Header = variable.Header
                });
            }

            foreach (string key in TableKeys())
            {
                foreach (string column in WorkshopColumnFilter.HiddenColumns(key))
                    data.HiddenColumns.Add(new ProjectHiddenColumn { TableKey = key, Name = column });
            }

            data.WorkbookSheets.AddRange(GeomOpHistoryWindow.StagedSheetNames());

            foreach (var outline in CommittedOutlineStore.Outlines)
            {
                data.Silhouettes.Add(new ProjectSilhouette
                {
                    SpecimenName = outline.SpecimenName,
                    Name = outline.Name,
                    Points = new List<Point>(outline.Points)
                });
            }

            return data;
        }

        /// One specimen's measurements: the live working set for the loaded specimen,
        /// and the stored record for every other.
        private IEnumerable<WorkOperation> OperationsOf(Specimen specimen, Specimen live)
        {
            if (ReferenceEquals(specimen, live)) return UndoRedoManager.History;

            return specimen.Record != null
                ? (IEnumerable<WorkOperation>)specimen.Record.Operations
                : new List<WorkOperation>();
        }

        /// <summary>Every table that can carry formula columns or hidden ones.</summary>
        private static IEnumerable<string> TableKeys()
        {
            foreach (WorkshopCategory category in Enum.GetValues(typeof(WorkshopCategory)))
                yield return WorkshopTables.KeyFor(category);

            yield return CustomTable.Key;
        }

        // =====================
        // Opening
        // =====================

        /// <summary>Reads a project file and makes the session it describes.</summary>
        private void OpenProject(string path)
        {
            ProjectData data;
            List<string> rejected;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                data = ProjectFile.Load(path);
                rejected = RestoreProject(data);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;

                // A session built only halfway is worse than none, so what is left of
                // it goes before the failure is reported.
                ResetSession();
                ProjectSession.Reset();

                MessageBox.Show(this,
                    "This project could not be opened:\n" + ex.Message,
                    "Open Project", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            ProjectSession.MarkSaved(path);
            AutoSave.Discard();

            // What came back is smaller than what the file describes, so saving over
            // it again would lose the rest.
            if (data.MissingPictures.Count > 0 || rejected.Count > 0)
                ProjectSession.MarkChanged();

            ReportOpenProblems(data, rejected, "Open Project");
        }

        /// Builds the session the project describes, on top of an empty one. Returns
        /// the formula columns that no longer fit the tables they belong to.
        private List<string> RestoreProject(ProjectData data)
        {
            var rejected = new List<string>();

            // Restoring is not the user changing anything, so nothing here counts
            // towards the unsaved mark.
            ProjectSession.Suspended = true;

            try
            {
                ResetSession();

                var loaded = RestoreSpecimens(data);
                var target = TargetSpecimen(data, loaded);

                RestoreGroups(data, loaded);
                RestoreHistory(data, loaded, target);

                // Scale, alignment and corrections belong to the shown specimen and
                // are what every table reads its units from, so they are bound whether
                // or not there is a picture for them to sit on.
                if (target != null)
                {
                    ScaleCalibration.BindTo(target);
                    ImageAlignment.BindTo(target);
                    ActiveAlignment.Bind(ImageAlignment);
                    PictureCorrections.BindTo(target);
                }

                if (target != null && target.Image != null)
                {
                    SetWorkspaceImage(target.Image, target.FileName, registerAsNewSpecimen: false);

                    // A picture read back from a project is shown as a picture, whether
                    // or not it began as a view of a model.
                    _workingImageIsModelCapture = false;
                    _activeMesh = null;
                    _activeModelName = null;
                    UI_MenuReposition3D.IsEnabled = false;
                }

                // The chosen variables come first: a formula written for the Custom
                // table can only be read back once its columns are there to name.
                RestoreCustomVariables(data);
                rejected = RestoreFormulas(data);
                RestoreHiddenColumns(data);

                GeomOpHistoryWindow.StageSheets(data.WorkbookSheets);
                RestoreSilhouettes(data);

                RebuildSampleList();
                UpdateAttemptCounter();
                UpdateDataDependentControls();
                RefreshPlotTab();

                // The workspace has to be laid out before the stored geometry can be
                // put back on it, and Background runs after the layout pass that
                // SetWorkspaceImage queues.
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Background, new Action(RebuildOperationVisuals));
            }
            finally
            {
                ProjectSession.Suspended = false;
            }

            return rejected;
        }

        /// Registers every saved specimen, keyed by the ordinal the file used so the
        /// groups and measurements can find them again.
        private Dictionary<int, Specimen> RestoreSpecimens(ProjectData data)
        {
            var loaded = new Dictionary<int, Specimen>();

            foreach (var saved in data.Specimens)
            {
                // A model path is passed as pending only while there is no picture to
                // show, which is what marks a specimen as still needing positioning.
                var specimen = SpecimenManager.ImportSpecimen(
                    saved.Image,
                    saved.FileName,
                    saved.Image == null ? saved.ModelPath : null,
                    saved.SourcePath);

                specimen.Name = saved.Name;
                specimen.ImageEdited = saved.ImageEdited;
                specimen.ModelPath = saved.ModelPath;
                specimen.ModelOrientation = saved.ModelOrientation;
                specimen.Calibration = saved.Calibration;
                specimen.Alignment = saved.Alignment;
                specimen.Corrections = saved.Corrections;

                loaded[saved.Ordinal] = specimen;
            }

            return loaded;
        }

        /// The specimen to show: the one the project was saved on, or the first that
        /// has a picture when that one's photograph could not be found.
        private Specimen TargetSpecimen(ProjectData data, Dictionary<int, Specimen> loaded)
        {
            Specimen target;
            if (loaded.TryGetValue(data.CurrentOrdinal, out target) && target.Image != null)
                return target;

            var withPicture = SpecimenManager.Specimens.FirstOrDefault(s => !s.Deleted && s.Image != null);

            // Something has to be the loaded specimen, or a record would be archived
            // under a specimen that is also the live one and appear twice.
            return withPicture
                   ?? target
                   ?? SpecimenManager.Specimens.FirstOrDefault(s => !s.Deleted);
        }

        private static void RestoreGroups(ProjectData data, Dictionary<int, Specimen> loaded)
        {
            SpecimenGroups.Clear();

            // Made first and in order, so a column every specimen has since left still
            // comes back, in the place it was made.
            foreach (string column in data.GroupColumns)
                SpecimenGroups.EnsureColumn(column);

            foreach (string column in data.GroupColumns)
            {
                foreach (var saved in data.Specimens)
                {
                    string value;
                    if (!saved.Groups.TryGetValue(column, out value)) continue;

                    Specimen specimen;
                    if (!loaded.TryGetValue(saved.Ordinal, out specimen)) continue;

                    SpecimenGroups.Assign(column, value, new[] { specimen });
                }
            }
        }

        /// Puts the measurements back: the shown specimen's as the live working set,
        /// and every other specimen's as its own stored record.
        private void RestoreHistory(ProjectData data, Dictionary<int, Specimen> loaded, Specimen target)
        {
            var archived = new List<SpecimenRecord>();
            var live = new List<WorkOperation>();

            foreach (var saved in data.Specimens)
            {
                Specimen specimen;
                if (!loaded.TryGetValue(saved.Ordinal, out specimen)) continue;

                // The mode that made a measurement is what puts its numbers back on the
                // panel and draws it again.
                foreach (var operation in saved.Operations)
                    operation.SourceMode = ModeFor(operation);

                if (ReferenceEquals(specimen, target))
                {
                    live.AddRange(saved.Operations);
                    continue;
                }

                var record = new SpecimenRecord
                {
                    SpecimenName = SpecimenManager.NameOf(specimen),
                    Ordinal = specimen.Ordinal,
                    Operations = new List<WorkOperation>(saved.Operations)
                };

                // Parked on the specimen as well as archived, which is where switching
                // back to it looks for them.
                specimen.Record = record;
                archived.Add(record);
            }

            if (target != null) SpecimenManager.MakeCurrent(target);

            UndoRedoManager.RestoreSession(archived, live);
        }

        private WorkMode ModeFor(WorkOperation operation)
        {
            if (operation is CircularArcOperation
                || operation is ParabolaOperation
                || operation is SplineOperation) return CurvatureMode;

            if (operation is GetAngleOperation) return GetAngleMode;
            if (operation is ShapeOperation || operation is LineOperation) return DrawMode;
            if (operation is OutlineOperation) return OutlineMode;

            return null;
        }

        private static void RestoreCustomVariables(ProjectData data)
        {
            CustomTableSelection.Clear();

            foreach (var saved in data.CustomVariables)
            {
                if (string.IsNullOrEmpty(saved.Header)) continue;

                WorkshopCategory category;
                if (!Enum.TryParse(saved.Category, out category)) continue;
                if (CustomTableSelection.IsSelected(category, saved.Header)) continue;

                CustomTableSelection.Toggle(category, saved.Header);
            }
        }

        /// Compiles each saved formula against the table it belongs to, in the order
        /// they were made, so one that builds on another still finds it. A formula
        /// naming a column this session's tables no longer hold is reported rather
        /// than restored as something that cannot be calculated.
        private List<string> RestoreFormulas(ProjectData data)
        {
            var rejected = new List<string>();

            WorkshopFormulas.Clear();

            foreach (var saved in data.Formulas)
            {
                if (string.IsNullOrEmpty(saved.Name) || string.IsNullOrEmpty(saved.TableKey)) continue;

                var table = TableFor(saved.TableKey);
                if (table == null || table.MeasurementHeaders == null)
                {
                    rejected.Add(saved.Name);
                    continue;
                }

                Formula formula;
                string error;
                if (!FormulaCompiler.TryCompile(saved.Text, table.MeasurementHeaders, out formula, out error))
                {
                    rejected.Add(saved.Name);
                    continue;
                }

                WorkshopFormulas.Save(null, saved.TableKey, saved.Name, formula);
            }

            return rejected;
        }

        /// One table as it stands right now, which is what a formula for it is
        /// allowed to name.
        private WorkshopTable TableFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            if (string.Equals(key, CustomTable.Key, StringComparison.OrdinalIgnoreCase))
                return CustomTable.Build(UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);

            foreach (WorkshopCategory category in Enum.GetValues(typeof(WorkshopCategory)))
            {
                if (string.Equals(WorkshopTables.KeyFor(category), key, StringComparison.OrdinalIgnoreCase))
                    return WorkshopTables.Build(category, UndoRedoManager, SpecimenManager.DisplayName, ScaleCalibration);
            }

            return null;
        }

        /// Puts back the outlines the user kept as silhouettes. The points are stored
        /// as they were committed, so an outline comes back the shape it was kept.
        private static void RestoreSilhouettes(ProjectData data)
        {
            CommittedOutlineStore.Clear();

            foreach (var outline in data.Silhouettes)
                CommittedOutlineStore.Add(outline.SpecimenName, outline.Name, outline.Points);
        }

        private static void RestoreHiddenColumns(ProjectData data)
        {
            WorkshopColumnFilter.RestoreAll();

            foreach (var column in data.HiddenColumns)
            {
                if (string.IsNullOrEmpty(column.TableKey) || string.IsNullOrEmpty(column.Name)) continue;

                WorkshopColumnFilter.Hide(column.TableKey, column.Name);
            }
        }

        /// Draws the shown specimen's measurements again from the geometry stored with
        /// them, then leaves it to the View setting whether they are on screen. Called
        /// after a project is opened and whenever another specimen is brought in, since
        /// a measurement read back from a file has no drawing until this makes one.
        internal void RebuildOperationVisuals()
        {
            var transform = CurrentWorkMode != null ? CurrentWorkMode.ImageTransform : default(ViewTransform);

            if (!transform.IsValid)
            {
                SyncOutlineImageTransform();
                transform = CurrentWorkMode != null ? CurrentWorkMode.ImageTransform : default(ViewTransform);
            }

            if (!transform.IsValid) return;

            foreach (var operation in UndoRedoManager.History)
            {
                var mode = operation.SourceMode;

                var rebuilt = OperationRedraw.Build(
                    operation,
                    transform,
                    mode != null ? mode.LineColor : null,
                    mode != null ? mode.LineThickness : WorkMode.DefaultLineThickness);

                // Drawn again for the view as it stands now. An operation carrying no
                // geometry keeps whatever it was drawn with when it was made.
                if (rebuilt.Count > 0) operation.Elements = rebuilt;
            }

            Menu_SeePrevOps(UI_SeePrevOps, new RoutedEventArgs());
        }

        // =====================
        // Keeping the session
        // =====================

        /// Writes the session to the kept copy, for a crash or a close that never got
        /// round to saving. Nothing is asked and nothing is reported: it runs behind
        /// whatever the user is doing, and a copy that cannot be written simply stops
        /// being attempted rather than interrupting them.
        private void KeepSessionSafe()
        {
            if (_autoSaving) return;
            if (!ProjectSession.HasUnsavedChanges) return;
            if (!SpecimenManager.HasOpenedImage) return;

            // A session being rebuilt is not yet a session, and a batch reposition
            // leaves the dispatcher free between meshes with half the sample turned.
            if (ProjectSession.Suspended || _batchPosing) return;

            _autoSaving = true;

            try
            {
                if (AutoSave.Write(GatherProject(), ProjectSession.FilePath)) _autoSaveFailures = 0;
                else GiveUpKeepingSession();
            }
            catch (Exception)
            {
                // Keeping a copy is for the user's benefit, so failing at it must
                // never take down the work it was meant to protect.
                GiveUpKeepingSession();
            }
            finally
            {
                _autoSaving = false;
            }
        }

        private void GiveUpKeepingSession()
        {
            _autoSaveFailures++;

            if (_autoSaveFailures >= AutoSaveFailureLimit) _autoSaveTimer?.Stop();
        }

        // =====================
        // Recovering
        // =====================

        private void Project_ContentRendered(object sender, EventArgs e)
        {
            if (_recoveryOffered) return;
            _recoveryOffered = true;

            OfferRecovery();
        }

        /// Offers back the session the last start left behind. Declining removes it,
        /// so the offer is made once and never haunts later sessions.
        private void OfferRecovery()
        {
            if (!AutoSave.HasRecovery()) return;

            string source = AutoSave.SourceProject;
            bool named = !string.IsNullOrEmpty(source);

            var message = new System.Text.StringBuilder();
            message.Append("DinoLino closed with work that had not been saved, and kept it.");

            if (AutoSave.SavedAt > DateTime.MinValue)
                message.Append("\n\nKept at: " + AutoSave.SavedAt.ToString("f"));

            message.Append(named
                ? "\nBelongs to: " + source
                : "\nThis session had not been saved to a project of its own.");

            message.Append("\n\nOpen it again?");

            var answer = MessageBox.Show(this, message.ToString(), "Recover Work",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                AutoSave.Discard();
                return;
            }

            RecoverSession(named ? source : null);
        }

        /// Puts the kept session back. It carries the name of the project it came
        /// from, and is marked as holding more than that project file does, because
        /// that is exactly what it was kept for.
        private void RecoverSession(string sourceProject)
        {
            ProjectData data;
            List<string> rejected;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                data = ProjectFile.Load(AutoSave.ProjectPath);
                rejected = RestoreProject(data);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;

                ResetSession();
                ProjectSession.Reset();

                _keepRecoveryFile = true;

                MessageBox.Show(this,
                    "The kept work could not be opened, and has been left where it is so"
                    + " it can be tried again:\n" + ex.Message,
                    "Recover Work", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (!string.IsNullOrEmpty(sourceProject)) ProjectSession.MarkSaved(sourceProject);
            else ProjectSession.Reset();

            // Recovered work is by definition work the project file does not hold.
            ProjectSession.MarkChanged();

            ReportOpenProblems(data, rejected, "Recover Work");
        }

        // =====================
        // Prompts
        // =====================

        /// Offers a save before something that would throw the session away. False
        /// means the user changed their mind, and the caller should do nothing.
        private bool ConfirmDiscardingUnsavedWork(string action)
        {
            if (!ProjectSession.HasUnsavedChanges) return true;
            if (!SpecimenManager.HasOpenedImage) return true;

            var answer = MessageBox.Show(
                this,
                action + " will discard changes this project does not hold yet.\n\nSave them first?",
                "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No) return true;

            return SaveProject(saveAs: false);
        }

        /// Says what could not be brought back, so a project opened somewhere else
        /// does not quietly come back smaller than it went away.
        private void ReportOpenProblems(ProjectData data, List<string> rejected, string title)
        {
            if (data.MissingPictures.Count == 0 && rejected.Count == 0) return;

            var message = new System.Text.StringBuilder();

            if (data.MissingPictures.Count > 0)
            {
                message.Append(data.MissingPictures.Count == 1
                    ? "1 photograph could not be found, so its specimen has its measurements but no picture:\n"
                    : data.MissingPictures.Count
                      + " photographs could not be found, so their specimens have their measurements but no picture:\n");

                message.Append(string.Join("\n", data.MissingPictures));
                message.Append("\n\nOpening each image again brings the picture back.");
            }

            if (rejected.Count > 0)
            {
                if (message.Length > 0) message.Append("\n\n");

                message.Append(rejected.Count == 1
                    ? "1 formula column could not be calculated from this session's tables and was left out:\n"
                    : rejected.Count
                      + " formula columns could not be calculated from this session's tables and were left out:\n");

                message.Append(string.Join(", ", rejected));
            }

            MessageBox.Show(this, message.ToString(), title,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
