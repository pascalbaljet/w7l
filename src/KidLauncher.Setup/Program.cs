using System;
using System.Drawing;
using System.Windows.Forms;

namespace KidLauncher.Setup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool quiet = Array.IndexOf(args, "--quiet") >= 0;
            try
            {
                string directory = @"C:\Games\KidLauncher";
                bool directorySpecified = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--quiet") continue;
                    if (args[i] == "--install-dir" && !directorySpecified && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        directory = args[++i];
                        directorySpecified = true;
                    }
                    else throw new ArgumentException("Usage: KidLauncher.Setup.exe [--install-dir <absolute path>] [--quiet]");
                }
                directory = Installer.NormalizeDirectory(directory);
                if (quiet)
                {
                    Installer.Install(directory);
                    Console.WriteLine("KidLauncher installed to " + directory);
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var window = new SetupWindow(directory))
                {
                    Application.Run(window);
                    return window.Succeeded ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                string message = "Installation failed: " + ex.Message;
                if (quiet) Console.Error.WriteLine(message);
                else MessageBox.Show(message, "KidLauncher Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal sealed class SetupWindow : Form
    {
        public bool Succeeded { get; private set; }

        public SetupWindow(string directory)
        {
            Text = "KidLauncher Setup";
            ClientSize = new Size(540, 255);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            var heading = new Label { Text = "Install or update KidLauncher", AutoSize = true, Location = new Point(20, 20) };
            var path = new TextBox { Text = directory, ReadOnly = true, Location = new Point(20, 52), Width = 500 };
            var result = new Label { Text = "Existing games, artwork, disc images and other user files are kept.\r\nClose KidLauncher and its configurator before continuing.", Location = new Point(20, 92), Size = new Size(500, 100) };
            var install = new Button { Text = "Install / Update", Location = new Point(300, 210), Width = 130 };
            var close = new Button { Text = "Close", Location = new Point(440, 210), Width = 80, DialogResult = DialogResult.Cancel };
            install.Click += (sender, args) =>
            {
                install.Enabled = false;
                close.Enabled = false;
                UseWaitCursor = true;
                result.Text = "Installing…";
                result.Refresh();
                try
                {
                    Installer.Install(directory);
                    Succeeded = true;
                    result.Text = "Installation complete. Close Setup, then start KidLauncher normally.\r\nSetup does not start the application as administrator.";
                }
                catch (Exception ex)
                {
                    result.Text = "Installation failed: " + ex.Message;
                    MessageBox.Show(this, result.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    install.Enabled = true;
                }
                finally { UseWaitCursor = false; close.Enabled = true; }
            };
            Controls.AddRange(new Control[] { heading, path, result, install, close });
            AcceptButton = install;
            CancelButton = close;
        }
    }
}
