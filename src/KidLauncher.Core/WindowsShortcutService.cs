using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace KidLauncher.Core
{
    public static class WindowsShortcutService
    {
        public static DesktopShortcut Read(string path)
        {
            object instance = new ShellLink();
            try
            {
                ((IPersistFile)instance).Load(Path.GetFullPath(path), 0);
                var link = (IShellLink)instance;
                var target = new StringBuilder(32768);
                var args = new StringBuilder(32768);
                var directory = new StringBuilder(32768);
                var icon = new StringBuilder(32768);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH; no network resolution/UI.
                link.GetArguments(args, args.Capacity);
                link.GetWorkingDirectory(directory, directory.Capacity);
                int iconIndex;
                link.GetIconLocation(icon, icon.Capacity, out iconIndex);
                uint flags;
                ((IShellLinkDataList)instance).GetFlags(out flags);
                return new DesktopShortcut
                {
                    Title = Path.GetFileNameWithoutExtension(path), ShortcutPath = Path.GetFullPath(path),
                    TargetPath = Environment.ExpandEnvironmentVariables(target.ToString()), Arguments = args.ToString(),
                    WorkingDirectory = Environment.ExpandEnvironmentVariables(directory.ToString()),
                    IconPath = icon.Length == 0 ? target.ToString() : icon + "," + iconIndex,
                    RunAsAdministrator = (flags & 0x2000) != 0
                };
            }
            finally { Marshal.FinalReleaseComObject(instance); }
        }

        public static IReadOnlyList<DesktopShortcut> EnumerateDesktop()
        {
            var result = new List<DesktopShortcut>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            {
                if (!Directory.Exists(folder) || !seen.Add(folder)) continue;
                try
                {
                    foreach (var path in Directory.GetFiles(folder, "*.lnk"))
                        try { result.Add(Read(path)); }
                        catch (Exception ex) when (ex is COMException || ex is IOException || ex is UnauthorizedAccessException)
                        { Trace.TraceWarning("Cannot read desktop shortcut {0}: {1}", path, ex.Message); }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { Trace.TraceWarning("Cannot enumerate desktop {0}: {1}", folder, ex.Message); }
            }
            return result;
        }

        internal static void Save(string path, string executable)
        {
            object instance = new ShellLink();
            try
            {
                var link = (IShellLink)instance;
                link.SetPath(executable);
                link.SetWorkingDirectory(Path.GetDirectoryName(executable));
                link.SetDescription("KidLauncher");
                link.SetIconLocation(executable, 0);
                ((IPersistFile)instance).Save(path, true);
            }
            finally { Marshal.FinalReleaseComObject(instance); }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLink
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr data, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int command);
            void SetShowCmd(int command);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }
        [ComImport, Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkDataList
        {
            void AddDataBlock(IntPtr data);
            void CopyDataBlock(uint signature, out IntPtr data);
            void RemoveDataBlock(uint signature);
            void GetFlags(out uint flags);
            void SetFlags(uint flags);
        }
    }
}
