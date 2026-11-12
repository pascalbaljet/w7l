using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace KidLauncher.Core
{
    // Swallows both Windows keys so a child cannot open the Start menu or
    // minimize a game. Ctrl+Alt+Del cannot be blocked and keeps working.
    // Like the exit-key hook, it pumps on its own thread so a busy UI thread
    // cannot make Windows silently remove the hook.
    public sealed class WindowsKeyBlocker : IDisposable
    {
        private readonly HookProc callback;
        private readonly Thread thread;
        private readonly bool[] swallowed = new bool[2];
        private Control pump;
        private IntPtr hook;
        private volatile bool enabled;
        private bool disposed;

        public WindowsKeyBlocker()
        {
            callback = KeyboardHook;
            Exception startupError = null;
            using (var ready = new ManualResetEvent(false))
            {
                thread = new Thread(() =>
                {
                    try { pump = new Control(); var handle = pump.Handle; }
                    catch (Exception error) { startupError = error; ready.Set(); return; }
                    ready.Set();
                    try { Application.Run(); }
                    finally { Unhook(); pump.Dispose(); }
                }) { IsBackground = true, Name = "KidLauncher Windows key blocker" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.WaitOne();
            }
            if (startupError != null)
            {
                thread.Join();
                throw new InvalidOperationException("Cannot start the Windows key blocker.", startupError);
            }
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                if (disposed) throw new ObjectDisposedException(nameof(WindowsKeyBlocker));
                if (value != enabled) pump.Invoke(new Action(() => SetEnabled(value)));
            }
        }

        private void SetEnabled(bool value)
        {
            if (!value) { Unhook(); return; }
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0); // WH_KEYBOARD_LL
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot install the Windows key hook.");
            enabled = true;
        }

        private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                int index = Marshal.ReadInt32(data) - 0x5B; // VK_LWIN, VK_RWIN
                if (index == 0 || index == 1)
                {
                    int kind = message.ToInt32();
                    bool down = kind == 0x100 || kind == 0x104;
                    // Swallow a key-up only when its key-down was swallowed. Otherwise
                    // enabling mid-press would leave Windows thinking the key is held.
                    if (down && enabled) { swallowed[index] = true; return new IntPtr(1); }
                    if (!down && swallowed[index]) { swallowed[index] = false; return new IntPtr(1); }
                }
            }
            return CallNextHookEx(hook, code, message, data);
        }

        private void Unhook()
        {
            enabled = false;
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
            swallowed[0] = swallowed[1] = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            pump.Invoke(new Action(Application.ExitThread));
            thread.Join();
            GC.KeepAlive(callback);
        }

        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    }
}
