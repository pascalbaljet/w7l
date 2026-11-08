using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace KidLauncher.E2E
{
    internal static class Desktop
    {
        private const uint Keyboard = 1;
        private const uint KeyUp = 2;
        private const uint Unicode = 4;

        public static IntPtr Foreground { get { return GetForegroundWindow(); } }

        public static bool Visible(IntPtr hwnd) { return IsWindowVisible(hwnd); }

        public static bool ClientAreaAnimation
        {
            get
            {
                if (!GetSystemParameters(0x1042, 0, out var enabled, 0)) throw NativeError("SPI_GETCLIENTAREAANIMATION");
                return enabled != 0;
            }
            set
            {
                // Update the disposable test account's preference and notify existing WPF windows.
                if (!SetSystemParameters(0x1043, 0, new IntPtr(value ? 1 : 0), 3)) throw NativeError("SPI_SETCLIENTAREAANIMATION");
            }
        }

        public static Size CursorSize()
        {
            var cursor = new CursorInfo { Size = Marshal.SizeOf(typeof(CursorInfo)) };
            if (!GetCursorInfo(ref cursor)) throw NativeError("GetCursorInfo");
            if ((cursor.Flags & 1) == 0) throw new InvalidOperationException("The cursor is not visible. Flags=" + cursor.Flags);
            if (!GetIconInfo(cursor.Cursor, out var info)) throw NativeError("GetIconInfo");
            try
            {
                using (var bitmap = Bitmap.FromHbitmap(info.Color)) return bitmap.Size;
            }
            finally
            {
                if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
                if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
            }
        }

        public static void MovePointer(int x, int y)
        {
            Point start;
            if (!GetCursorPos(out start)) throw NativeError("GetCursorPos");
            Rectangle bounds = SystemInformation.VirtualScreen;
            for (int step = 1; step <= 15; step++)
            {
                int nextX = (int)Math.Round(start.X + ((double)x - start.X) * step / 15);
                int nextY = (int)Math.Round(start.Y + ((double)y - start.Y) * step / 15);
                // Inject actual mouse motion, including its cursor-visibility effects.
                // Address pixel centers in absolute coordinates on the virtual desktop.
                Inject(new Input { Data = new InputUnion { Mouse = new MouseInput {
                    X = Math.Min(65535, Math.Max(0, (int)((nextX - bounds.Left + 0.5) * 65536 / bounds.Width))),
                    Y = Math.Min(65535, Math.Max(0, (int)((nextY - bounds.Top + 0.5) * 65536 / bounds.Height))),
                    Flags = 0xC001
                } } });
                Thread.Sleep(10);
            }
            Point end;
            if (!GetCursorPos(out end)) throw NativeError("GetCursorPos");
            if (end.X != x || end.Y != y)
                throw new InvalidOperationException("The actual cursor did not reach the requested position.");
        }

        public static void Click(int x, int y)
        {
            MovePointer(x, y);
            try { Inject(new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 2 } } }); }
            finally { Inject(new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 4 } } }); }
        }

        public static void Key(ushort key) { Chord(key); }

        public static void Wheel(int delta)
        {
            Inject(new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 0x800, MouseData = unchecked((uint)delta) } } });
        }

        public static void Chord(params ushort[] keys)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            int pressed = 0;
            Exception failure = null;
            try
            {
                foreach (ushort key in keys)
                {
                    Inject(KeyInput(key, 0, 0));
                    pressed++;
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                for (int i = pressed - 1; i >= 0; i--)
                {
                    try { Inject(KeyInput(keys[i], 0, KeyUp)); }
                    catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
                }
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public static void Type(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            foreach (char character in text)
            {
                try { Inject(KeyInput(0, character, Unicode)); }
                finally { Inject(KeyInput(0, character, Unicode | KeyUp)); }
            }
        }

        public static void Activate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) throw new ArgumentException("A window handle is required.", nameof(hwnd));
            SetForegroundWindow(hwnd);
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 2000)
            {
                if (Foreground == hwnd) return;
                Thread.Sleep(25);
            }
            throw new InvalidOperationException("SetForegroundWindow could not foreground window " + hwnd + ".");
        }

        public static string DisplayMode()
        {
            Screen screen = Screen.PrimaryScreen;
            if (screen == null) throw new InvalidOperationException("No primary display is available.");
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw NativeError("GetDC");
            try
            {
                Rectangle bounds = screen.Bounds;
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}x{1} {2}bpp {3}Hz", bounds.Width, bounds.Height, screen.BitsPerPixel, GetDeviceCaps(dc, 116));
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        public static void Capture(string path)
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                    CopyPixelOperation.SourceCopy);
                var cursor = new CursorInfo { Size = Marshal.SizeOf(typeof(CursorInfo)) };
                if (!GetCursorInfo(ref cursor)) throw NativeError("GetCursorInfo");
                if ((cursor.Flags & 1) != 0)
                {
                    IntPtr icon = CopyIcon(cursor.Cursor);
                    if (icon == IntPtr.Zero) throw NativeError("CopyIcon");
                    var info = new IconInfo();
                    try
                    {
                        if (!GetIconInfo(icon, out info)) throw NativeError("GetIconInfo");
                        IntPtr dc = graphics.GetHdc();
                        try
                        {
                            if (!DrawIconEx(dc, cursor.Position.X - bounds.Left - (int)info.HotspotX,
                                cursor.Position.Y - bounds.Top - (int)info.HotspotY, icon, 0, 0, 0, IntPtr.Zero, 3))
                                throw NativeError("DrawIconEx");
                        }
                        finally { graphics.ReleaseHdc(dc); }
                    }
                    finally
                    {
                        if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
                        if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
                        DestroyIcon(icon);
                    }
                }
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static Input KeyInput(ushort key, ushort scan, uint flags)
        {
            return new Input { Type = Keyboard, Data = new InputUnion {
                Keyboard = new KeyboardInput { VirtualKey = key, ScanCode = scan, Flags = flags } } };
        }

        private static void Inject(Input input)
        {
            uint count = SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Input)));
            if (count != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "SendInput injected " + count + " of 1 events (input may be blocked by UIPI).");
        }

        private static Win32Exception NativeError(string operation)
        {
            return new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed.");
        }

        // The mouse member is essential: INPUT is 28 bytes on x86, 40 on x64.
        // Pointer-sized fields supply the native union alignment on both architectures.
        [StructLayout(LayoutKind.Sequential)]
        private struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey, ScanCode;
            public uint Flags, Time;
            public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo { public int Size; public uint Flags; public IntPtr Cursor; public Point Position; }
        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
            public uint HotspotX, HotspotY;
            public IntPtr Mask, Color;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemParameters(uint action, uint parameter, out int value, uint flags);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetSystemParameters(uint action, uint parameter, IntPtr value, uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CopyIcon(IntPtr icon);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    }

    internal sealed class DesktopRecording : IDisposable
    {
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly Thread worker;
        private Exception failure;
        private bool disposed;

        public DesktopRecording(string directory)
        {
            if (directory == null) throw new ArgumentNullException(nameof(directory));
            Directory.CreateDirectory(directory);
            worker = new Thread(() => Record(directory)) { IsBackground = true, Name = "Desktop recording" };
            worker.Start();
        }

        private void Record(string directory)
        {
            try
            {
                var clock = Stopwatch.StartNew();
                int frame = 0;
                // Even an immediately disposed recording must attempt its first frame.
                while (true)
                {
                    if (clock.ElapsedMilliseconds >= 120000)
                        throw new TimeoutException("Desktop recording exceeded its two-minute limit; captured frames were retained.");
                    long next = clock.ElapsedMilliseconds + 200;
                    Desktop.Capture(Path.Combine(directory, "frame-" + frame.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".png"));
                    frame++;
                    // Target 5fps without a catch-up burst if desktop capture is slow.
                    if (stop.WaitOne((int)Math.Max(0, next - clock.ElapsedMilliseconds))) break;
                }
            }
            catch (Exception error) { failure = error; }
        }

        public void Dispose()
        {
            if (!disposed)
            {
                stop.Set();
                worker.Join();
                stop.Dispose();
                disposed = true;
            }
            if (failure != null)
                throw new InvalidOperationException("Desktop recording failed; captured frames were retained.", failure);
        }
    }
}
