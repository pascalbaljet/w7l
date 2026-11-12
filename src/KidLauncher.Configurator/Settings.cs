using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KidLauncher.Core;

namespace KidLauncher.Configurator
{
    public sealed partial class MainWindow
    {
        UIElement BuildAppearance()
        {
            var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) }); root.ColumnDefinitions.Add(new ColumnDefinition());
            var p = Panel(); var a = config.Appearance;
            Heading(p, "Launcher appearance");
            Check(p, "All games on one screen", a, "AllGamesOnOneScreen", true);
            p.Children.Add(Note("The launcher picks the columns that give the largest cards, for up to " + TileGrid.MaxOnOneScreen + " games. Clear this to use pages of columns × rows instead."));
            Field(p, "Tile columns (1–12)", a, "Columns", true); Field(p, "Tile rows (1–12)", a, "Rows", true);
            var capacity = Note("With pages: tiles per page = columns × rows, up to 12. The default is 4 × 3."); p.Children.Add(capacity);
            Field(p, "Spacing (pixels at 1920 × 1080)", a, "Spacing", true);
            Field(p, "Background color (#RRGGBB)", a, "BackgroundColor", true);
            var background = Field(p, "Background image (optional)", a, "BackgroundImage", true);
            p.Children.Add(Buttons(Button("Browse image…", () => { var path = Browse("Images|*.png;*.jpg;*.jpeg"); if (path != null) background.Text = path; }), Button("Clear", () => background.Text = "")));
            Field(p, "Tile corner radius", a, "CornerRadius", true); Check(p, "Show game titles", a, "ShowTitles", true);
            Field(p, "Hide cursor after inactivity (seconds)", a, "CursorHideSeconds", true);
            Check(p, "Gentle surprises while the launcher rests", a, "IdleAnimations");
            p.Children.Add(Note("Stars twinkle and the owl, fireflies, moon, or a shooting star do something small every few seconds. Windows reduced motion always turns this off."));
            Check(p, "Shuffle the games each time the launcher starts", a, "ShuffleGames");
            p.Children.Add(Note("The order stays the same until the launcher restarts, so a game is still in its place after F6. Clear this to use the order from the Games tab. The preview shows that order."));
            root.Children.Add(Scroll(p));
            var right = new DockPanel { Margin = new Thickness(12) }; Grid.SetColumn(right, 1); root.Children.Add(right);
            var title = Note("Live 1920 × 1080 layout preview. Only visible games appear. Images fill each tile without distortion."); DockPanel.SetDock(title, Dock.Top); right.Children.Add(title);
            pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0, 15, 0) };
            var navigation = Buttons(Button("◀ Previous", () => { previewPage--; RefreshPreview(); }), Button("Next ▶", () => { previewPage++; RefreshPreview(); })); navigation.Children.Insert(1, pageLabel);
            DockPanel.SetDock(navigation, Dock.Bottom); right.Children.Add(navigation);
            preview = new Canvas { Width = 1920, Height = 1080, ClipToBounds = true };
            right.Children.Add(new Viewbox { Child = preview, Stretch = Stretch.Uniform });
            RefreshPreview(); return root;
        }
        void RefreshPreview()
        {
            if (preview == null) return;
            var a = config.Appearance; preview.Children.Clear();
            try { preview.Background = (Brush)new BrushConverter().ConvertFromString(a.BackgroundColor); } catch { preview.Background = Brushes.MidnightBlue; }
            System.Windows.Media.Imaging.BitmapSource bg = null;
            try { bg = Artwork.Image(store.ResolvePath(a.BackgroundImage)); } catch { /* Partial paths are normal while typing. */ }
            if (bg != null) preview.Children.Add(new Image { Source = bg, Width = 1920, Height = 1080, Stretch = Stretch.UniformToFill });
            var heading = new TextBlock { Text = config.Language == "en" ? "Let's play!" : "Spelen maar!", Foreground = Brushes.White, FontSize = 62, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(heading, 60); Canvas.SetTop(heading, 28); preview.Children.Add(heading);
            int columns = Math.Max(1, Math.Min(12, a.Columns)), rows = Math.Max(1, Math.Min(12, a.Rows));
            int capacity = a.AllGamesOnOneScreen ? TileGrid.MaxOnOneScreen : columns * rows;
            var visible = config.Games.Where(g => g.Visible).ToList(); int pages = Math.Max(1, (visible.Count + capacity - 1) / capacity);
            previewPage = Math.Max(0, Math.Min(pages - 1, previewPage));
            double gap = Math.Max(0, Math.Min(100, a.Spacing)), width, height;
            int shown = Math.Min(capacity, visible.Count - previewPage * capacity);
            if (a.AllGamesOnOneScreen && shown > 0)
            {
                double ratio = TileGrid.CardRatio(a.ShowTitles);
                columns = TileGrid.FitColumns(shown, 1800, 800, ratio); rows = (shown + columns - 1) / columns;
                width = Math.Max(20, Math.Min((1800 - gap * (columns - 1)) / columns, ((800 - gap * (rows - 1)) / rows) / ratio)); height = width * ratio;
            }
            else { width = Math.Max(20, (1800 - gap * (columns - 1)) / columns); height = Math.Max(20, (800 - gap * (rows - 1)) / rows); }
            foreach (UIElement child in ((Panel)pageLabel.Parent).Children)
                if (child != pageLabel) child.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            pageLabel.Text = pages == 1 && a.AllGamesOnOneScreen ? "All " + visible.Count + " games on one screen" : "Page " + (previewPage + 1) + " of " + pages + " · " + capacity + " tiles per page";
            Color[] colors = { Color.FromRgb(31, 136, 166), Color.FromRgb(155, 86, 165), Color.FromRgb(214, 117, 45), Color.FromRgb(62, 143, 95) };
            var page = visible.Skip(previewPage * capacity).Take(capacity).ToList();
            for (int i = 0; i < page.Count; i++)
            {
                var g = page[i]; var tile = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(Math.Max(0, a.CornerRadius)), Background = new SolidColorBrush(colors[i % colors.Length]), ClipToBounds = true };
                var grid = new Grid(); var art = Artwork.Load(store, g);
                if (art != null) grid.Children.Add(new Image { Source = art, Stretch = string.IsNullOrWhiteSpace(g.ArtworkPath) ? Stretch.Uniform : Stretch.UniformToFill, Margin = new Thickness(string.IsNullOrWhiteSpace(g.ArtworkPath) ? 35 : 0) });
                else grid.Children.Add(new TextBlock { Text = "★", FontSize = Math.Min(height * .45, 130), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                if (a.ShowTitles) grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(210, 13, 27, 46)), VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(10, 8, 10, 8), Child = new TextBlock { Text = g.Title, FontSize = Math.Max(12, Math.Min(22, width * 0.068)), Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center } });
                tile.Child = grid; Canvas.SetLeft(tile, 60 + (i % columns) * (width + gap)); Canvas.SetTop(tile, 145 + (i / columns) * (height + gap)); preview.Children.Add(tile);
            }
            if (pages > 1)
            {
            var pagesText = new TextBlock { Text = "◀     " + (previewPage + 1) + " / " + pages + "     ▶", Foreground = Brushes.White, FontSize = 42 }; Canvas.SetLeft(pagesText, 800); Canvas.SetTop(pagesText, 985); preview.Children.Add(pagesText);
            }
        }

        UIElement BuildControls()
        {
            var p = Panel(); Heading(p, "Exit-game key");
            p.Children.Add(Note("The launcher uses one global key. F6 is recommended. F12 is reserved by debuggers; Windows/system and typing keys are unsuitable. Test the selected key with each real game."));
            var keyLabel = new TextBlock { FontSize = 22, Text = KeyInterop.KeyFromVirtualKey(config.Controls.ExitVirtualKey).ToString() }; p.Children.Add(keyLabel);
            var capture = new TextBox { IsReadOnly = true, Text = "Click here, then press the desired key", Padding = new Thickness(12), MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(capture, "Exit-game key");
            capture.PreviewKeyDown += (s, e) =>
            {
                e.Handled = true; var key = e.Key == Key.System ? e.SystemKey : e.Key;
                int vk = KeyInterop.VirtualKeyFromKey(key);
                if (Keyboard.Modifiers != ModifierKeys.None || vk < 1 || vk > 254 || key == Key.F12 || key == Key.LWin || key == Key.RWin || key == Key.Escape || key == Key.Return || key == Key.Tab || key == Key.LeftShift || key == Key.RightShift || key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt)
                { capture.Text = "Choose an unmodified key such as F6, Pause or Scroll Lock."; return; }
                try
                {
                    using (var hotkey = new WindowsHotkey(new WindowInteropHelper(this).Handle, vk, () => { }))
                    {
                        hotkey.Enabled = true;
                        if (!hotkey.Enabled) throw new InvalidOperationException("Windows did not enable the key.");
                        config.Controls.ExitVirtualKey = vk; keyLabel.Text = key.ToString(); capture.Text = "Checked: " + hotkey.Strategy;
                    }
                }
                catch (Exception ex) { Error("Hotkey unavailable", ex); }
            };
            p.Children.Add(capture);
            p.Children.Add(Note("Test games always use F6, regardless of the saved launcher key. A key registration check is not proof for elevated or exclusive-input games. If an elevated game cannot be controlled, run the launcher at the same privilege level only for trusted games; do not disable UAC."));
            Heading(p, "Windows key");
            Check(p, "Block the Windows keys while the launcher runs", config.Controls, "BlockWindowsKey");
            p.Children.Add(Note("Stops a child from opening the Start menu or minimizing a game, in the launcher and during games. The keys work again while the grown-up menu or the configurator is open, and after Exit launcher to Windows. Ctrl+Alt+Del always works."));
            Heading(p, "Graceful / forced exit");
            Check(p, "Close games instantly with the exit key", config.Controls, "InstantExit");
            p.Children.Add(Note("When on, one press force-closes the tracked game processes at once, without a quit question. Unsaved game progress is lost. Games with automatic force-close turned off still wait until they close themselves. When off, each game uses its own timeout in Games → Process tracking and exit: a press requests close, waits for that timeout, then force-closes tracked processes if enabled."));
            Heading(p, "Adult access PIN");
            p.Children.Add(Note("The launcher has a hidden adult entry route (Ctrl+Alt+S). Set a PIN here. A salted hash is stored; the PIN is never stored in plaintext."));
            p.Children.Add(new TextBlock { Text = "Current PIN (required when one is already set)" }); var old = new PasswordBox { MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220, Padding = new Thickness(6) }; p.Children.Add(old);
            p.Children.Add(new TextBlock { Text = "New PIN", Margin = new Thickness(0, 10, 0, 0) }); var next = new PasswordBox { MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220, Padding = new Thickness(6) }; p.Children.Add(next);
            p.Children.Add(new TextBlock { Text = "Repeat new PIN", Margin = new Thickness(0, 10, 0, 0) }); var confirm = new PasswordBox { MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220, Padding = new Thickness(6) }; p.Children.Add(confirm);
            AutomationProperties.SetName(old, "Current PIN");
            AutomationProperties.SetName(next, "New PIN");
            AutomationProperties.SetName(confirm, "Repeat new PIN");
            p.Children.Add(Button("Change PIN", () =>
            {
                EnsureIdle();
                if (!string.IsNullOrEmpty(config.Controls.PinHash) && !PinSecurity.VerifyPin(config.Controls, old.Password)) throw new InvalidOperationException("The current PIN is incorrect.");
                if (next.Password.Length < 4 || next.Password.Length > 12 || !next.Password.All(char.IsDigit)) throw new InvalidOperationException("Choose a PIN of 4–12 digits.");
                if (next.Password != confirm.Password) throw new InvalidOperationException("The two new PINs do not match.");
                PinSecurity.SetPin(config.Controls, next.Password); old.Clear(); next.Clear(); confirm.Clear(); status.Text = "PIN changed in memory. Save configuration to keep it.";
            }));
            return Scroll(p);
        }

        UIElement BuildGeneral()
        {
            var p = Panel();
            Heading(p, "Children's launcher language");
            var language = new ComboBox { MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
            language.Items.Add("Nederlands"); language.Items.Add("English");
            language.SelectedIndex = config.Language == "en" ? 1 : 0;
            AutomationProperties.SetName(language, "Launcher language");
            language.SelectionChanged += (s, e) => { config.Language = language.SelectedIndex == 1 ? "en" : "nl"; RefreshPreview(); };
            p.Children.Add(language);
            p.Children.Add(Note("Dutch is the default, including for existing configurations without a language setting. Save configuration and reopen the launcher to apply. The configurator stays in English."));
            Heading(p, "Start with Windows");
            var startup = new CheckBox { Content = "Launch KidLauncher when this Windows user signs in", IsChecked = !smoke && WindowsStartupService.IsEnabled(), Margin = new Thickness(0, 12, 0, 12) }; p.Children.Add(startup);
            p.Children.Add(Button("Apply startup shortcut", () => { EnsureIdle(); WindowsStartupService.SetEnabled(startup.IsChecked == true, Path.Combine(store.ApplicationDirectory, "KidLauncher.exe")); status.Text = "Per-user startup shortcut updated."; }));
            p.Children.Add(Note("This is a normal shortcut in the current user's Startup folder. It does not replace Explorer or change other users. Apply is immediate and separate from Save configuration. Administrator games may still show UAC prompts."));
            Heading(p, "Portable configuration");
            p.Children.Add(Note("Application: " + store.ApplicationDirectory + "\nConfiguration: " + store.ConfigurationPath + "\nWritable data: " + store.DataDirectory + "\n\nRelative paths are resolved against the application folder, never the working directory. Save creates a backup before replacing a valid configuration."));
            Heading(p, "Recommended setup");
            p.Children.Add(Note("Use a dedicated, standard local Windows account for games. Keep the portable launcher in a folder that account can write, not Program Files. Configure already-installed, trusted games here. The configurator never alters compatibility settings, disables UAC or creates additional virtual drives. Tests may run while the launcher waits in adult settings; an exclusive session lock prevents concurrent display/disc sessions."));
            return Scroll(p);
        }

        UIElement BuildDiagnostics()
        {
            var p = Panel(); Heading(p, "Diagnostics and recovery");
            p.Children.Add(Note("Configuration: " + store.ConfigurationPath + "\nData / recovery: " + store.DataDirectory + "\nLogs: " + Path.Combine(store.DataDirectory, "Logs") + "\nAssets: " + Path.Combine(store.ApplicationDirectory, "Assets")));
            p.Children.Add(Buttons(Button("Open data folder", () => Process.Start("explorer.exe", "\"" + store.DataDirectory + "\"")),
                Button("Open application folder", () => Process.Start("explorer.exe", "\"" + store.ApplicationDirectory + "\"")),
                Button("Validate files and settings", () => { var errors = store.Validate(config, true); MessageBox.Show(this, errors.Count == 0 ? "Configuration and referenced files passed validation." : string.Join("\n", errors), "Validation"); }),
                Button("Retry interrupted-session recovery", Recover)));
            p.Children.Add(Button("Confirm games closed and recover…", ConfirmRecovery));
            p.Children.Add(Note("Use confirmation recovery only if the launcher stopped during process creation and cannot identify the game. Close every game manually first. Known live game processes still block recovery."));
            p.Children.Add(Note("• Missing executable / artwork: browse to the installed file, then save.\n• No write access: move the portable app outside Program Files. Configuration may fall back to the user's LocalAppData; managed artwork needs a writable Assets folder.\n• Game will not close: check observed process names and enable automatic force exit for that game. Never select unrelated processes.\n• Elevated game: a standard-integrity launcher cannot reliably close or hook a higher-integrity game. Use matching privileges for trusted games; keep UAC enabled.\n• Hotkey conflicts: choose a different key and test it. Exclusive-input games may prevent reliable hotkey reception.\n• Wrong disc: verify the actual VCD unit and Windows letter; these are not interchangeable. Do not guess or change unrelated units.\n• Recovery failed: do not delete recovery state. Close the game, retry recovery, and consult the log. If necessary use Windows Screen Resolution to restore a supported mode, then verify the managed disc in Virtual CloneDrive.\n• Sudden power loss, driver failure, secure desktop and shutdown can prevent automatic cleanup. Real Windows 7 hardware testing remains necessary."));
            return Scroll(p);
        }
    }
}
