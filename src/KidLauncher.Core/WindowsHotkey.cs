using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KidLauncher.Core
{
    // Construct, enable and dispose on the HWND's UI thread, which must pump messages.
    public sealed class WindowsHotkey : IDisposable
    {
        private readonly int key;
        private readonly Action callback;
        private readonly MessageWindow window;
        private readonly HookProc hookCallback;
        private readonly int threadId;
        private readonly int hotkeyId;
        private readonly Timer releaseTimer;
        private IntPtr hook;
        private bool enabled, disposed, pressed, registered;
        public string Strategy { get; private set; }

        public WindowsHotkey(IntPtr windowHandle, int virtualKey, Action callback)
        {
            if (windowHandle == IntPtr.Zero) throw new ArgumentException("A live launcher HWND is required.", nameof(windowHandle));
            if (virtualKey < 1 || virtualKey > 254) throw new ArgumentOutOfRangeException(nameof(virtualKey));
            this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
            key = virtualKey;
            threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            hotkeyId = GlobalAddAtom("KidLauncher.Hotkey." + Guid.NewGuid().ToString("N"));
            if (hotkeyId == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            window = new MessageWindow(this, windowHandle);
            hookCallback = KeyboardHook;
            releaseTimer = new Timer { Interval = 30 };
            releaseTimer.Tick += (sender, args) => { if ((GetAsyncKeyState(key) & 0x8000) == 0) pressed = false; };
            Strategy = "Disabled; RegisterHotKey preferred, low-level hook fallback. Exclusive-input/elevated games require real-machine testing.";
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                CheckThread();
                if (disposed) throw new ObjectDisposedException(nameof(WindowsHotkey));
                if (value == enabled) return;
                if (!value) { Stop(); return; }
                pressed = (GetAsyncKeyState(key) & 0x8000) != 0;
                registered = RegisterHotKey(window.Handle, hotkeyId, 0x4000, (uint)key); // MOD_NOREPEAT, Windows 7+
                if (registered) Strategy = "RegisterHotKey (MOD_NOREPEAT + key-release guard)";
                else
                {
                    int registrationError = Marshal.GetLastWin32Error();
                    hook = SetWindowsHookEx(13, hookCallback, GetModuleHandle(null), 0);
                    if (hook == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot install exit hotkey. RegisterHotKey error " + registrationError + "; keyboard hook also failed.");
                    Strategy = "WH_KEYBOARD_LL fallback (RegisterHotKey error " + registrationError + "). Not guaranteed for exclusive input or higher-integrity games.";
                    Trace.TraceWarning(Strategy);
                }
                enabled = true;
                releaseTimer.Start();
            }
        }

        private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
        {
            try
            {
                if (code >= 0 && enabled && Marshal.ReadInt32(data) == key)
                {
                    int kind = message.ToInt32();
                    if (kind == 0x100 || kind == 0x104) { Fire(); return new IntPtr(1); }
                    if (kind == 0x101 || kind == 0x105) { pressed = false; return new IntPtr(1); }
                }
            }
            catch (Exception ex) { Trace.TraceError("Exit keyboard hook failed: " + ex); }
            return CallNextHookEx(hook, code, message, data);
        }
        private void Fire()
        {
            if (!enabled || pressed) return;
            pressed = true;
            // Keep native callbacks short. Run user code on a later UI message.
            PostMessage(window.Handle, 0x8000 + 731, IntPtr.Zero, IntPtr.Zero);
        }
        private void InvokeCallback()
        {
            if (!enabled || disposed) return;
            try { callback(); }
            catch (Exception ex) { Trace.TraceError("Exit hotkey callback failed: " + ex); }
        }
        private void Stop()
        {
            enabled = false;
            releaseTimer.Stop();
            if (registered && !UnregisterHotKey(window.Handle, hotkeyId)) Trace.TraceWarning("UnregisterHotKey failed: " + Marshal.GetLastWin32Error());
            registered = false;
            if (hook != IntPtr.Zero && !UnhookWindowsHookEx(hook)) Trace.TraceWarning("UnhookWindowsHookEx failed: " + Marshal.GetLastWin32Error());
            hook = IntPtr.Zero;
            pressed = false;
        }
        private void CheckThread()
        {
            if (threadId != System.Threading.Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException("Manage the exit hotkey on its HWND's UI thread.");
        }
        public void Dispose()
        {
            CheckThread();
            if (disposed) return;
            Stop();
            disposed = true;
            releaseTimer.Dispose();
            window.ReleaseHandle();
            GlobalDeleteAtom((ushort)hotkeyId);
            GC.KeepAlive(hookCallback);
        }
        private sealed class MessageWindow : NativeWindow
        {
            private readonly WindowsHotkey owner;
            public MessageWindow(WindowsHotkey owner, IntPtr handle) { this.owner = owner; AssignHandle(handle); }
            protected override void WndProc(ref Message message)
            {
                try
                {
                    if (message.Msg == 0x312 && message.WParam.ToInt32() == owner.hotkeyId) { owner.Fire(); return; }
                    if (message.Msg == 0x8000 + 731) { owner.InvokeCallback(); return; }
                }
                catch (Exception ex) { Trace.TraceError("Exit hotkey message failed: " + ex); }
                base.WndProc(ref message);
            }
        }
        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort GlobalAddAtom(string name);
        [DllImport("kernel32.dll")] private static extern ushort GlobalDeleteAtom(ushort atom);
    }
}
