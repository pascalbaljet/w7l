using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using KidLauncher.Core;

namespace KidLauncher.Configurator
{
    public sealed class DiscTestRecovery
    {
        public DiscSettings Disc { get; set; }
        public string PreviousImage { get; set; }
        public string VcdMountPath { get; set; }
    }

    public sealed partial class MainWindow
    {
        string DiscTestJournal => Path.Combine(store.DataDirectory, "configurator-disc-test.json");
        void SaveDiscTestJournal(DiscSettings disc, string previous)
        {
            var record = new DiscTestRecovery { Disc = disc, PreviousImage = previous, VcdMountPath = store.ResolvePath(config.VcdMountPath) };
            var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(record));
            using (var stream = new FileStream(DiscTestJournal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        async Task RestoreDiscTest()
        {
            if (!File.Exists(DiscTestJournal)) return;
            var record = new JavaScriptSerializer().Deserialize<DiscTestRecovery>(File.ReadAllText(DiscTestJournal));
            if (record?.Disc == null || record.PreviousImage == null) throw new InvalidDataException("Disc test recovery state is invalid. Preserve it for diagnostics and inspect the managed unit in Virtual CloneDrive.");
            ValidateDiscTest(record.Disc);
            var service = new VirtualCloneDriveService(record.VcdMountPath, log); service.ValidateMapping(record.Disc);
            if (!string.IsNullOrEmpty(record.PreviousImage) && string.Equals(service.GetMountedImage(record.Disc), record.PreviousImage, StringComparison.OrdinalIgnoreCase))
            { File.Delete(DiscTestJournal); return; }
            using (var timeout = new CancellationTokenSource(Math.Max(10000, record.Disc.MountTimeoutMs)))
            {
                await service.UnmountAsync(record.Disc, timeout.Token);
                if (!string.IsNullOrEmpty(record.PreviousImage))
                {
                    if (!File.Exists(record.PreviousImage)) throw new FileNotFoundException("The previous image is no longer available. Restore it before retrying recovery.", record.PreviousImage);
                    // The previous disc must not be checked against the test game's
                    // volume label/readiness file. Verify its drive readiness instead.
                    var original = new DiscSettings { Enabled = true, VirtualDriveIndex = record.Disc.VirtualDriveIndex,
                        ExpectedDriveLetter = record.Disc.ExpectedDriveLetter, MountTimeoutMs = record.Disc.MountTimeoutMs,
                        ImagePath = record.PreviousImage };
                    await service.MountAsync(original, record.PreviousImage, timeout.Token);
                }
            }
            File.Delete(DiscTestJournal);
        }
    }
}
