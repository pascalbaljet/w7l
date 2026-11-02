using System;
using System.Collections.Generic;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Win32;
using KidLauncher.Core;

namespace KidLauncher.Configurator
{
    internal sealed class VirtualUnit
    {
        public int Index;
        public string Letter;
        public override string ToString() { return Letter + "  (verified ELBY drive; managed slot " + Index + ")"; }
    }

    public sealed partial class MainWindow
    {
        async void DiscoverUnits(ListBox list, TextBlock description)
        {
            bool started = false;
            try
            {
                EnsureIdle(); if (smoke) throw new InvalidOperationException("Discovery is not run in smoke mode.");
                SetBusy(true); started = true; description.Text = "Querying the Windows CD-ROM driver…";
                list.ItemsSource = await Task.Run(() =>
                {
                    var result = new List<VirtualUnit>();
                    var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = false };
                    using (var query = new ManagementObjectSearcher(new ManagementScope("root\\CIMV2"), new ObjectQuery("SELECT Drive,PNPDeviceID,SCSITargetId FROM Win32_CDROMDrive"), options))
                    using (var devices = query.Get())
                        foreach (ManagementObject device in devices)
                            using (device)
                            {
                                string pnp = device["PNPDeviceID"] as string, letter = device["Drive"] as string;
                                if (pnp == null || string.IsNullOrEmpty(letter) || pnp.IndexOf("VEN_ELBY&PROD_CLONEDRIVE", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                var candidate = new DiscSettings { VirtualDriveIndex = result.Count, ExpectedDriveLetter = letter };
                                // /l addresses the driver's real target; this index is
                                // only a stable key once saved in the game settings.
                                try { DiscService().ValidateMapping(candidate); }
                                catch (InvalidOperationException) { continue; }
                                if (result.Count < 15) result.Add(new VirtualUnit { Index = result.Count, Letter = letter });
                            }
                    return result;
                });
                description.Text = list.Items.Count == 0 ? "No ELBY drives matched the current user's VCDDriveMask. Open Virtual CloneDrive settings under this Windows account and retry." : "Select a verified drive. VCDMount resolves its actual unit through /l=<letter>; managed slot numbers are journal keys, not guessed driver indices.";
            }
            catch (Exception ex) { Error("Virtual-unit discovery failed", ex); }
            finally { if (started) SetBusy(false); }
        }
    }
}
