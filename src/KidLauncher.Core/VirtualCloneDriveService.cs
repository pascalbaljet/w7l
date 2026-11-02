using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
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
        private readonly string stateDirectory;
        public VirtualCloneDriveService(string vcdMountPath, ILog log, string stateDirectory = null)
        {
            executable = string.IsNullOrWhiteSpace(vcdMountPath) ? DetectExecutable() : Path.GetFullPath(vcdMountPath);
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.stateDirectory = stateDirectory == null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KidLauncher") : Path.GetFullPath(stateDirectory);
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

        public string GetMountedImage(DiscSettings disc)
        {
            EnterAsync(disc, CancellationToken.None).GetAwaiter().GetResult();
            try { ValidateMapping(disc); return KnownImage(disc); }
            finally { Exit(); }
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
                            ". Legacy configured index " + disc.VirtualDriveIndex + " is a journal lookup key only; /l=" + letter[0] + " resolves the actual VCD target. Integer slot mapping is not assumed.");
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
                string previous = KnownImage(disc);
                if (string.Equals(previous, image, StringComparison.OrdinalIgnoreCase))
                {
                    await WaitReadyCoreAsync(disc, cancellationToken).ConfigureAwait(false);
                    return;
                }
                bool occupied = !IsEmpty(disc);
                if (disc.RestorePreviousImage && occupied && previous == null)
                    throw new InvalidOperationException("The previous image was mounted outside KidLauncher or its live media generation cannot be verified. Disable Restore Previous Image to authorize replacement of the configured managed unit, or eject it manually first.");
                // MountAsync is explicit replacement intent. Cleanup-only UnmountAsync
                // intentionally has stricter ownership checks. Do not pre-unmount here
                // through that public method (nor from GameSession).
                DeleteJournal(disc.VirtualDriveIndex);
                if (occupied)
                {
                    await RunAsync(disc, "/u", cancellationToken).ConfigureAwait(false);
                    await WaitEmptyAsync(disc, cancellationToken).ConfigureAwait(false);
                }
                await RunAsync(disc, Quote(image), cancellationToken).ConfigureAwait(false);
                // Establish ownership before optional content checks can fail, so a
                // wrong label/readiness file still permits safe session cleanup.
                var readable = new DiscSettings { VirtualDriveIndex = disc.VirtualDriveIndex,
                    ExpectedDriveLetter = disc.ExpectedDriveLetter, MountTimeoutMs = disc.MountTimeoutMs };
                await WaitReadyCoreAsync(readable, CancellationToken.None).ConfigureAwait(false);
                var media = ReadMedia(disc);
                WriteJournal(disc, image, media);
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
                if (IsEmpty(disc)) { DeleteJournal(disc.VirtualDriveIndex); return; }
                string current = KnownImage(disc);
                if (current == null || !Path.IsPathRooted(disc.ImagePath) || !string.Equals(current, Path.GetFullPath(disc.ImagePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Cleanup refused: the selected unit no longer contains the verified KidLauncher image. Another program may have changed the disc. Its media has been preserved. Use MountAsync, not UnmountAsync, for intentional replacement.");
                ValidateMapping(disc);
                await RunAsync(disc, "/u", cancellationToken).ConfigureAwait(false);
                await WaitEmptyAsync(disc, cancellationToken).ConfigureAwait(false);
                DeleteJournal(disc.VirtualDriveIndex);
                log.Write("Verified image ejected from VCD unit " + disc.VirtualDriveIndex);
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

        private sealed class MediaIdentity
        {
            public string Device, Boot, Volume;
            public uint Generation;
        }

        private string KnownImage(DiscSettings disc)
        {
            try
            {
                int unit = disc.VirtualDriveIndex;
                string path = JournalPath(unit);
                if (!File.Exists(path)) return null;
                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 }))
                    document.Load(reader);
                var root = document.DocumentElement;
                if (root == null || root.Name != "vcd" || root.GetAttribute("version") != "1" || root.GetAttribute("unit") != unit.ToString(System.Globalization.CultureInfo.InvariantCulture)) return null;
                if (!string.Equals(Letter(disc), root.GetAttribute("letter"), StringComparison.OrdinalIgnoreCase)) return null;
                var current = ReadMedia(disc);
                uint generation;
                if (!uint.TryParse(root.GetAttribute("generation"), out generation) || generation != current.Generation ||
                    !string.Equals(root.GetAttribute("device"), current.Device, StringComparison.OrdinalIgnoreCase) ||
                    root.GetAttribute("boot") != current.Boot || root.GetAttribute("volume") != current.Volume) return null;
                string image = root.GetAttribute("image");
                return Path.IsPathRooted(image) ? image : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException || ex is Win32Exception || ex is ManagementException || ex is InvalidOperationException || ex is ArgumentException)
            {
                log.Write("VCD slot " + disc.VirtualDriveIndex + ": journal cannot establish live image identity: " + ex.Message);
                return null;
            }
        }

        private void WriteJournal(DiscSettings disc, string image, MediaIdentity media)
        {
            Directory.CreateDirectory(stateDirectory);
            string path = JournalPath(disc.VirtualDriveIndex);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false }))
                    {
                        writer.WriteStartElement("vcd");
                        writer.WriteAttributeString("version", "1");
                        writer.WriteAttributeString("unit", disc.VirtualDriveIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        writer.WriteAttributeString("letter", Letter(disc));
                        writer.WriteAttributeString("image", image);
                        writer.WriteAttributeString("device", media.Device);
                        writer.WriteAttributeString("boot", media.Boot);
                        writer.WriteAttributeString("volume", media.Volume);
                        writer.WriteAttributeString("generation", media.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        writer.WriteEndElement();
                    }
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private string JournalPath(int unit) { return Path.Combine(stateDirectory, "vcd-unit-" + unit + ".xml"); }
        private void DeleteJournal(int unit) { string path = JournalPath(unit); if (File.Exists(path)) File.Delete(path); }

        private static MediaIdentity ReadMedia(DiscSettings disc)
        {
            string device = null;
            using (var search = new ManagementObjectSearcher("SELECT Drive,PNPDeviceID FROM Win32_CDROMDrive"))
            using (var devices = search.Get())
                foreach (ManagementObject item in devices)
                    using (item)
                        if (string.Equals(item["Drive"] as string, Letter(disc), StringComparison.OrdinalIgnoreCase)) device = item["PNPDeviceID"] as string;
            if (string.IsNullOrEmpty(device)) throw new InvalidOperationException("VCD device identity is unavailable.");
            string boot = null;
            using (var search = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
            using (var systems = search.Get())
                foreach (ManagementObject item in systems)
                    using (item) boot = item["LastBootUpTime"] as string;
            if (string.IsNullOrEmpty(boot)) throw new InvalidOperationException("Cannot verify the current Windows boot session; the VCD journal is not trusted.");
            uint generation = MediaGeneration(disc);
            var label = new StringBuilder(261);
            var fileSystem = new StringBuilder(261);
            uint serial, maxComponent, flags;
            if (!GetVolumeInformation(Letter(disc) + @"\", label, label.Capacity, out serial, out maxComponent, out flags, fileSystem, fileSystem.Capacity))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the VCD volume identity.");
            uint after = MediaGeneration(disc);
            if (after != generation) throw new InvalidOperationException("The VCD media changed while checking its identity.");
            return new MediaIdentity { Device = device, Boot = boot, Generation = generation,
                Volume = serial.ToString("X8") + "|" + label + "|" + fileSystem };
        }

        private static uint MediaGeneration(DiscSettings disc)
        {
            using (var handle = CreateFile(@"\\.\" + Letter(disc), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open VCD to verify its media generation.");
                var data = Marshal.AllocHGlobal(4);
                try
                {
                    int returned;
                    if (!DeviceIoControl(handle, 0x002D0800, IntPtr.Zero, 0, data, 4, out returned, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "The VCD media generation could not be read.");
                    if (returned != 4) throw new InvalidOperationException("This VCD driver does not expose a media-change counter. Automatic reuse/cleanup cannot be promised; use manual eject.");
                    return unchecked((uint)Marshal.ReadInt32(data));
                }
                finally { Marshal.FreeHGlobal(data); }
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeInformation(string root, StringBuilder label, int labelSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, int fileSystemSize);
    }
}
