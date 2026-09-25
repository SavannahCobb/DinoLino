using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DinoLino.Utilities
{
    /// <summary>
    /// The copy of a session kept for the user rather than by them. It is written on
    /// a timer while there is something a project file does not hold, and again when
    /// the program closes, so a crash, a power cut or a hurried exit costs nothing.
    ///
    /// It lives beside the preferences, in the user's application data, and not next
    /// to the project: a session that has never been saved has nowhere else to go,
    /// and one that has must not have a stray file appear next to it.
    ///
    /// It is a project file like any other, written by ProjectFile, so recovering is
    /// the ordinary open. Beside it sits a short note saying which project it came
    /// from and when, which is what the offer to recover is built from.
    /// </summary>
    public static class AutoSave
    {
        private const string FolderName = "DinoLino";
        private const string ProjectFileName = "recovery.dlino";
        private const string NoteFileName = "recovery.txt";

        private const string SourceKey = "Project";
        private const string TimeKey = "Saved";

        /// <summary>Where the recovered session and its note sit.</summary>
        public static string FolderPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            FolderName);

        /// <summary>The session itself, as an ordinary project file.</summary>
        public static string ProjectPath => Path.Combine(FolderPath, ProjectFileName);

        private static string NotePath => Path.Combine(FolderPath, NoteFileName);

        /// The project this session belongs to, or "" for one that was never saved.
        /// Read from the note beside the file, so it survives a crash.
        public static string SourceProject { get; private set; } = "";

        /// <summary>When the kept session was last written.</summary>
        public static DateTime SavedAt { get; private set; }

        /// True when a session was left behind by a start that never got to close
        /// tidily. Reading the note fills in what the offer needs to say.
        public static bool HasRecovery()
        {
            SourceProject = "";
            SavedAt = DateTime.MinValue;

            try
            {
                if (!File.Exists(ProjectPath)) return false;

                // The note only says what the session is; without it the session is
                // still there to be offered, just unnamed and undated.
                if (!File.Exists(NotePath)) return true;

                foreach (string line in File.ReadAllLines(NotePath))
                {
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();

                    if (string.Equals(key, SourceKey, StringComparison.OrdinalIgnoreCase))
                    {
                        SourceProject = value;
                    }
                    else if (string.Equals(key, TimeKey, StringComparison.OrdinalIgnoreCase))
                    {
                        DateTime when;
                        if (DateTime.TryParse(
                                value, CultureInfo.InvariantCulture, DateTimeStyles.None, out when))
                            SavedAt = when;
                    }
                }

                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// Keeps the session, and a note of where it came from. Returns false when it
        /// could not be written, which is the caller's cue to stop trying rather than
        /// to interrupt the user.
        public static bool Write(ProjectData data, string sourceProject)
        {
            if (data == null) return false;

            try
            {
                Directory.CreateDirectory(FolderPath);

                ProjectFile.Save(ProjectPath, data);

                var note = new StringBuilder();
                note.AppendLine("# DinoLino keeps the session here so a crash costs nothing.");
                note.AppendLine("# It is removed as soon as the session is saved or closed tidily.");
                note.AppendLine(SourceKey + "=" + (sourceProject ?? ""));
                note.AppendLine(TimeKey + "=" + DateTime.Now.ToString("s", CultureInfo.InvariantCulture));

                // Written after the session it describes, so a write that fails part
                // way through can never advertise one that was not kept.
                File.WriteAllText(NotePath, note.ToString(), new UTF8Encoding(false));

                return true;
            }
            catch (Exception)
            {
                // A kept copy that cannot be written is a convenience the user has
                // lost, not a failure worth stopping their work for.
                return false;
            }
        }

        /// Removes the kept session, which is what a save or a tidy close does. Being
        /// unable to remove it is not worth reporting: the worst it costs is an offer
        /// to recover something the user already has.
        public static void Discard()
        {
            SourceProject = "";
            SavedAt = DateTime.MinValue;

            Remove(ProjectPath);
            Remove(NotePath);
        }

        private static void Remove(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
