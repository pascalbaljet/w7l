using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace KidLauncher.Configurator
{
    public sealed class App : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            bool smoke = args.Contains("--smoke-test");
            string temporary = null;
            bool acquired;
            using (var instance = new Mutex(true, "Local\\KidLauncher.Configurator" + (smoke ? ".Smoke." + Guid.NewGuid().ToString("N") : ""), out acquired))
            {
                if (!acquired) { MessageBox.Show("The configurator is already open.", "KidLauncher"); return; }
                try
                {
                    var app = new App();
                    app.DispatcherUnhandledException += (s, e) =>
                    {
                        if (smoke) { Console.Error.WriteLine(e.Exception); Environment.ExitCode = 1; app.Shutdown(1); }
                        else MessageBox.Show(e.Exception.Message, "Configurator error", MessageBoxButton.OK, MessageBoxImage.Error);
                        e.Handled = true;
                    };
                    if (smoke) { temporary = Path.Combine(Path.GetTempPath(), "KidLauncher.Configurator.Smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary); }
                    var window = new MainWindow(smoke, temporary);
                    if (smoke)
                    {
                        int index = Array.IndexOf(args, "--screenshot");
                        string path = index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.Combine(Environment.CurrentDirectory, "configurator-smoke.png");
                        bool captured = false;
                        window.ContentRendered += async (s, e) =>
                        {
                            if (captured) return;
                            captured = true;
                            try { await window.CaptureSmokeAsync(path); Environment.ExitCode = 0; app.Shutdown(0); }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine(ex); Environment.ExitCode = 1;
                                try { File.WriteAllText(Path.ChangeExtension(path, ".failure.txt"), ex.ToString()); } catch { }
                                app.Shutdown(1);
                            }
                        };
                    }
                    Environment.ExitCode = app.Run(window);
                }
                catch (Exception ex)
                {
                    Environment.ExitCode = 1;
                    if (smoke) Console.Error.WriteLine(ex);
                    else MessageBox.Show(ex.Message, "Configurator could not start", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    if (temporary != null) try { Directory.Delete(temporary, true); } catch { }
                    instance.ReleaseMutex();
                }
            }
        }
    }
}
