using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DinoLino.Utilities
{
    /// <summary>
    /// A plain-text record of each session, kept for troubleshooting: when the program
    /// started and stopped, which commands were used, and any error that went unhandled.
    /// </summary>
    /// <remarks>
    /// Every line is written to disk as it is logged, not held back for the end of the
    /// session, so a crash leaves the record complete up to the moment it happened. The
    /// logs of earlier sessions are kept beside the current one, which is what lets a
    /// crash be read about after the program has been started again.
    /// </remarks>
    public static class AppLog
    {
        /// <summary>This session and the four before it.</summary>
        public const int SessionsKept = 5;

        // A session that logs without end, an error raised on every frame for instance,
        // stops being recorded here so it cannot fill the disk.
        private const long MaxBytesPerSession = 5 * 1024 * 1024;

        private const string EndedMarker = "Session ended normally.";

        private static readonly object Gate = new object();
        private static bool _started;
        private static bool _full;
        private static long _written;

        /// <summary>The folder the logs are kept in, beside the saved settings.</summary>
        public static string FolderPath => Path.Combine(
            Path.GetDirectoryName(UserSettings.FilePath) ?? ".", "Logs");

        /// The log of a session: 0 is this one, 1 the one before it, and so on.
        public static string PathOfSession(int age) => Path.Combine(
            FolderPath, age <= 0 ? "session.log" : $"session.{age}.log");

        /// Begins this session's log, moving each earlier one back a place first. Safe
        /// to call more than once; only the first call does anything.
        public static void Start()
        {
            lock (Gate)
            {
                if (_started) return;
                _started = true;

                bool previousEndedNormally = true;

                try
                {
                    Directory.CreateDirectory(FolderPath);

                    string last = PathOfSession(0);

                    if (File.Exists(last))
                        previousEndedNormally = File.ReadAllText(last).Contains(EndedMarker);

                    for (int age = SessionsKept - 1; age >= 1; age--)
                    {
                        string from = PathOfSession(age - 1);
                        string to = PathOfSession(age);

                        if (!File.Exists(from)) continue;
                        if (File.Exists(to)) File.Delete(to);

                        File.Move(from, to);
                    }
                }
                catch
                {
                    // A log that cannot be set up must never stop the program starting.
                }

                var version = Assembly.GetExecutingAssembly().GetName().Version;

                WriteLine($"Session started. DinoLino {version}");
                WriteLine($"Windows: {Environment.OSVersion.VersionString}"
                          + (Environment.Is64BitProcess ? ", 64-bit process" : ", 32-bit process"));
                WriteLine($".NET runtime: {Environment.Version}");

                if (!previousEndedNormally)
                    WriteLine("The previous session did not close normally. Its log is kept as the previous session.");
            }

            try
            {
                Trace.Listeners.Add(new LogTraceListener());
            }
            catch
            {
            }
        }

        /// <summary>Adds one line to this session's log.</summary>
        public static void Write(string message)
        {
            lock (Gate)
            {
                WriteLine(message);
            }
        }

        /// <summary>Records an error with everything needed to find where it came from.</summary>
        public static void WriteException(string context, Exception exception)
        {
            var text = new StringBuilder();
            text.Append("ERROR: ").Append(context);

            if (exception != null)
            {
                text.AppendLine();
                text.Append(exception);
            }

            Write(text.ToString());
        }

        /// <summary>Marks the session as having closed the ordinary way.</summary>
        public static void End() => Write(EndedMarker);

        /// The text of a session's log, or null when there is none. Read while the file
        /// may still be open for writing, so this session's own log can be shown.
        public static string Read(int age)
        {
            try
            {
                string path = PathOfSession(age);
                if (!File.Exists(path)) return null;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                return "This log could not be read: " + ex.Message;
            }
        }

        /// Notes each menu command, button press and tab change made in a window, so a
        /// log shows what was being done in the moments before something went wrong.
        public static void WatchCommands(Window window)
        {
            if (window == null) return;

            window.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (e.OriginalSource is MenuItem item) Write("Menu: " + MenuPath(item));
            }), true);

            window.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (!(e.OriginalSource is ButtonBase button)) return;

                string label = button.Content as string;
                if (string.IsNullOrWhiteSpace(label)) label = button.Name;
                if (string.IsNullOrWhiteSpace(label)) return;

                Write($"{button.GetType().Name}: {label}");
            }), true);

            window.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) =>
            {
                if (e.OriginalSource is TabControl tabs && tabs.SelectedItem is TabItem tab)
                    Write("Tab: " + (tab.Header as string ?? tab.Name));
            }), true);
        }

        // "File > Open Image > 2D Image", read up through the menus the item sits in.
        private static string MenuPath(MenuItem item)
        {
            var parts = new List<string>();

            for (DependencyObject at = item; at != null; at = LogicalTreeHelper.GetParent(at))
            {
                if (at is MenuItem menu)
                    parts.Insert(0, (menu.Header as string ?? menu.Name ?? "?").Replace("_", ""));
            }

            return string.Join(" > ", parts);
        }

        private static void WriteLine(string message)
        {
            if (!_started || _full) return;

            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";

                if (_written + line.Length > MaxBytesPerSession)
                {
                    _full = true;
                    line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  "
                           + $"The log reached its size limit and stopped recording.{Environment.NewLine}";
                }

                File.AppendAllText(PathOfSession(0), line, Encoding.UTF8);
                _written += line.Length;
            }
            catch
            {
                // Logging is a help, never a reason to interrupt the work.
            }
        }

        // Diagnostic output the program already writes is kept as well.
        private sealed class LogTraceListener : TraceListener
        {
            private readonly StringBuilder _partial = new StringBuilder();

            public override void Write(string message) => _partial.Append(message);

            public override void WriteLine(string message)
            {
                _partial.Append(message);
                AppLog.Write(_partial.ToString());
                _partial.Clear();
            }
        }
    }

    /// <summary>Help ▸ View Log: shows this session's log and those of the sessions before it.</summary>
    public class LogWindow : Window
    {
        private readonly ComboBox _session = new ComboBox { MinWidth = 180, Margin = new Thickness(0, 0, 8, 0) };

        private readonly TextBox _text = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(8, 0, 8, 8)
        };

        public LogWindow()
        {
            Title = "DinoLino Log";
            Width = 860;
            Height = 560;
            MinWidth = 480;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            for (int age = 0; age < AppLog.SessionsKept; age++)
            {
                if (age > 0 && !File.Exists(AppLog.PathOfSession(age))) continue;

                _session.Items.Add(new ComboBoxItem
                {
                    Tag = age,
                    Content = age == 0 ? "This session"
                            : age == 1 ? "Previous session"
                            : $"{age} sessions ago"
                });
            }

            _session.SelectedIndex = 0;
            _session.SelectionChanged += (s, e) => ShowSelected();

            var bar = new DockPanel { Margin = new Thickness(8), LastChildFill = false };

            var caption = new TextBlock
            {
                Text = "Session:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            bar.Children.Add(caption);
            bar.Children.Add(_session);
            bar.Children.Add(MakeButton("Refresh", ShowSelected));
            bar.Children.Add(MakeButton("Copy", () => Clipboard.SetText(_text.Text ?? "")));
            bar.Children.Add(MakeButton("Open Log Folder", OpenFolder));

            var close = MakeButton("Close", Close);
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(close);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(_text);

            Content = root;

            ShowSelected();
        }

        private static Button MakeButton(string label, Action onClick)
        {
            var button = new Button
            {
                Content = label,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 8, 0)
            };

            button.Click += (s, e) =>
            {
                try
                {
                    onClick();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "DinoLino Log", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            return button;
        }

        private void ShowSelected()
        {
            int age = _session.SelectedItem is ComboBoxItem item && item.Tag is int tag ? tag : 0;

            _text.Text = AppLog.Read(age) ?? "There is no log for this session.";

            // The end of a log is where a problem shows.
            _text.ScrollToEnd();
        }

        private static void OpenFolder()
        {
            Directory.CreateDirectory(AppLog.FolderPath);
            Process.Start("explorer.exe", "\"" + AppLog.FolderPath + "\"");
        }
    }
}
