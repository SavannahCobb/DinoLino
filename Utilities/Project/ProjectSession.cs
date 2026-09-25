using System;
using System.IO;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Which project the session belongs to and whether it holds anything that is not
    /// in the file yet. The window title is built from both, so the user can tell at a
    /// glance what they are working in and whether closing it would cost them work.
    ///
    /// Only the two facts live here. What a project contains, and how it is written
    /// and read, belongs to ProjectFile; when the title has to change, the window
    /// listens for Changed rather than this deciding anything about the interface.
    /// </summary>
    public static class ProjectSession
    {
        /// <summary>What an unsaved session is called in the title bar.</summary>
        public const string UntitledName = "Untitled";

        private const string ProgramName = "DinoLino";

        private static string _filePath;
        private static bool _unsaved;

        // Set while a project is being read back. Rebuilding a session touches every
        // piece of state a change would, and none of it is the user changing anything.
        private static bool _suspended;

        /// <summary>Raised whenever the file or the unsaved mark moves.</summary>
        public static event Action Changed;

        /// <summary>The project file this session was opened from or saved to.</summary>
        public static string FilePath => _filePath;

        /// <summary>True once the session has a file of its own.</summary>
        public static bool HasFile => !string.IsNullOrEmpty(_filePath);

        /// <summary>True when something has happened that the file does not hold.</summary>
        public static bool HasUnsavedChanges => _unsaved;

        /// <summary>The project's name, or "Untitled" before it has been saved.</summary>
        public static string Name
        {
            get
            {
                if (!HasFile) return UntitledName;

                try
                {
                    string name = Path.GetFileNameWithoutExtension(_filePath);
                    return string.IsNullOrEmpty(name) ? UntitledName : name;
                }
                catch
                {
                    return UntitledName;
                }
            }
        }

        /// The window title: the program on its own until the session has a project
        /// or something to save, then the project's name and an unsaved mark.
        public static string Title
        {
            get
            {
                if (!HasFile && !_unsaved) return ProgramName;

                return ProgramName + " - " + Name + (_unsaved ? " *" : "");
            }
        }

        /// <summary>True while a project is being opened, when nothing counts as a change.</summary>
        public static bool Suspended
        {
            get { return _suspended; }
            set { _suspended = value; }
        }

        /// Records that the session now holds something the file does not. Cheap
        /// enough to call from anywhere a measurement or a table changes.
        public static void MarkChanged()
        {
            if (_suspended || _unsaved) return;

            _unsaved = true;
            Raise();
        }

        /// <summary>Records a successful save or open of this file.</summary>
        public static void MarkSaved(string path)
        {
            _filePath = path;
            _unsaved = false;
            Raise();
        }

        /// Returns to an unsaved, unnamed session. The session's contents are the
        /// caller's to clear; this is only the name and the mark.
        public static void Reset()
        {
            _filePath = null;
            _unsaved = false;
            Raise();
        }

        private static void Raise()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
