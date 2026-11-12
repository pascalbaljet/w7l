using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using KidLauncher.Core;

namespace KidLauncher
{
    public partial class App : Application
    {
        private Mutex instance;
        internal ILog Log { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            bool first;
            instance = new Mutex(true, @"Local\KidLauncher.ChildConsole", out first);
            if (!first) { Shutdown(); return; }
            DispatcherUnhandledException += (s, args) =>
            {
                RecordException(args.Exception);
                args.Handled = true;
                var window = MainWindow as MainWindow;
                if (window != null) window.ShowUnexpectedError();
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) => RecordException(args.ExceptionObject as Exception);
            SessionEnding += (s, args) =>
            {
                var window = MainWindow as MainWindow;
                if (window != null && window.OperationActive)
                {
                    args.Cancel = true;
                    window.RequestGameExit();
                }
            };
            try
            {
                bool smoke = e.Args.Contains("--smoke-test");
                string temporaryRoot = smoke ? Path.Combine(Path.GetTempPath(), "KidLauncherVisual-" + Guid.NewGuid().ToString("N")) : null;
                if (smoke) Directory.CreateDirectory(temporaryRoot);
                var store = new ConfigurationStore(temporaryRoot);
                if (smoke)
                {
                    var sample = new LauncherConfiguration();
                    var titles = new[] { "Putt-Putt Saves the Zoo", "Freddi Fish", "Pajama Sam", "Spy Fox", "Fatty Bear", "Let's Explore", "The Backyard", "Jungle Adventure", "Space Adventure", "Ocean Explorer", "Big Thinkers", "Play and Learn", "One More Adventure", "Winnie de Poeh en de Honingboom", "Mickey Stad op Stelten", "Robbie Konijn Peuter", "Max en de Piraten" };
                    foreach (var title in titles) sample.Games.Add(new Game { Title = title, SortOrder = sample.Games.Count });
                    store.Save(sample);
                }
                Log = new FileLog(store.DataDirectory);
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    Log.Write("Launcher build=" + typeof(App).Assembly.ManifestModule.ModuleVersionId + "; OS=" + Environment.OSVersion +
                        "; elevated=" + new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
                var window = new MainWindow(store, Log, smoke);
                MainWindow = window;
                if (smoke) window.ContentRendered += (sender, args) => window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        int index = Array.IndexOf(e.Args, "--screenshots");
                        if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Supply --screenshots directory.");
                        window.CaptureSmokeStates(e.Args[index + 1]);
                        Shutdown(0);
                    }
                    catch (Exception error)
                    {
                        Log.Write(error.ToString());
                        int index = Array.IndexOf(e.Args, "--screenshots");
                        if (index >= 0 && index + 1 < e.Args.Length)
                        {
                            Directory.CreateDirectory(e.Args[index + 1]);
                            File.WriteAllText(Path.Combine(e.Args[index + 1], "launcher.failure.txt"), error.ToString());
                        }
                        Shutdown(1);
                    }
                    finally { Directory.Delete(temporaryRoot, true); }
                }), DispatcherPriority.ApplicationIdle);
                MainWindow.Show();
            }
            catch (Exception ex)
            {
                RecordException(ex);
                MessageBox.Show("The play room could not open. Ask a grown-up to open KidLauncher.Configurator and check Diagnostics.", "KidLauncher");
                Shutdown(1);
            }
        }

        private void RecordException(Exception ex)
        {
            try { if (Log != null) Log.Write("Unhandled launcher error: " + ex); }
            catch { /* A failed log must not cause a second crash. */ }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (instance != null) instance.Dispose();
            base.OnExit(e);
        }
    }
}
