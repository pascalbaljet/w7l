using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KidLauncher.Core;

namespace KidLauncher.Configurator
{
    public sealed partial class MainWindow
    {
        UIElement BuildDrives()
        {
            var p = Panel(); Heading(p, "Virtual CloneDrive");
            p.Children.Add(Note("Reuse an existing Virtual CloneDrive drive. The service verifies its ELBY device identity and VCDDriveMask entry, then addresses it by letter so VCDMount resolves the real unit. No drivers or drives are created."));
            var path = Field(p, "VCDMount.exe path", config, "VcdMountPath");
            p.Children.Add(Buttons(Button("Detect VCDMount.exe", () => { path.Text = VirtualCloneDriveService.DetectExecutable() ?? ""; status.Text = string.IsNullOrEmpty(path.Text) ? "Not detected. Browse to the installed VCDMount.exe." : "Detected " + path.Text; }),
                Button("Browse…", () => { string file = Browse("Virtual CloneDrive utility|VCDMount.exe|Executables|*.exe"); if (file != null) path.Text = file; })));
            var units = new ListBox { MinHeight = 70, MaxHeight = 150, Margin = new Thickness(0, 8, 0, 8) };
            var unitNote = Note("Discover existing virtual units, or enter and verify the unit and letter manually in Games.");
            p.Children.Add(units); p.Children.Add(unitNote);
            p.Children.Add(Buttons(Button("Discover ELBY unit mappings", () => DiscoverUnits(units, unitNote)), Button("Use selected unit for selected game", () =>
            {
                EnsureIdle(); var unit = units.SelectedItem as VirtualUnit;
                if (unit == null || selected == null) throw new InvalidOperationException("Select a game in Games and a detected virtual unit first.");
                var disc = new DiscSettings { VirtualDriveIndex = unit.Index, ExpectedDriveLetter = unit.Letter }; DiscService().ValidateMapping(disc);
                selected.Disc.VirtualDriveIndex = unit.Index; selected.Disc.ExpectedDriveLetter = unit.Letter; BuildEditor(); status.Text = "Selected unit assigned to " + selected.Title + ". Save configuration to keep it.";
            })));
            var drives = new TextBlock { TextWrapping = TextWrapping.Wrap }; p.Children.Add(drives);
            p.Children.Add(Button("Refresh Windows CD-ROM drives", () =>
            {
                drives.Text = string.Join("\n", DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).Select(d =>
                {
                    try { return d.Name + " — " + (d.IsReady ? d.VolumeLabel : "No readable disc"); } catch (Exception ex) { return d.Name + " — " + ex.Message; }
                }));
                if (string.IsNullOrEmpty(drives.Text)) drives.Text = "No Windows CD-ROM drive letters were found.";
            }));
            p.Children.Add(Note("To inspect or test a managed unit, select its game in Games, then use the fields below. Verification queries the actual unit/letter mapping; the Windows list above includes physical CD-ROM drives and must not be used to guess a VCD index."));
            p.Children.Add(Button("Edit selected game's disc settings", () => { tabs.SelectedIndex = 0; BuildEditor(); }));
            return Scroll(p);
        }

