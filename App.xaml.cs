using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace DinoLino
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            DinoLino.Utilities.AppLog.Start();

            // Each of these is only written down. None is marked as handled, so what
            // the program does when an error goes uncaught is exactly what it did
            // before there was a log.
            DispatcherUnhandledException += (s, e) =>
                DinoLino.Utilities.AppLog.WriteException("Unhandled error on the main thread.", e.Exception);

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                DinoLino.Utilities.AppLog.WriteException(
                    e.IsTerminating
                        ? "Unhandled error. The program is closing."
                        : "Unhandled error.",
                    e.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (s, e) =>
                DinoLino.Utilities.AppLog.WriteException("Unhandled error in a background task.", e.Exception);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DinoLino.Utilities.AppLog.End();
            base.OnExit(e);
        }
    }
}
