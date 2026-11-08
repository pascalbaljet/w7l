using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KidLauncher.Core
{
    public sealed class WindowsDisplayService : IDisplayService
    {
        // DEVMODEW's fixed header is 220 bytes on both Win32 and Win64.
        private const int HeaderSize = 220;
        private const int BufferSize = HeaderSize + ushort.MaxValue;
        public DisplayMode Capture() { return Read(PrimaryDevice(), -1); }
        public IReadOnlyList<DisplayMode> Enumerate()
        {
            var result = new List<DisplayMode>();
            var variants = new Dictionary<string, int>();
            var device = PrimaryDevice();
            for (int i = 0; ; i++)
            {
                var mode = Read(device, i);
                if (mode == null) break;
                var label = mode.Width + "/" + mode.Height + "/" + mode.Frequency + "/" + mode.BitsPerPixel;
                variants.TryGetValue(label, out var number);
                variants[label] = mode.VariantNumber = number + 1;
                mode.EnumerationIndex = i;
                result.Add(mode);
            }
            return result;
        }

        public void Apply(DisplayMode mode)
        {
            if (mode == null) throw new ArgumentNullException(nameof(mode));
            var device = PrimaryDevice();
            var data = mode.NativeData;
            if (data == null)
            {
                DisplayMode match = null;
                foreach (var available in Enumerate())
                    if (available.Width == mode.Width && available.Height == mode.Height &&
                        (mode.BitsPerPixel == 0 || available.BitsPerPixel == mode.BitsPerPixel) &&
                        (mode.Frequency == 0 || available.Frequency == mode.Frequency)) { match = available; break; }
                if (match == null) throw new InvalidOperationException("The primary display driver does not support " + mode + ".");
                data = match.NativeData;
            }
            if (data.Length < HeaderSize || BitConverter.ToUInt16(data, 68) != HeaderSize ||
                data.Length != HeaderSize + BitConverter.ToUInt16(data, 70))
                throw new InvalidOperationException("The saved native display mode is invalid.");
            if (BitConverter.ToInt32(data, 172) != mode.Width || BitConverter.ToInt32(data, 176) != mode.Height)
                throw new InvalidOperationException("The saved native display dimensions disagree with the selected resolution. Reselect the mode in the configurator.");
            var memory = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, memory, data.Length);
                Check(ChangeDisplaySettingsEx(device, memory, IntPtr.Zero, 2, IntPtr.Zero), "test");
                // Flags 0: temporary operation, never CDS_UPDATEREGISTRY.
                Check(ChangeDisplaySettingsEx(device, memory, IntPtr.Zero, 0, IntPtr.Zero), "apply");
                var actual = Read(device, -1);
                if (actual.Width != mode.Width || actual.Height != mode.Height ||
                    (mode.BitsPerPixel > 0 && actual.BitsPerPixel != mode.BitsPerPixel) ||
                    (mode.Frequency > 1 && actual.Frequency != mode.Frequency))
                    throw new InvalidOperationException("The driver accepted " + mode + " but Windows reports " + actual + ". The selected mode was not applied.");
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private static void Check(int result, string operation)
        {
            if (result != 0) throw new InvalidOperationException("Display " + operation + " failed (DISP_CHANGE " + result + "). Restore manually through Windows Screen Resolution if necessary.");
        }
        private static DisplayMode Read(string device, int index)
        {
            var buffer = new byte[BufferSize];
            var memory = Marshal.AllocHGlobal(buffer.Length);
            try
            {
                Marshal.Copy(buffer, 0, memory, buffer.Length);
                Marshal.WriteInt16(memory, 68, HeaderSize);
                Marshal.WriteInt16(memory, 70, unchecked((short)ushort.MaxValue));
                if (!EnumDisplaySettingsEx(device, index, memory, 0))
                {
                    if (index == -1) throw new Win32Exception("Cannot capture the primary display mode.");
                    return null;
                }
                int size = (ushort)Marshal.ReadInt16(memory, 68) + (ushort)Marshal.ReadInt16(memory, 70);
                if (size < HeaderSize || size > BufferSize) throw new InvalidOperationException("Invalid driver DEVMODE size.");
                var native = new byte[size];
                Marshal.Copy(memory, native, 0, size);
                return new DisplayMode { Width = Marshal.ReadInt32(memory, 172), Height = Marshal.ReadInt32(memory, 176),
                    BitsPerPixel = Marshal.ReadInt32(memory, 168), Frequency = Marshal.ReadInt32(memory, 184), NativeData = native };
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        private static string PrimaryDevice()
        {
            for (uint i = 0; ; i++)
            {
                var device = new DisplayDevice { Size = Marshal.SizeOf(typeof(DisplayDevice)) };
                if (!EnumDisplayDevices(null, i, ref device, 0)) break;
                if ((device.Flags & 5) == 5) return device.Name;
            }
            throw new InvalidOperationException("Windows did not report an active primary display.");
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public int Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsEx(string device, int index, IntPtr mode, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, IntPtr mode, IntPtr window, uint flags, IntPtr parameter);
    }
}