        void BuildDiscFields(Panel p, DiscSettings disc)
        {
            Check(p, "This game requires a disc image", disc, "Enabled");
            PathField(p, "Disc image", disc, "ImagePath", "Disc images|*.iso;*.bin;*.img;*.udf;*.dvd;*.ccd|All files|*.*");
            Field(p, "Legacy slot ID (0–14; the drive is selected by letter)", disc, "VirtualDriveIndex");
            Field(p, "Expected Windows drive letter (for example V:)", disc, "ExpectedDriveLetter");
            Field(p, "Expected volume label (optional)", disc, "ExpectedVolumeLabel");
            var ready = Field(p, "Readiness file relative to disc root (optional)", disc, "ReadinessFile");
            p.Children.Add(Button("Choose file on mounted disc…", () =>
            {
                string file = Browse("All files|*.*"); if (file == null) return;
                string root = (disc.ExpectedDriveLetter ?? "").TrimEnd('\\') + "\\";
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || root.Length < 3) throw new InvalidOperationException("Choose a file on the configured drive letter, not a local game file.");
                ready.Text = file.Substring(root.Length);
            }));
            Field(p, "Mount / readiness timeout (milliseconds, 1000–120000)", disc, "MountTimeoutMs");
            Check(p, "Unmount after the game exits", disc, "UnmountAfterExit");
            p.Children.Add(Buttons(Button("Verify unit ↔ letter and read volume", () => VerifyDisc(disc)), Button("Mount and readiness test…", () => TestDisc(disc, false)), Button("Unmount this unit…", () => TestDisc(disc, true))));
            p.Children.Add(Note("This drive is dedicated to KidLauncher. Starting a game or mount test replaces any existing disc, even one mounted outside KidLauncher. Previous discs are never restored. Mount tests eject the disc when complete. Other drives are not changed."));
        }
        IDiscService DiscService() { return new VirtualCloneDriveService(store.ResolvePath(config.VcdMountPath), log); }
        static void ValidateDiscTest(DiscSettings disc)
        {
            if (disc.VirtualDriveIndex < 0 || disc.VirtualDriveIndex > 14) throw new InvalidOperationException("Choose an existing Virtual CloneDrive unit from 0–14.");
            if (disc.MountTimeoutMs < 1000 || disc.MountTimeoutMs > 120000) throw new InvalidOperationException("The test timeout must be 1000–120000 milliseconds.");
            string letter = (disc.ExpectedDriveLetter ?? "").TrimEnd('\\');
            if (letter.Length != 2 || !char.IsLetter(letter[0]) || letter[1] != ':') throw new InvalidOperationException("Set the expected Windows drive letter, such as V:.");
        }
        void VerifyDisc(DiscSettings disc)
        {
            EnsureIdle(); ValidateDiscTest(disc); DiscService().ValidateMapping(disc);
            var drive = new DriveInfo(disc.ExpectedDriveLetter.TrimEnd('\\') + "\\");
            MessageBox.Show(this, "The selected letter is a verified ELBY Virtual CloneDrive device. VCDMount resolves its actual unit by letter.\n" + drive.Name + "\n" + (drive.IsReady ? "Volume: " + drive.VolumeLabel : "No readable disc is currently mounted."), "Virtual drive verified");
        }
        async void TestDisc(DiscSettings disc, bool unmountOnly)
        {
            bool started = false, changed = false;
            IDiscService service = null;
            DiscSettings snapshot = null;
            FileStream ownership = null;
            Exception failure = null;
            try
            {
                BeforeTest(); ValidateDiscTest(disc);
                ownership = AcquireStandaloneOwnership();
                snapshot = Copy(new Game { Disc = disc }).Disc; snapshot.ImagePath = store.ResolvePath(snapshot.ImagePath);
                service = DiscService(); service.ValidateMapping(snapshot);
                if (MessageBox.Show(this, "This will " + (unmountOnly ? "eject the disc from" : "replace any disc in") + " the dedicated VCD drive " + snapshot.ExpectedDriveLetter + ". Confirm no program is using this disc. The previous disc will not be restored.", "Confirm dedicated drive", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                SetBusy(true); started = true;
                using (var timeout = new CancellationTokenSource(snapshot.MountTimeoutMs))
                {
                    if (unmountOnly) { await service.UnmountAsync(snapshot, timeout.Token); status.Text = "Managed unit unmounted."; }
                    else
                    {
                        string image = store.ResolvePath(snapshot.ImagePath); if (!File.Exists(image)) throw new FileNotFoundException("Select an existing image file.", image);
                        SaveDiscTestJournal(snapshot); changed = true;
                        await service.MountAsync(snapshot, image, timeout.Token); await service.WaitReadyAsync(snapshot, timeout.Token);
                        var drive = new DriveInfo(snapshot.ExpectedDriveLetter.TrimEnd('\\') + "\\");
                        MessageBox.Show(this, "Disc ready.\nDrive: " + drive.Name + "\nVolume: " + drive.VolumeLabel + "\nClose this message to complete test cleanup.", "Disc test successful");
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (changed)
                {
                    try { await RestoreDiscTest(); status.Text = "Disc test complete; dedicated drive ejected."; }
                    catch (Exception ex) { restorePending = true; failure = failure == null ? ex : new AggregateException(failure, ex); }
                }
                if (started) SetBusy(false);
                ownership?.Dispose();
            }
            if (failure != null) Error(restorePending ? "Disc test cleanup needs attention — retry recovery" : "Disc test failed", failure);
        }
    }
}
