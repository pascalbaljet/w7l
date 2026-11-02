using System;
using System.IO;

namespace KidLauncher.Core
{
    public static class WindowsStartupService
    {
        private static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "KidLauncher.lnk"); }
        }
        public static bool IsEnabled() { return File.Exists(ShortcutPath); }
        public static void SetEnabled(bool enabled, string launcherExecutablePath)
        {
            if (!enabled) { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); return; }
            var executable = Path.GetFullPath(launcherExecutablePath);
            if (!File.Exists(executable)) throw new FileNotFoundException("The launcher executable does not exist.", executable);
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath));
            var temporary = ShortcutPath + "." + Guid.NewGuid().ToString("N") + ".lnk";
            try
            {
                WindowsShortcutService.Save(temporary, executable);
                if (File.Exists(ShortcutPath)) File.Replace(temporary, ShortcutPath, null);
                else File.Move(temporary, ShortcutPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
