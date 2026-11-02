using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using KidLauncher.Core;

namespace KidLauncher.Configurator
{
    public sealed partial class MainWindow : Window
    {
        readonly ConfigurationStore store;
        readonly LauncherConfiguration config;
        readonly IDisplayService display = new WindowsDisplayService();
        readonly ILog log;
        readonly bool smoke;
        bool loadFailed;
        string savedSnapshot;
        readonly ListBox games = new ListBox { MinWidth = 210, DisplayMemberPath = "Title", Margin = new Thickness(0, 0, 12, 0) };
        readonly ContentControl editor = new ContentControl();
        readonly TextBlock status = new TextBlock { Text = "Ready — changes are saved only when you choose Save configuration.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 10, 16, 10) };
        readonly TabControl tabs = new TabControl();
        Game selected;
        bool busy;
        GameSession session;
        WindowsHotkey testHotkey;
        List<TrackedProcess> observed = new List<TrackedProcess>();
        Canvas preview;
        TextBlock pageLabel;
        int previewPage;

        public MainWindow(bool smokeTest, string smokeDirectory = null)
        {
            smoke = smokeTest;
            Title = "KidLauncher · Parent configurator";
            Width = 1220; Height = 850; MinWidth = 960; MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
            Background = new SolidColorBrush(Color.FromRgb(242, 245, 249));
            if (smoke && string.IsNullOrEmpty(smokeDirectory)) throw new ArgumentException("Smoke mode requires an isolated temporary application directory.");
            store = new ConfigurationStore(smoke ? smokeDirectory : null);
            log = new FileLog(store.DataDirectory);
            try { config = smoke ? Sample(store.ApplicationDirectory) : store.Load(); }
            catch (Exception ex)
            {
                if (smoke) throw;
                MessageBox.Show("The configuration could not be loaded. No files have been replaced. Repair or restore the configuration before saving.\n\n" + ex.Message, "Load failed", MessageBoxButton.OK, MessageBoxImage.Error);
                loadFailed = true;
                config = new LauncherConfiguration();
            }
            config.Games = config.Games.OrderBy(g => g.SortOrder).ToList();
            var root = new DockPanel(); Content = root;
            var header = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };
            header.Children.Add(new TextBlock { Text = "KidLauncher", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.MidnightBlue });
            header.Children.Add(new TextBlock { Text = "Games, artwork and safe launch settings — for grown-ups", Foreground = Brushes.SlateGray });
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            var footer = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom);
            var save = Button("Save configuration", Save); save.IsEnabled = !loadFailed && !smoke;
            save.ToolTip = loadFailed ? "Load failed: restore a valid configuration and reopen. Saving is disabled to preserve the original file." : "Validate and safely save all settings";
            DockPanel.SetDock(save, Dock.Right); footer.Children.Add(save);
            var stop = Button("■ Stop test / F6", StopTest); DockPanel.SetDock(stop, Dock.Right); footer.Children.Add(stop);
            footer.Children.Add(status); root.Children.Add(footer);
            tabs.Margin = new Thickness(16, 0, 16, 4); root.Children.Add(tabs);
            AddTab("Games", BuildGames()); AddTab("Appearance", BuildAppearance()); AddTab("Controls", BuildControls());
            AddTab("Virtual drives", BuildDrives()); AddTab("General", BuildGeneral()); AddTab("Diagnostics", BuildDiagnostics());
            games.SelectionChanged += (s, e) => { selected = games.SelectedItem as Game; BuildEditor(); };
            RefreshGames();
            savedSnapshot = Fingerprint();
            Closing += OnClosing;
            if (!smoke) Loaded += (s, e) => Recover();
            tabs.SelectionChanged += (s, e) => { if (ReferenceEquals(e.Source, tabs)) RefreshPreview(); };
        }

