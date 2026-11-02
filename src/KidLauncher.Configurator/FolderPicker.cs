using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KidLauncher.Configurator
{
    // SHBrowseForFolder is available on Windows 7 and does not add a WinForms dependency.
    internal static class FolderPicker
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct BrowseInfo
        {
            public IntPtr Owner, Root, DisplayName;
            [MarshalAs(UnmanagedType.LPWStr)] public string Title;
            public uint Flags;
            public IntPtr Callback, Param;
            public int Image;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHBrowseForFolder(ref BrowseInfo info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHGetPathFromIDList(IntPtr item, System.Text.StringBuilder path);
        public static string Select(Window owner, string initial)
        {
            var info = new BrowseInfo { Owner = new WindowInteropHelper(owner).Handle, Title = "Choose working directory", Flags = 0x41, DisplayName = Marshal.AllocHGlobal(520) };
            IntPtr item = IntPtr.Zero;
            try
            {
                item = SHBrowseForFolder(ref info); if (item == IntPtr.Zero) return null;
                var path = new System.Text.StringBuilder(260); return SHGetPathFromIDList(item, path) ? path.ToString() : null;
            }
            finally { if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item); Marshal.FreeHGlobal(info.DisplayName); }
        }
    }
}
