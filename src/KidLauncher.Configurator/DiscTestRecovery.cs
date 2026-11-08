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
        public string VcdMountPath { get; set; }
    }

    public sealed partial class MainWindow
    {
        string DiscTestJournal => Path.Combine(store.DataDirectory, "configurator-disc-test.json");
        void SaveDiscTestJournal(DiscSettings disc)
        {
            var record = new DiscTestRecovery { Disc = disc, VcdMountPath = store.ResolvePath(config.VcdMountPath) };
            var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(record));
            using (var stream = new FileStream(DiscTestJournal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        async Task RestoreDiscTest()
        {
            if (!File.Exists(DiscTestJournal)) return;
            var record = new JavaScriptSerializer().Deserialize<DiscTestRecovery>(File.ReadAllText(DiscTestJournal));
            if (record?.Disc == null) throw new InvalidDataException("Disc test recovery state is invalid. Preserve it for diagnostics and inspect the managed unit in Virtual CloneDrive.");
            ValidateDiscTest(record.Disc);
            var service = new VirtualCloneDriveService(record.VcdMountPath, log); service.ValidateMapping(record.Disc);
            using (var timeout = new CancellationTokenSource(Math.Max(10000, record.Disc.MountTimeoutMs)))
            {
                await service.UnmountAsync(record.Disc, timeout.Token);
            }
            File.Delete(DiscTestJournal);
        }
    }
}
