using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace KidLauncher.E2E
{
    internal static class TestGame
    {
        public static int Run(string eventPath, bool ignoreClose)
        {
            if (eventPath == null) throw new ArgumentNullException(nameof(eventPath));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string pid = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            using (var window = new Form())
            {
                window.Text = "KidLauncher E2E Test Game - " + (ignoreClose ? "Ignores close" : "Cooperative") + " - PID " + pid;
                window.ClientSize = new Size(640, 400);
                window.StartPosition = FormStartPosition.CenterScreen;
                window.WindowState = FormWindowState.Normal;
                window.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Text = "Native Windows test game\r\n\r\n" +
                        (ignoreClose ? "Ignores close requests" : "Cooperative: accepts close requests") +
                        "\r\n\r\nPID " + pid
                });
                window.Shown += (sender, args) =>
                {
                    File.AppendAllText(eventPath, "DISPLAY " + Desktop.DisplayMode() + Environment.NewLine + "START " + pid + Environment.NewLine);
                    window.Activate();
                };
                window.FormClosing += (sender, args) =>
                {
                    File.AppendAllText(eventPath, "CLOSE_REQUEST " + pid + Environment.NewLine);
                    if (ignoreClose) args.Cancel = true;
                    else File.AppendAllText(eventPath, "CLOSE_ACCEPTED " + pid + Environment.NewLine);
                };
                Application.Run(window);
            }
            return 0;
        }
    }
}
