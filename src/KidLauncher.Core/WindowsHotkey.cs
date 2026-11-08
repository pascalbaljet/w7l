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
        private readonly System.Threading.Thread listenerThread;
        private DetectorWindow listener;
        private Timer releaseTimer;
        private IntPtr hook;
        private volatile bool enabled;
        private bool disposed, pressed, registered;
        private int pending, generation;
        public string Strategy { get; private set; }
        public string LastTrigger { get; private set; }

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
            // Low-level hooks must return promptly or Windows silently removes
            // them. Never share their message pump with WPF, WMI or game startup.
            Exception startupError = null;
            using (var ready = new System.Threading.ManualResetEvent(false))
            {
                listenerThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        try
                        {
                            listener = new DetectorWindow(this);
                            var handle = listener.Handle;
                            releaseTimer = new Timer { Interval = 30 };
                            releaseTimer.Tick += (sender, args) =>
                            {
                                if ((GetAsyncKeyState(key) & 0x8000) != 0) Fire("GetAsyncKeyState polling");
                                else pressed = false;
                            };
                        }
                        catch (Exception error) { startupError = error; return; }
                        finally { ready.Set(); }
                        Application.Run();
                    }
                    finally
                    {
                        Stop();
                        releaseTimer?.Dispose();
                        listener?.Dispose();
                    }
                }) { IsBackground = true, Name = "KidLauncher exit key" };
                listenerThread.SetApartmentState(System.Threading.ApartmentState.STA);
                listenerThread.Start();
                ready.WaitOne();
            }
            if (startupError != null)
            {
                listenerThread.Join();
                window.ReleaseHandle();
                GlobalDeleteAtom((ushort)hotkeyId);
                throw new InvalidOperationException("Cannot start the exit-key listener.", startupError);
            }
            Strategy = "Disabled; dedicated exit-key message thread.";
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                CheckThread();
                if (disposed) throw new ObjectDisposedException(nameof(WindowsHotkey));
                if (value == enabled) return;
                listener.Invoke(new Action(() => SetEnabled(value)));
            }
        }

        private void SetEnabled(bool value)
        {
            if (!value) { Stop(); return; }
            generation++;
            pressed = (GetAsyncKeyState(key) & 0x8000) != 0;
            registered = RegisterHotKey(listener.Handle, hotkeyId, 0x4000, (uint)key); // MOD_NOREPEAT, Windows 7+
            int registrationError = registered ? 0 : Marshal.GetLastWin32Error();
            // A successful RegisterHotKey is not proof that an exclusive-input
            // game will deliver WM_HOTKEY. Keep the hook active in that case too.
            hook = SetWindowsHookEx(13, hookCallback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                int hookError = Marshal.GetLastWin32Error();
                if (registered) UnregisterHotKey(listener.Handle, hotkeyId);
                registered = false;
                throw new Win32Exception(hookError, "Cannot install exit keyboard hook. RegisterHotKey error " + registrationError + ".");
            }
            Strategy = "Dedicated input thread: WH_KEYBOARD_LL + GetAsyncKeyState polling; " + (registered ? "RegisterHotKey enabled" : "RegisterHotKey error " + registrationError) + ". Launcher and game must use the same elevation level.";
            enabled = true;
            releaseTimer.Start();
        }

        private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
        {
            try
            {
                if (code >= 0 && enabled && Marshal.ReadInt32(data) == key)
                {
                    int kind = message.ToInt32();
                    if (kind == 0x100 || kind == 0x104) Fire("WH_KEYBOARD_LL");
                    // Let the polling timer rearm after key-up. WM_HOTKEY for
                    // this press can arrive after key-up and must drain first.
                    // Timer messages have lower priority than queued hotkeys.
                }
            }
            catch (Exception ex) { Trace.TraceError("Exit keyboard hook failed: " + ex); }
            return CallNextHookEx(hook, code, message, data);
        }
        private void Fire(string source)
        {
            if (!enabled || pressed) return;
            pressed = true;
            if (System.Threading.Interlocked.CompareExchange(ref pending, 1, 0) != 0) return;
            LastTrigger = source;
            // Keep native callbacks short. Run user code on a later UI message.
            PostMessage(window.Handle, 0x8000 + 731, new IntPtr(generation), IntPtr.Zero);
        }
        private void InvokeCallback(int activation)
        {
            if (!enabled || disposed || activation != generation) return;
            System.Threading.Interlocked.Exchange(ref pending, 0);
            try { callback(); }
            catch (Exception ex) { Trace.TraceError("Exit hotkey callback failed: " + ex); }
        }
        private void Stop()
        {
            enabled = false;
            generation++;
            releaseTimer?.Stop();
            if (registered && !UnregisterHotKey(listener.Handle, hotkeyId)) Trace.TraceWarning("UnregisterHotKey failed: " + Marshal.GetLastWin32Error());
            registered = false;
            if (hook != IntPtr.Zero && !UnhookWindowsHookEx(hook)) Trace.TraceWarning("UnhookWindowsHookEx failed: " + Marshal.GetLastWin32Error());
            hook = IntPtr.Zero;
            pressed = false;
            System.Threading.Interlocked.Exchange(ref pending, 0);
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
            listener.Invoke(new Action(() => Application.ExitThread()));
            listenerThread.Join();
            disposed = true;
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
                    if (message.Msg == 0x8000 + 731) { owner.InvokeCallback(message.WParam.ToInt32()); return; }
                }
                catch (Exception ex) { Trace.TraceError("Exit hotkey message failed: " + ex); }
                base.WndProc(ref message);
            }
        }
        private sealed class DetectorWindow : Control
        {
            private readonly WindowsHotkey owner;
            public DetectorWindow(WindowsHotkey owner) { this.owner = owner; }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x312 && message.WParam.ToInt32() == owner.hotkeyId)
                {
                    // A nonzero hook handle does not mean Windows still delivers
                    // hook callbacks. WM_HOTKEY is an independent fallback.
                    owner.Fire("WM_HOTKEY");
                    return;
                }
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
