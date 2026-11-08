using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KidLauncher.Core;

namespace KidLauncher.Configurator
{
    public sealed partial class MainWindow
    {
        Point dragStart;
        Game dragged;

        UIElement BuildGames()
        {
            var dock = new DockPanel { Margin = new Thickness(12) };
            var actions = Buttons(Button("＋ Add", () => { EnsureIdle(); var g = new Game(); config.Games.Add(g); RefreshGames(g); }),
                Button("Import from Desktop…", ImportDesktop), Button("Duplicate", Duplicate), Button("Remove", Remove),
                Button("↑ Up", () => Move(-1)), Button("↓ Down", () => Move(1)),
                Button("Hide / show", () => { EnsureIdle(); if (selected != null) { selected.Visible = !selected.Visible; BuildEditor(); } }));
            DockPanel.SetDock(actions, Dock.Top); dock.Children.Add(actions);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(games); Grid.SetColumn(editor, 1); grid.Children.Add(editor); dock.Children.Add(grid);
            games.AllowDrop = true;
            games.PreviewMouseLeftButtonDown += (s, e) => { dragStart = e.GetPosition(games); dragged = ItemAt(e.OriginalSource as DependencyObject); };
            games.PreviewMouseMove += (s, e) =>
            {
                if (busy || dragged == null || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(games) - dragStart).Length < 8) return;
                DragDrop.DoDragDrop(games, dragged, DragDropEffects.Move); dragged = null;
            };
            games.Drop += (s, e) =>
            {
                if (busy) return;
                var source = e.Data.GetData(typeof(Game)) as Game;
                var target = ItemAt(e.OriginalSource as DependencyObject);
                if (source == null || target == null || source == target) return;
                int index = config.Games.IndexOf(target); config.Games.Remove(source); config.Games.Insert(index, source); RefreshGames(source);
            };
            return dock;
        }
        Game ItemAt(DependencyObject element)
        {
            while (element != null && !(element is ListBoxItem)) element = VisualTreeHelper.GetParent(element);
            return (element as ListBoxItem)?.Content as Game;
        }
        void EnsureIdle() { if (busy) throw new InvalidOperationException("Finish the active test first."); }
        void RefreshGames(Game focus = null)
        {
            var keep = focus ?? selected;
            games.ItemsSource = null; games.ItemsSource = config.Games;
            games.SelectedItem = keep != null && config.Games.Contains(keep) ? keep : config.Games.FirstOrDefault();
            RefreshPreview();
        }
        void Move(int delta)
        {
            EnsureIdle(); if (selected == null) return;
            int i = config.Games.IndexOf(selected), next = i + delta;
            if (next < 0 || next >= config.Games.Count) return;
            var g = selected; config.Games.RemoveAt(i); config.Games.Insert(next, g); RefreshGames(g);
        }
        void Remove()
        {
            EnsureIdle(); if (selected == null) return;
            if (MessageBox.Show(this, "Remove ‘" + selected.Title + "’ from the launcher? Installed game files will not be deleted.", "Remove game", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            config.Games.Remove(selected); RefreshGames();
        }
        static Game Copy(Game g)
        {
            return new Game { Title = g.Title + " (copy)", ExecutablePath = g.ExecutablePath, WorkingDirectory = g.WorkingDirectory, Arguments = g.Arguments,
                ArtworkPath = g.ArtworkPath, Visible = g.Visible, RunAsAdministrator = g.RunAsAdministrator, Resolution = g.Resolution,
                ProcessNames = new List<string>(g.ProcessNames), StartupGraceMs = g.StartupGraceMs, GracefulExitTimeoutMs = g.GracefulExitTimeoutMs, AutomaticForceExit = g.AutomaticForceExit,
                Disc = new DiscSettings { Enabled = g.Disc.Enabled, ImagePath = g.Disc.ImagePath, VirtualDriveIndex = g.Disc.VirtualDriveIndex, ExpectedDriveLetter = g.Disc.ExpectedDriveLetter,
                    ExpectedVolumeLabel = g.Disc.ExpectedVolumeLabel, ReadinessFile = g.Disc.ReadinessFile, MountTimeoutMs = g.Disc.MountTimeoutMs, UnmountAfterExit = g.Disc.UnmountAfterExit } };
        }
        void Duplicate() { EnsureIdle(); if (selected == null) return; var g = Copy(selected); config.Games.Insert(config.Games.IndexOf(selected) + 1, g); RefreshGames(g); }

        void BuildEditor()
        {
            if (selected == null) { editor.Content = Note("Add a game or choose desktop shortcuts to get started."); return; }
            var g = selected;
            var panel = Panel(); panel.Margin = new Thickness(6);
            panel.Children.Add(Buttons(Button("▶ Test game (F6 exits)", () => TestGame(g)), Button("■ Stop test", StopTest), Button("Choose observed processes…", () => SelectProcesses(g))));
            panel.Children.Add(Note("Tests use the shared launcher session, including display recovery and disc cleanup. Save configuration when you are satisfied."));
            Field(panel, "Game title", g, "Title"); Check(panel, "Visible in the child launcher", g, "Visible");
            Field(panel, "Executable path", g, "ExecutablePath");
            panel.Children.Add(Button("Browse executable or shortcut…", () =>
            {
                EnsureIdle(); string path = Browse("Programs and shortcuts|*.exe;*.lnk|All files|*.*"); if (path == null) return;
                if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) ApplyShortcut(g, WindowsShortcutService.Read(path));
                else { g.ExecutablePath = path; g.WorkingDirectory = Path.GetDirectoryName(path); if (g.Title == "New game") g.Title = Path.GetFileNameWithoutExtension(path); }
                BuildEditor(); RefreshGames(g);
            }));
            Field(panel, "Arguments (passed directly, not through a command shell)", g, "Arguments");
            Field(panel, "Working directory", g, "WorkingDirectory");
            panel.Children.Add(Button("Choose working directory…", () => { string path = FolderPicker.Select(this, store.ResolvePath(g.WorkingDirectory)); if (path != null) { g.WorkingDirectory = path; BuildEditor(); } }));
            Check(panel, "Run this trusted game as administrator (Windows may prompt)", g, "RunAsAdministrator");
            Heading(panel, "Artwork");
            panel.Children.Add(new Image { Source = Artwork.Load(store, g), Height = 135, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform });
            panel.Children.Add(Buttons(Button("Choose and crop image…", () => EditArtwork(g)), Button("Use executable icon", () => { g.ArtworkPath = ""; BuildEditor(); })));
            Field(panel, "Artwork path (managed PNG or existing image)", g, "ArtworkPath");
            Heading(panel, "Display mode");
            var modes = new ComboBox { Margin = new Thickness(0, 5, 0, 5), MinHeight = 30 };
            modes.PreviewMouseWheel += (sender, args) =>
            {
                if (modes.IsDropDownOpen) return;
                args.Handled = true;
                panel.RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta)
                    { RoutedEvent = Mouse.MouseWheelEvent, Source = modes });
            };
            modes.Items.Add("Use current resolution");
            try
            {
                foreach (var mode in display.Enumerate().GroupBy(m => new { m.Width, m.Height, m.Frequency, m.BitsPerPixel }).Select(group => group.First()))
                    modes.Items.Add(mode);
            }
            catch (Exception ex) { panel.Children.Add(Note("Display enumeration failed: " + ex.Message)); }
            modes.SelectedIndex = 0;
            if (g.Resolution != null)
            {
                var match = modes.Items.OfType<DisplayMode>().FirstOrDefault(m => m.Width == g.Resolution.Width && m.Height == g.Resolution.Height && m.Frequency == g.Resolution.Frequency && m.BitsPerPixel == g.Resolution.BitsPerPixel);
                if (match == null) { modes.Items.Add(g.Resolution); match = g.Resolution; }
                modes.SelectedItem = match;
            }
            modes.SelectionChanged += (s, e) => g.Resolution = modes.SelectedItem as DisplayMode;
            panel.Children.Add(modes); panel.Children.Add(Button("Test selected resolution (15 seconds, always restore)", () => TestResolution(g.Resolution)));
            Heading(panel, "Process tracking and exit");
            panel.Children.Add(Note("Only verified processes from this launch are tracked. Names help identify bootstrap handoffs, not arbitrary existing processes."));
            var names = new TextBox { Text = string.Join(", ", g.ProcessNames), Padding = new Thickness(6) };
            panel.Children.Add(Note("Process names, separated by commas (without .exe)")); panel.Children.Add(names);
            names.TextChanged += (s, e) => g.ProcessNames = names.Text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(n => Path.GetFileNameWithoutExtension(n.Trim())).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Field(panel, "Startup observation grace (milliseconds)", g, "StartupGraceMs");
            Field(panel, "Graceful close timeout (milliseconds)", g, "GracefulExitTimeoutMs");
            Check(panel, "Automatically force-close tracked processes after the timeout", g, "AutomaticForceExit");
            Heading(panel, "CD image"); BuildDiscFields(panel, g.Disc);
            editor.Content = Scroll(panel);
        }

        void ApplyShortcut(Game game, DesktopShortcut link)
        {
            game.Title = link.Title; game.ExecutablePath = link.TargetPath; game.Arguments = link.Arguments ?? "";
            game.WorkingDirectory = string.IsNullOrWhiteSpace(link.WorkingDirectory) ? Path.GetDirectoryName(link.TargetPath) : link.WorkingDirectory;
            game.RunAsAdministrator = link.RunAsAdministrator;
            string artwork = Artwork.SaveIcon(store, link.IconPath, link.TargetPath);
            if (artwork != null) game.ArtworkPath = artwork;
        }
        void ImportDesktop()
        {
            EnsureIdle();
            var links = WindowsShortcutService.EnumerateDesktop();
            var dialog = new Window { Title = "Choose desktop shortcuts to import", Owner = this, Width = 820, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var root = new DockPanel { Margin = new Thickness(16) }; dialog.Content = root;
            var note = Note("Select only the game shortcuts you want (Ctrl/Shift for several). Review the target below: some shortcuts start a separate game selector."); DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
            var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12), MinHeight = 110 }; DockPanel.SetDock(details, Dock.Bottom);
            var list = new ListBox { ItemsSource = links, SelectionMode = SelectionMode.Extended, DisplayMemberPath = "Title" };
            var icon = new Image { Height = 64, Width = 64, Margin = new Thickness(8) };
            var review = new StackPanel(); review.Children.Add(icon); review.Children.Add(details); DockPanel.SetDock(review, Dock.Bottom);
            var import = Button("Import selected shortcuts", () =>
            {
                if (list.SelectedItems.Count == 0) return;
                foreach (DesktopShortcut link in list.SelectedItems) { var game = new Game(); ApplyShortcut(game, link); config.Games.Add(game); }
                RefreshGames(); dialog.DialogResult = true;
            });
            DockPanel.SetDock(import, Dock.Bottom); root.Children.Add(import); root.Children.Add(review); root.Children.Add(list);
            list.SelectionChanged += (s, e) =>
            {
                var link = list.SelectedItem as DesktopShortcut;
                if (link == null) return;
                details.Text = link.Title + "\nTarget: " + link.TargetPath + "\nArguments: " + link.Arguments + "\nWorking directory: " + link.WorkingDirectory + "\nAdministrator: " + (link.RunAsAdministrator ? "Yes" : "No") + "\n" + link.ShortcutPath;
                icon.Source = Artwork.Icon(link.IconPath) ?? Artwork.Icon(link.TargetPath);
            };
            if (links.Count == 0) details.Text = "No readable desktop shortcuts were found on the current user or public desktop.";
            dialog.ShowDialog();
        }
        void EditArtwork(Game game)
        {
            EnsureIdle(); string path = Browse("Artwork|*.png;*.jpg;*.jpeg"); if (path == null) return;
            var dialog = new ArtworkEditor(path, TileAspect()) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            string directory = Path.Combine(store.ApplicationDirectory, "Assets"); Directory.CreateDirectory(directory);
            string relative = Path.Combine("Assets", Guid.NewGuid().ToString("N") + ".png");
            dialog.Save(Path.Combine(store.ApplicationDirectory, relative)); game.ArtworkPath = relative; BuildEditor(); RefreshPreview();
        }
        double TileAspect()
        {
            int columns = Math.Max(1, Math.Min(12, config.Appearance.Columns)), rows = Math.Max(1, Math.Min(12, config.Appearance.Rows));
            double gap = Math.Max(0, Math.Min(100, config.Appearance.Spacing));
            return Math.Max(20, (1800 - gap * (columns - 1)) / columns) / Math.Max(20, (800 - gap * (rows - 1)) / rows);
        }
    }
}
