using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace KidLauncher.Core
{
    public sealed class VirtualCloneDriveService : IDiscService
    {
        private static readonly SemaphoreSlim Commands = new SemaphoreSlim(1, 1);
        private static FileStream commandLock;
        private readonly string executable;
        private readonly ILog log;
        public VirtualCloneDriveService(string vcdMountPath, ILog log)
        {
            executable = string.IsNullOrWhiteSpace(vcdMountPath) ? DetectExecutable() : Path.GetFullPath(vcdMountPath);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public static string DetectExecutable()
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = root.OpenSubKey(@"SOFTWARE\Elaborate Bytes\VirtualCloneDrive"))
                {
                    var directory = key == null ? null : key.GetValue("Install_Dir") as string;
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        var candidate = Path.Combine(directory, "VCDMount.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                var candidate = Path.Combine(Environment.GetFolderPath(folder), "Elaborate Bytes", "VirtualCloneDrive", "VCDMount.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        public void ValidateMapping(DiscSettings disc)
        {
            if (disc == null) throw new ArgumentNullException(nameof(disc));
            Timeout(disc);
            if (disc.VirtualDriveIndex < 0 || disc.VirtualDriveIndex > 14) throw new InvalidOperationException("VCD unit must be between 0 and 14.");
            string letter = Letter(disc);
            // SageTV uses the installed VCDDriveMask and VCDMount /l=<letter> for
            // both mount and eject. Let VCD resolve its own slot from that letter;
            // never infer /d from Windows CDROM index, SCSI target or PnP UINumber.
            // https://github.com/google/sagetv/blob/1888f18c4b7bc27b201e25953b2817728fd39da7/java/sage/FSManager.java#L248-L299
            // https://github.com/google/sagetv/blob/1888f18c4b7bc27b201e25953b2817728fd39da7/java/sage/FSManager.java#L353-L367
            uint mask = 0;
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                using (var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                using (var key = user.OpenSubKey(@"Software\Elaborate Bytes\VirtualCloneDrive"))
                {
                    object value = key == null ? null : key.GetValue("VCDDriveMask");
                    if (value is int) mask |= unchecked((uint)(int)value);
                }
            if ((mask & (1u << (letter[0] - 'A'))) == 0)
                throw new InvalidOperationException(letter + " is not in the current user's installed VCDDriveMask. Open Virtual CloneDrive settings under this Windows account and verify the assigned drive letter. No drive was changed.");
            using (var query = new ManagementObjectSearcher("SELECT Drive,PNPDeviceID FROM Win32_CDROMDrive"))
            using (var devices = query.Get())
                foreach (ManagementObject device in devices)
                    using (device)
                    {
                        if (!string.Equals(device["Drive"] as string, letter, StringComparison.OrdinalIgnoreCase)) continue;
                        string pnp = device["PNPDeviceID"] as string;
                        if (pnp == null || pnp.IndexOf("VEN_ELBY&PROD_CLONEDRIVE", StringComparison.OrdinalIgnoreCase) < 0)
                            throw new InvalidOperationException(letter + " is not an ELBY Virtual CloneDrive device.");
                        log.Write("Verified VCD letter " + letter + " against VCDDriveMask and ELBY PnP device " + pnp +
                            ". /l=" + letter[0] + " resolves the actual VCD target. Integer slot mapping is not assumed.");
                        return;
                    }
            throw new InvalidOperationException("Configured VCD drive " + letter + " was not found. Check its assigned letter in Disk Management.");
        }

        public async Task MountAsync(DiscSettings disc, string imagePath, CancellationToken cancellationToken)
        {
            await EnterAsync(disc, cancellationToken).ConfigureAwait(false);
            try
            {
                ValidateMapping(disc);
                string image = Path.GetFullPath(imagePath);
                if (!File.Exists(image)) throw new FileNotFoundException("Disc image not found.", image);
                // This verified drive is dedicated to KidLauncher. Always replace its
                // media, even if the previous image was mounted outside the launcher.
                if (!IsEmpty(disc))
                {
                    await RunAsync(disc, "/u", cancellationToken).ConfigureAwait(false);
                    await WaitEmptyAsync(disc, cancellationToken).ConfigureAwait(false);
                }
                await RunAsync(disc, Quote(image), cancellationToken).ConfigureAwait(false);
                await WaitReadyCoreAsync(disc, cancellationToken).ConfigureAwait(false);
                log.Write("VCD mount command and content verification completed for unit " + disc.VirtualDriveIndex + ": " + image);
            }
            finally { Exit(); }
        }

        public async Task UnmountAsync(DiscSettings disc, CancellationToken cancellationToken)
        {
            await EnterAsync(disc, cancellationToken).ConfigureAwait(false);
            try
            {
                ValidateMapping(disc);
                if (IsEmpty(disc)) return;
                await RunAsync(disc, "/u", cancellationToken).ConfigureAwait(false);
                await WaitEmptyAsync(disc, cancellationToken).ConfigureAwait(false);
                log.Write("Dedicated VCD drive ejected: " + Letter(disc));
            }
            finally { Exit(); }
        }

        public async Task WaitReadyAsync(DiscSettings disc, CancellationToken cancellationToken)
        {
            await EnterAsync(disc, cancellationToken).ConfigureAwait(false);
            try { ValidateMapping(disc); await WaitReadyCoreAsync(disc, cancellationToken).ConfigureAwait(false); }
            finally { Exit(); }
        }

        private static async Task EnterAsync(DiscSettings disc, CancellationToken token)
        {
            if (disc == null) throw new ArgumentNullException(nameof(disc));
            if (!await Commands.WaitAsync(Timeout(disc), token).ConfigureAwait(false))
                throw new TimeoutException("Another VCD operation is still active. Wait for it to finish before retrying.");
            try
            {
                // An exclusive file handle also serializes the launcher/configurator.
                // Unlike a named semaphore, Windows releases it after a process crash.
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KidLauncher");
                Directory.CreateDirectory(folder);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    try { commandLock = new FileStream(Path.Combine(folder, "vcd-command.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return; }
                    catch (IOException)
                    {
                        if (watch.ElapsedMilliseconds >= Timeout(disc)) throw new TimeoutException("Another KidLauncher process holds the VCD command lock.");
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                }
            }
            catch { Commands.Release(); throw; }
        }

        private static void Exit()
        {
            try { if (commandLock != null) commandLock.Dispose(); }
            finally { commandLock = null; Commands.Release(); }
        }

        private static async Task WaitEmptyAsync(DiscSettings disc, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            while (!IsEmpty(disc))
            {
                token.ThrowIfCancellationRequested();
                if (watch.ElapsedMilliseconds >= Timeout(disc)) throw new TimeoutException("VCD did not report an empty drive after eject.");
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }

        private async Task WaitReadyCoreAsync(DiscSettings disc, CancellationToken token)
        {
            int timeout = Timeout(disc);
            string root = Letter(disc) + @"\";
            string ready = null;
            if (!string.IsNullOrWhiteSpace(disc.ReadinessFile))
            {
                if (Path.IsPathRooted(disc.ReadinessFile)) throw new InvalidOperationException("Readiness file must be relative to the disc root.");
                ready = Path.GetFullPath(Path.Combine(root, disc.ReadinessFile));
                if (!ready.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Readiness file escapes the disc root.");
            }
            string diagnostic = "No readable media";
            var watch = Stopwatch.StartNew();
            do
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        bool label = string.IsNullOrEmpty(disc.ExpectedVolumeLabel) || string.Equals(drive.VolumeLabel, disc.ExpectedVolumeLabel, StringComparison.OrdinalIgnoreCase);
                        if (label && (ready == null || File.Exists(ready))) return;
                        diagnostic = "Unexpected volume label or missing readiness file";
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { diagnostic = ex.Message; }
                await Task.Delay(200, token).ConfigureAwait(false);
            } while (watch.ElapsedMilliseconds < timeout);
            throw new TimeoutException("VCD disc not ready at " + root + ": " + diagnostic);
        }

        private async Task RunAsync(DiscSettings disc, string arguments, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) throw new FileNotFoundException("Select the installed VCDMount.exe in Virtual Drives settings.", executable);
            token.ThrowIfCancellationRequested();
            ValidateMapping(disc);
            using (var process = Process.Start(new ProcessStartInfo(executable, "/l=" + Letter(disc)[0] + " " + arguments)
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable) }))
            {
                if (process == null) throw new InvalidOperationException("VCDMount did not start.");
                var identity = WindowsProcessService.StartedIdentity(process, executable);
                var watch = Stopwatch.StartNew();
                // Do not cancel a live native command and release serialization while it
                // can still change a drive. Finish it or terminate this exact handle first.
                while (!process.HasExited)
                {
                    if (watch.ElapsedMilliseconds >= Timeout(disc))
                    {
                        new WindowsProcessService().Terminate(identity);
                        if (!process.WaitForExit(3000)) throw new TimeoutException("VCDMount could not be stopped. Check the unit manually before retrying.");
                        throw new TimeoutException("VCDMount timed out. Verify the selected unit manually before retrying.");
                    }
                    await Task.Delay(100).ConfigureAwait(false);
                }
                if (process.ExitCode != 0) throw new InvalidOperationException("VCDMount failed with exit code " + process.ExitCode + ". Check permissions and the image format.");
            }
        }

        private static bool IsEmpty(DiscSettings disc)
        {
            using (var handle = CreateFile(@"\\.\" + Letter(disc), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query VCD media state.");
                int returned;
                if (DeviceIoControl(handle, 0x002D0800, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero)) return false;
                int error = Marshal.GetLastWin32Error();
                if (error == 21 || error == 1112) return true; // ERROR_NOT_READY / ERROR_NO_MEDIA_IN_DRIVE
                throw new Win32Exception(error, "Cannot establish whether the VCD unit is empty; no change was made.");
            }
        }

        private static int Timeout(DiscSettings disc)
        {
            if (disc.MountTimeoutMs < 100 || disc.MountTimeoutMs > 300000) throw new InvalidOperationException("Mount timeout must be 100–300000 ms.");
            return disc.MountTimeoutMs;
        }
        private static string Letter(DiscSettings disc)
        {
            string letter = (disc.ExpectedDriveLetter ?? "").Trim().TrimEnd('\\').ToUpperInvariant();
            if (letter.Length != 2 || letter[0] < 'A' || letter[0] > 'Z' || letter[1] != ':') throw new InvalidOperationException("Specify the exact expected drive letter, such as V:.");
            return letter;
        }
        private static string Quote(string value)
        {
            var output = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                output.Append(c);
                slashes = 0;
            }
            output.Append('\\', slashes * 2).Append('"');
            return output.ToString();
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    }
}