        static LauncherConfiguration Sample(string applicationDirectory)
        {
            var c = new LauncherConfiguration();
            c.Games.Add(new Game { Title = "Putt-Putt Saves the Zoo", ExecutablePath = @"C:\Games\PuttPutt\puttzoo.exe", WorkingDirectory = @"C:\Games\PuttPutt", ProcessNames = new List<string> { "puttzoo" }, ArtworkPath = SampleArtwork(applicationDirectory, "Purple road trip", Color.FromRgb(108, 76, 172), "★") });
            c.Games.Add(new Game { Title = "Freddi Fish", ExecutablePath = @"C:\Games\Freddi\freddi.exe", ArtworkPath = SampleArtwork(applicationDirectory, "Underwater adventure", Color.FromRgb(33, 137, 170), "●") });
            for (int i = 3; i <= 14; i++) c.Games.Add(new Game { Title = "Adventure " + i, ExecutablePath = @"C:\Games\Adventure" + i + "\\game.exe" });
            return c;
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (busy || (session != null && session.IsRunning))
            {
                e.Cancel = true;
                MessageBox.Show("A test or recovery is active. Use Stop test / F6 and wait for display and disc cleanup before closing.", "Please wait");
                return;
            }
            if (!smoke && !loadFailed && savedSnapshot != Fingerprint())
            {
                var answer = MessageBox.Show(this, "Save your configuration changes before closing?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (answer == MessageBoxResult.Yes)
                {
                    try { Save(); e.Cancel = savedSnapshot != Fingerprint(); }
                    catch (Exception ex) { e.Cancel = true; Error("Save failed", ex); }
                    if (e.Cancel) return;
                }
            }
            if (!smoke && (restorePending || File.Exists(DisplayTestJournal) || File.Exists(DiscTestJournal)) && MessageBox.Show(this, "Recovery is still pending. Its journal will be kept. Close anyway? Reopen the configurator and retry recovery before playing.", "Unfinished recovery", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }
            if (testHotkey != null) { testHotkey.Dispose(); testHotkey = null; }
        }
        string Fingerprint() { return new JavaScriptSerializer().Serialize(config); }

        void AddTab(string title, UIElement child) { tabs.Items.Add(new TabItem { Header = title, Content = child }); }
        static StackPanel Panel() { return new StackPanel { Margin = new Thickness(16) }; }
        static ScrollViewer Scroll(UIElement child) { return new ScrollViewer { Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; }
        static TextBlock Note(string text) { return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12), Foreground = Brushes.SlateGray }; }
        static Button Button(string title, Action action)
        {
            var button = new Button { Content = title, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(3), MinHeight = 32 };
            button.Click += (s, e) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); } };
            return button;
        }
        static WrapPanel Buttons(params Button[] buttons) { var p = new WrapPanel(); foreach (var b in buttons) p.Children.Add(b); return p; }
        static void Heading(Panel panel, string title) { panel.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) }); }
        TextBox Field(Panel panel, string caption, object source, string property, bool live = false)
        {
            panel.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 7, 0, 3) });
            var box = new TextBox { Padding = new Thickness(6), MinWidth = 120 };
            box.SetBinding(TextBox.TextProperty, new Binding(property) { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true, NotifyOnValidationError = true });
            if (live) box.TextChanged += (s, e) => RefreshPreview();
            panel.Children.Add(box); return box;
        }
        CheckBox Check(Panel panel, string caption, object source, string property, bool live = false)
        {
            var box = new CheckBox { Content = caption, Margin = new Thickness(0, 10, 0, 5) };
            box.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Source = source, Mode = BindingMode.TwoWay });
            if (live) { box.Checked += (s, e) => RefreshPreview(); box.Unchecked += (s, e) => RefreshPreview(); }
            panel.Children.Add(box); return box;
        }
        void PathField(Panel panel, string caption, object source, string property, string filter)
        {
            var box = Field(panel, caption, source, property);
            panel.Children.Add(Button("Browse…", () => { var path = Browse(filter); if (path != null) box.Text = path; }));
        }
        static string Browse(string filter)
        {
            var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
        void Error(string title, Exception ex) { log.Write(title + ": " + ex); status.Text = title + ": " + ex.Message; MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
        void Save()
        {
            if (smoke) throw new InvalidOperationException("Smoke-test mode never saves configuration.");
            if (loadFailed) throw new InvalidOperationException("The original configuration could not be loaded. Restore a valid configuration/backup and reopen the configurator; the unreadable file will not be overwritten.");
            if (busy) throw new InvalidOperationException("Finish the active test before saving.");
            if (HasValidationError(tabs)) throw new InvalidOperationException("Correct the highlighted numeric or invalid fields first.");
            try { if (new BrushConverter().ConvertFromString(config.Appearance.BackgroundColor) == null) throw new FormatException(); }
            catch { throw new InvalidOperationException("Enter a valid background color, such as #14233B, in Appearance."); }
            for (int i = 0; i < config.Games.Count; i++) config.Games[i].SortOrder = i;
            var errors = store.Validate(config, true);
            if (errors.Count > 0) { MessageBox.Show(string.Join("\n", errors), "Correct these settings before saving"); return; }
            store.Save(config); savedSnapshot = Fingerprint(); RefreshGames(); status.Text = "Saved safely to " + store.ConfigurationPath;
        }
        static bool HasValidationError(DependencyObject node)
        {
            if (Validation.GetHasError(node)) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (HasValidationError(VisualTreeHelper.GetChild(node, i))) return true;
            return false;
        }
    }
}
