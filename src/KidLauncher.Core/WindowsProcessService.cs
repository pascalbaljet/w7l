using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KidLauncher.Core
{
    public sealed class WindowsProcessService : IProcessService
    {
        public IReadOnlyList<TrackedProcess> Snapshot()
        {
            var result = new List<TrackedProcess>();
            foreach (var process in Process.GetProcesses())
                using (process)
                {
                    try { result.Add(Identity(process)); }
                    catch (Win32Exception) { /* Protected system processes are not candidates. */ }
                    catch (InvalidOperationException) { }
                }
            return result;
        }

        public TrackedProcess Start(Game game, string applicationDirectory)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            var executable = Resolve(applicationDirectory, game.ExecutablePath);
            var directory = string.IsNullOrWhiteSpace(game.WorkingDirectory)
                ? Path.GetDirectoryName(executable) : Resolve(applicationDirectory, game.WorkingDirectory);
            using (var process = Process.Start(new ProcessStartInfo(executable, game.Arguments ?? "")
            {
                WorkingDirectory = directory,
                UseShellExecute = game.RunAsAdministrator,
                Verb = game.RunAsAdministrator ? "runas" : ""
            }))
            {
                if (process == null) throw new UncertainProcessStartException("Windows did not return the launched process identity. Check whether the game opened before recovering.");
                try { return StartedIdentity(process, executable); }
                catch (Exception error) { throw new UncertainProcessStartException("The game started, but its identity could not be recorded. Close it manually before confirming recovery.", error); }
            }
        }

        internal static TrackedProcess StartedIdentity(Process process, string executable)
        {
            // Process.Start retains CreateProcess/ShellExecuteEx's returned handle.
            // GetProcessTimes remains valid after exit while that handle is open.
            // Do not reopen the PID or query ProcessName: a bootstrapper may already
            // have exited before Process.Start returns to the caller.
            long created, exited, kernel, user;
            if (!GetProcessTimesRaw(process.Handle, out created, out exited, out kernel, out user))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read creation time from the returned process handle.");
            return new TrackedProcess { Id = process.Id, StartTimeUtcTicks = DateTime.FromFileTimeUtc(created).Ticks,
                Name = Path.GetFileNameWithoutExtension(executable) };
        }

        public IReadOnlyList<TrackedProcess> FindChildren(IReadOnlyList<TrackedProcess> parents)
        {
            if (parents == null) throw new ArgumentNullException(nameof(parents));
            var rows = new List<Tuple<TrackedProcess, int>>();
            // One WMI snapshot preserves ancestry even after a bootstrapper has exited.
            using (var search = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,CreationDate,Name FROM Win32_Process"))
            using (var objects = search.Get())
                foreach (ManagementObject row in objects)
                    using (row)
                    {
                        if (row["CreationDate"] == null) continue;
                        rows.Add(Tuple.Create(new TrackedProcess
                        {
                            Id = Convert.ToInt32(row["ProcessId"]),
                            Name = Path.GetFileNameWithoutExtension((string)row["Name"]),
                            StartTimeUtcTicks = ManagementDateTimeConverter.ToDateTime((string)row["CreationDate"]).ToUniversalTime().Ticks
                        }, Convert.ToInt32(row["ParentProcessId"])));
                    }
            var known = parents.ToDictionary(p => p.Id, p => p);
            var result = new List<TrackedProcess>();
            bool added;
            do
            {
                added = false;
                foreach (var row in rows)
                {
                    TrackedProcess parent;
                    if (known.ContainsKey(row.Item1.Id) || !known.TryGetValue(row.Item2, out parent)) continue;
                    // WMI timestamps have microsecond precision. Never attach an older PID reuse.
                    if (row.Item1.StartTimeUtcTicks < parent.StartTimeUtcTicks) continue;
                    var presentParent = rows.FirstOrDefault(p => p.Item1.Id == parent.Id);
                    if (presentParent != null && presentParent.Item1.StartTimeUtcTicks / 10 != parent.StartTimeUtcTicks / 10) continue;
                    // Normalize to the kernel process creation time used by all destructive operations.
                    using (var child = Open(row.Item1.Id))
                    {
                        if (child == null) continue;
                        TrackedProcess identity;
                        try { identity = Identity(child); }
                        catch (InvalidOperationException) { continue; }
                        if (identity.StartTimeUtcTicks / 10 != row.Item1.StartTimeUtcTicks / 10) continue;
                        known.Add(identity.Id, identity);
                        result.Add(identity);
                        added = true;
                    }
                }
            } while (added);
            return result;
        }

        public bool IsAlive(TrackedProcess process)
        {
            using (var handle = OpenOwned(process, 0x1000 | 0x100000))
                return handle != null && IsRunning(handle);
        }

        public void RequestClose(TrackedProcess process)
        {
            using (var handle = OpenOwned(process, 0x1000 | 0x100000))
            {
                if (handle == null || !IsRunning(handle)) return;
                // Keep the identity's kernel handle open while locating its main window.
                using (var current = Open(process.Id))
                    if (current != null && Matches(current, process) && !current.HasExited) current.CloseMainWindow();
            }
        }

        public void Terminate(TrackedProcess process)
        {
            // Query creation time and terminate through the SAME kernel handle. A PID
            // cannot be swapped between verification and the destructive operation.
            using (var handle = OpenOwned(process, 0x1000 | 0x100000 | 1))
                if (handle != null && IsRunning(handle) && !TerminateProcess(handle, 1))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot terminate the owned game process; check launcher/game elevation levels.");
        }

        private static bool IsRunning(SafeProcessHandle handle)
        {
            uint result = WaitForSingleObject(handle, 0);
            if (result == 258) return true;
            if (result == 0) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query the owned process wait state.");
        }

        private static SafeProcessHandle OpenOwned(TrackedProcess process, uint access)
        {
            if (process == null) throw new ArgumentNullException(nameof(process));
            var handle = OpenProcess(access, false, process.Id);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == 87) return null; // ERROR_INVALID_PARAMETER: PID no longer exists.
                throw new Win32Exception(error, "Cannot inspect the owned process. Access denied is not evidence that it exited.");
            }
            long created, exited, kernel, user;
            if (!GetProcessTimes(handle, out created, out exited, out kernel, out user))
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Cannot verify the owned process creation time.");
            }
            if (DateTime.FromFileTimeUtc(created).Ticks != process.StartTimeUtcTicks) { handle.Dispose(); return null; }
            return handle;
        }

        private static Process Open(int id)
        {
            try { return Process.GetProcessById(id); }
            catch (ArgumentException) { return null; }
            // Access denied must propagate: it is not evidence of process exit.
        }
        private static bool Matches(Process current, TrackedProcess expected)
        {
            try { return current.StartTime.ToUniversalTime().Ticks == expected.StartTimeUtcTicks; }
            catch (InvalidOperationException) { return false; }
        }
        private static TrackedProcess Identity(Process process)
        {
            return new TrackedProcess { Id = process.Id, StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks, Name = process.ProcessName };
        }
        private static string Resolve(string root, string path)
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)] private static extern bool GetProcessTimesRaw(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle handle, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
    }
}
