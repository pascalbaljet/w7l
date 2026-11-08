using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using KidLauncher.Core;

namespace KidLauncher.Configurator
{
    public sealed partial class MainWindow
    {
        Game observedGame;
        bool restorePending;
        string DisplayTestJournal => Path.Combine(store.DataDirectory, "configurator-display-test.json");

        void SetBusy(bool value) { busy = value; tabs.IsEnabled = !value; }
        FileStream AcquireStandaloneOwnership(bool recovery = false)
        {
            FileStream ownership;
            try { ownership = new FileStream(Path.Combine(store.DataDirectory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw new InvalidOperationException("Another launcher/configurator session is using the display or disc. Finish that session and retry.", ex); }
            if (!recovery && File.Exists(Path.Combine(store.DataDirectory, "display-session.json")))
            { ownership.Dispose(); throw new InvalidOperationException("An interrupted game session needs recovery before standalone tests. Use Diagnostics → Retry recovery."); }
            return ownership;
        }
        void BeforeTest()
        {
            EnsureIdle();
            if (smoke) throw new InvalidOperationException("Hardware tests are disabled in smoke-test mode.");
            if (loadFailed) throw new InvalidOperationException("Restore a readable configuration and reopen the configurator before testing.");
            if (restorePending || File.Exists(DisplayTestJournal) || File.Exists(DiscTestJournal) || File.Exists(Path.Combine(store.DataDirectory, "display-session.json")))
                throw new InvalidOperationException("An interrupted session still needs recovery. Use Diagnostics → Retry interrupted-session recovery first.");
            if (HasValidationError(tabs)) throw new InvalidOperationException("Correct highlighted invalid fields before testing.");
        }
        GameSession NewSession()
        {
            var result = new GameSession(store, config, display, new WindowsProcessService(), new VirtualCloneDriveService(store.ResolvePath(config.VcdMountPath), log), log);
            result.StatusChanged += message => Dispatcher.BeginInvoke(new Action(() => status.Text = message));
            return result;
        }
        async void TestGame(Game game)
        {
            bool started = false;
            try
            {
                BeforeTest();
                var snapshot = Copy(game); snapshot.Title = game.Title; snapshot.Id = game.Id;
                var errors = store.Validate(new LauncherConfiguration { Games = new List<Game> { snapshot }, Appearance = config.Appearance, Controls = config.Controls, VcdMountPath = config.VcdMountPath }, true);
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
                SetBusy(true); started = true;
                session = null; observed.Clear(); observedGame = game;
                testHotkey = new WindowsHotkey(new WindowInteropHelper(this).Handle, 0x75, () =>
                {
                    log.Write("Configurator F6 received; source=" + testHotkey.LastTrigger + "; active game session: " + (session != null && session.IsRunning));
                    StopTest();
                }); testHotkey.Enabled = true;
                if (!testHotkey.Enabled) throw new InvalidOperationException("F6 could not be enabled; the test has not started.");
                status.Text = "F6 exits the test. " + testHotkey.Strategy;
                log.Write("Configurator test hotkey enabled: " + testHotkey.Strategy);
                session = NewSession(); observedGame = game; observed.Clear();
                await session.RunAsync(snapshot, CancellationToken.None);
                status.Text = "Test finished; display and disc cleanup completed. Select observed process names, then save.";
            }
            catch (Exception ex) { Error("Game test failed", ex); }
            finally
            {
                if (started)
                {
                    if (session != null) observed = session.ObservedProcesses.ToList();
                    if (testHotkey != null) { testHotkey.Dispose(); testHotkey = null; }
                    SetBusy(false); Activate();
                }
            }
            if (started && observed.Count > 0) SelectProcesses(game);
        }
        async void StopTest()
        {
            try { if (session != null && session.IsRunning) { status.Text = "Closing tracked game processes and restoring the session…"; await session.RequestExitAsync(); } }
            catch (Exception ex) { Error("Could not stop test", ex); }
        }
        void SelectProcesses(Game game)
        {
            if (observedGame != game || observed.Count == 0) { MessageBox.Show(this, "Test this game first. Only processes actually observed by its shared game session will appear here.", "Process candidates"); return; }
            var dialog = new Window { Owner = this, Title = "Observed processes — " + game.Title, Width = 620, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var root = new DockPanel { Margin = new Thickness(18) }; dialog.Content = root;
            var note = Note("Select only the real game / handoff executables. These names came from this test session, not an unfiltered list of every Windows process. PID and start time help distinguish launches."); DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
            var list = new ListBox { SelectionMode = SelectionMode.Multiple };
            foreach (var process in observed.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                var item = new ListBoxItem { Content = process.Name + "   (PID " + process.Id + ", " + new DateTime(process.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss") + ")", Tag = process.Name };
                list.Items.Add(item); item.IsSelected = game.ProcessNames.Contains(process.Name, StringComparer.OrdinalIgnoreCase);
            }
            var save = Button("Use selected process names", () =>
            {
                game.ProcessNames = list.SelectedItems.Cast<ListBoxItem>().Select(i => (string)i.Tag).ToList(); dialog.DialogResult = true; BuildEditor();
            });
            DockPanel.SetDock(save, Dock.Bottom); root.Children.Add(save); root.Children.Add(list); dialog.ShowDialog();
        }
        void SaveDisplayTestJournal(DisplayMode mode)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(mode));
            using (var stream = new FileStream(DisplayTestJournal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        void RestoreDisplayTest()
        {
            if (!File.Exists(DisplayTestJournal)) return;
            var original = new JavaScriptSerializer().Deserialize<DisplayMode>(File.ReadAllText(DisplayTestJournal));
            if (original == null || original.NativeData == null) throw new InvalidDataException("Display test recovery journal is invalid. Keep it for diagnosis and restore a supported mode using Windows Screen Resolution.");
            display.Apply(original); File.Delete(DisplayTestJournal); restorePending = false;
        }
        void TestResolution(DisplayMode mode)
        {
            bool started = false;
            Exception failure = null;
            FileStream ownership = null;
            try
            {
                BeforeTest(); ownership = AcquireStandaloneOwnership(); SetBusy(true); started = true;
                SaveDisplayTestJournal(display.Capture());
                if (mode != null) display.Apply(mode);
                var dialog = new Window { Owner = this, Title = "Display test — automatic restore", Width = 430, Height = 230, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
                var p = Panel(); dialog.Content = p; int seconds = 15;
                var countdown = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 18, Margin = new Thickness(0, 10, 0, 16) }; p.Children.Add(countdown);
                Action update = () => countdown.Text = "Can you see this correctly? The original mode will be restored in " + seconds + " seconds, even if you confirm.";
                update(); p.Children.Add(Button("Looks good — restore now", () => dialog.DialogResult = true));
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (s, e) => { seconds--; update(); if (seconds <= 0) dialog.Close(); };
                dialog.Closed += (s, e) => timer.Stop(); timer.Start();
                try { dialog.ShowDialog(); } finally { timer.Stop(); }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (started)
                {
                    try { RestoreDisplayTest(); status.Text = "Original display mode restored after test."; }
                    catch (Exception ex) { restorePending = true; failure = failure == null ? ex : new AggregateException(failure, ex); }
                    SetBusy(false);
                }
                ownership?.Dispose();
            }
            if (failure != null) Error(restorePending ? "Display restoration needs attention — retry in Diagnostics" : "Display test failed", failure);
        }
        void Recover() => Recover(false);
        void ConfirmRecovery()
        {
            if (MessageBox.Show(this, "Close every running game first. Only continue when you have checked that no game is still running.\n\nConfirm this check and restore the interrupted session's display and disc?", "Confirm games are closed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                Recover(true);
        }
        async void Recover(bool confirmNoUnrecordedGameIsRunning)
        {
            bool started = false;
            try
            {
                EnsureIdle(); if (smoke) throw new InvalidOperationException("Recovery is disabled in smoke mode.");
                SetBusy(true); started = true;
                using (AcquireStandaloneOwnership(true)) { RestoreDisplayTest(); await RestoreDiscTest(); }
                // GameSession acquires the same file itself. Do not hold it twice.
                await NewSession().RecoverAsync(confirmNoUnrecordedGameIsRunning); restorePending = false; status.Text = "Interrupted-session recovery completed.";
            }
            catch (Exception ex) { restorePending = true; Error("Recovery needs attention", ex); }
            finally { if (started) SetBusy(false); }
        }
    }
}
