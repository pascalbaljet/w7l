using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KidLauncher.Core;

namespace KidLauncher
{
    public partial class MainWindow : Window
    {
        private readonly ConfigurationStore store;
        private readonly ILog log;
        private readonly DispatcherTimer idleTimer;
        private LauncherConfiguration config;
        private GameSession session;
        private WindowsHotkey hotkey;
        private List<Game> games = new List<Game>();
        private int page;
        private int pageSize = 12;
        private bool busy = true;
        private bool recovered;
        private bool authorizedExit;
        private bool adultOpen;
        private bool exitRequested;
        private bool started;
        private readonly bool smokeTest;
        private DateTime lastMove = DateTime.UtcNow;
        private DateTime lastWheel = DateTime.MinValue;
        private string diagnostic = "No errors reported.";
        private readonly string[] colors = { "#2D747D", "#97604D", "#64639C", "#466F9A", "#6C7941", "#96617B" };
        public bool OperationActive { get { return busy || (session != null && session.IsRunning); } }

        public MainWindow(ConfigurationStore store, ILog log, bool smokeTest = false)
        {
            InitializeComponent();
            this.store = store;
            this.log = log;
            this.smokeTest = smokeTest;
            SourceInitialized += (s, e) => { Topmost = true; };
            Loaded += async (s, e) =>
            {
                if (started) return;
                started = true;
                if (smokeTest)
                {
                    config = store.Load(); recovered = true; busy = false;
                    WindowState = WindowState.Normal; Width = 1920; Height = 1080;
                    RenderPage(); Overlay.Visibility = Visibility.Collapsed;
                }
                else { EnterFullscreen(); await InitializeAsync(); }
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            Closing += OnClosing;
            Closed += (s, e) => Application.Current.Shutdown();
            PreviewKeyDown += OnKeyDown;
            PreviewMouseMove += (s, e) => { lastMove = DateTime.UtcNow; Mouse.OverrideCursor = null; };
            PreviewMouseWheel += (s, e) =>
            {
                if (DateTime.UtcNow - lastWheel < TimeSpan.FromMilliseconds(350)) return;
                lastWheel = DateTime.UtcNow;
                ChangePage(e.Delta < 0 ? 1 : -1);
                e.Handled = true;
            };
            idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            idleTimer.Tick += (s, e) =>
            {
                if (IsActive && !adultOpen && config != null && config.Appearance != null && config.Appearance.CursorHideSeconds > 0 &&
                    DateTime.UtcNow - lastMove >= TimeSpan.FromSeconds(config.Appearance.CursorHideSeconds))
                    Mouse.OverrideCursor = Cursors.None;
            };
            idleTimer.Start();
        }

        private async Task InitializeAsync()
        {
            busy = true;
            ShowOverlay(L("Getting ready…", "Even geduld…"), L("Your adventures will be here soon.", "Je avonturen komen eraan."), false);
            try
            {
                // Recovery deliberately precedes validation and all child interaction.
                // Even a broken configuration must not bypass a saved display session.
                Exception loadFailure = null;
                try { config = store.Load(); }
                catch (Exception ex)
                {
                    loadFailure = ex;
                    config = new LauncherConfiguration();
                    log.Write("Configuration could not load; attempting recovery with detected drive utility: " + ex);
                }
                ApplyLanguage();
                ShowOverlay(L("Getting ready…", "Even geduld…"), L("Your adventures will be here soon.", "Je avonturen komen eraan."), false);
                CreateSession();
                await session.RecoverAsync();
                recovered = true;
                if (loadFailure != null) throw loadFailure;
                var errors = store.Validate(config);
                if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
                CreateSession();
                RegisterHotkey();
                RenderPage();
                Overlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) { Error(L("The play room needs a little help.", "De speelkamer heeft wat hulp nodig."), ex); }
            finally { busy = false; BringBack(); }
        }

        private string L(string english, string dutch) => config?.Language == "en" ? english : dutch;

        private void ApplyLanguage()
        {
            Title = L("KidLauncher · Let's play", "KidLauncher · Spelen maar");
            PlayHeading.Text = L("LET'S PLAY", "SPELEN MAAR");
            PlaySubtitle.Text = L("Pick an adventure", "Kies een avontuur");
            ReturnButton.Content = L("Back to my games", "Terug naar mijn spelletjes");
            AutomationProperties.SetName(Previous, L("Previous page", "Vorige pagina"));
            AutomationProperties.SetName(Next, L("Next page", "Volgende pagina"));
        }

        private void CreateSession()
        {
            var vcd = string.IsNullOrWhiteSpace(config.VcdMountPath)
                ? VirtualCloneDriveService.DetectExecutable() : store.ResolvePath(config.VcdMountPath);
            session = new GameSession(store, config, new WindowsDisplayService(), new WindowsProcessService(),
                new VirtualCloneDriveService(vcd, log), log);
            session.GameStarting += () => Dispatcher.Invoke(new Action(() =>
            {
                Mouse.OverrideCursor = null;
                Topmost = false;
                Hide();
            }));
            session.StatusChanged += message => log.Write("Session: " + message);
        }

        private void RegisterHotkey()
        {
            if (hotkey != null) { hotkey.Dispose(); hotkey = null; }
            hotkey = new WindowsHotkey(new WindowInteropHelper(this).Handle, config.Controls.ExitVirtualKey, RequestGameExit);
            hotkey.Enabled = true;
            log.Write("Exit-game hotkey: " + hotkey.Strategy + "; enabled=" + hotkey.Enabled);
            if (!hotkey.Enabled) throw new InvalidOperationException("Exit-game hotkey unavailable. Select another key in the configurator. " + hotkey.Strategy);
            hotkey.Enabled = false;
        }

        private void RenderPage()
        {
            ApplyLanguage();
            var a = config.Appearance;
            Tiles.Columns = Math.Max(1, Math.Min(12, a.Columns));
            Tiles.Rows = Math.Max(1, Math.Min(12 / Tiles.Columns, a.Rows));
            pageSize = Tiles.Columns * Tiles.Rows;
            games = config.Games.Where(g => g.Visible).OrderBy(g => g.SortOrder).ThenBy(g => g.Title).ToList();
            var count = Math.Max(1, (games.Count + pageSize - 1) / pageSize);
            page = Math.Max(0, Math.Min(page, count - 1));
            var background = (Brush)new BrushConverter().ConvertFromString(a.BackgroundColor);
            Backdrop.Background = background;
            var wallpaper = LoadArtwork(a.BackgroundImage, null);
            if (wallpaper != null) Backdrop.Background = new ImageBrush(wallpaper) { Stretch = Stretch.UniformToFill };
            Tiles.Children.Clear();
            foreach (var game in games.Skip(page * pageSize).Take(pageSize)) Tiles.Children.Add(CreateTile(game));
            Previous.IsEnabled = page > 0;
            Next.IsEnabled = page < count - 1;
            PageLabel.Text = games.Count == 0 ? L("Adventures are on their way", "Je avonturen komen eraan") :
                L("Page ", "Pagina ") + (page + 1) + L(" of ", " van ") + count;
            PageDots.Children.Clear();
            // A bounded dot strip stays legible even with very large game collections.
            for (int i = Math.Max(0, page - 4); i < Math.Min(count, page + 5); i++)
                PageDots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(4, 0, 4, 0), Fill = i == page ? Brushes.Turquoise : Brushes.SlateGray });
            if (games.Count == 0)
            {
                var empty = new TextBlock { Text = L("★\nReady for new adventures!\nAsk a grown-up to add your games.", "★\nKlaar voor nieuwe avonturen!\nVraag een volwassene om je spelletjes toe te voegen."), FontSize = 28, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Tiles.Children.Add(empty);
            }
        }

        private Button CreateTile(Game game)
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(colors[Math.Abs(games.IndexOf(game) % colors.Length)]);
            var layout = new Grid();
            layout.SizeChanged += (s, e) => layout.Clip = new RectangleGeometry(new Rect(layout.RenderSize),
                Math.Max(0, config.Appearance.CornerRadius), Math.Max(0, config.Appearance.CornerRadius));
            layout.RowDefinitions.Add(new RowDefinition());
            if (config.Appearance.ShowTitles) layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var art = LoadArtwork(game.ArtworkPath, game.ExecutablePath);
            if (art != null)
                layout.Children.Add(new Image { Source = art, Stretch = Stretch.UniformToFill, ClipToBounds = true, Margin = new Thickness(3) });
            else
                layout.Children.Add(new TextBlock { Text = "★", Foreground = Brushes.White, Opacity = 0.8, FontSize = 92, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            if (config.Appearance.ShowTitles)
            {
                var title = new TextBlock { Text = game.Title, FontSize = 25, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 76, Margin = new Thickness(16, 10, 16, 14), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
                Grid.SetRow(title, 1);
                layout.Children.Add(title);
            }
            var button = new Button
            {
                Content = layout, Background = brush, Padding = new Thickness(0),
                Margin = new Thickness(Math.Max(0, config.Appearance.Spacing) / 2.0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
            };
            // Tile template stretches the artwork while the navigation template stays compact.
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Max(0, config.Appearance.CornerRadius)));
            border.SetValue(Border.BackgroundProperty, brush);
            border.SetValue(Border.ClipToBoundsProperty, true);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(presenter);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            button.PreviewMouseLeftButtonDown += (s, e) => button.Opacity = 0.62;
            button.PreviewMouseLeftButtonUp += (s, e) => button.Opacity = 1;
            button.MouseLeave += (s, e) => button.Opacity = 1;
            AutomationProperties.SetName(button, game.Title);
            button.Click += async (s, e) => await LaunchAsync(game);
            return button;
        }

        private async Task LaunchAsync(Game game)
        {
            if (OperationActive || !recovered || Overlay.Visibility == Visibility.Visible || adultOpen) return;
            busy = true;
            PlayRoom.IsEnabled = false;
            ShowOverlay(L("Let's go!", "Daar gaan we!"), L("Opening ", "We starten ") + game.Title + "…", false);
            try
            {
                log.Write("Game selected: " + game.Title);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(180); // Let one loading frame paint even for instant-start games.
                hotkey.Enabled = true;
                await session.RunAsync(game, CancellationToken.None);
                Overlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) { Error(L("That adventure couldn't start.", "Dit avontuur kon niet starten."), ex); }
            finally
            {
                if (hotkey != null) hotkey.Enabled = false;
                busy = false;
                PlayRoom.IsEnabled = true;
                BringBack();
            }
        }

        public async void RequestGameExit()
        {
            if (session == null || !session.IsRunning || exitRequested) return;
            exitRequested = true;
            try { log.Write("Global exit-game key pressed."); await session.RequestExitAsync(); }
            catch (Exception ex) { Error(L("We need a grown-up's help to come back.", "Vraag een volwassene om je terug te helpen."), ex); }
            finally { exitRequested = false; }
        }

        private void BringBack()
        {
            if (session != null && session.IsRunning) return;
            Show();
            EnterFullscreen();
            Topmost = true;
            Activate();
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
            lastMove = DateTime.UtcNow;
            Mouse.OverrideCursor = null;
        }

        private void EnterFullscreen()
        {
            if (smokeTest) return;
            // Maximized windows can stop at the taskbar work area. Use the full
            // primary-screen dimensions in WPF's system-DPI-aware logical units.
            WindowState = WindowState.Normal;
            Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }

        private void DisplaySettingsChanged(object sender, EventArgs args)
        {
            Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) EnterFullscreen(); }));
        }

        private void ShowOverlay(string title, string message, bool canReturn)
        {
            OverlayTitle.Text = title;
            OverlayMessage.Text = message;
            ReturnButton.Visibility = canReturn ? Visibility.Visible : Visibility.Collapsed;
            Overlay.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = null;
        }

        private void Error(string title, Exception ex)
        {
            diagnostic = ex.ToString();
            log.Write("Launcher: " + diagnostic);
            ShowOverlay(title, L("Ask a grown-up for help, or try coming back to your games.", "Vraag een volwassene om hulp, of ga terug naar je spelletjes."), true);
        }

        public void ShowUnexpectedError()
        {
            diagnostic = "An unexpected launcher error occurred. See the log in " + store.DataDirectory;
            ShowOverlay(L("Oops! Let's try again.", "Oeps! We proberen het opnieuw."), L("Ask a grown-up if you need a hand.", "Vraag een volwassene als je hulp nodig hebt."), true);
            if (!OperationActive) BringBack();
        }

        private async void Return_Click(object sender, RoutedEventArgs e)
        {
            if (OperationActive) { RequestGameExit(); return; }
            await InitializeAsync();
        }

        private void ChangePage(int delta)
        {
            if (OperationActive || adultOpen || Overlay.Visibility == Visibility.Visible) return;
            int newPage = Math.Max(0, Math.Min(page + delta, Math.Max(0, (games.Count - 1) / pageSize)));
            if (page == newPage) return;
            page = newPage;
            RenderPage();
        }
        private void Previous_Click(object sender, RoutedEventArgs e) { ChangePage(-1); }
        private void Next_Click(object sender, RoutedEventArgs e) { ChangePage(1); }

        internal void CaptureSmokeStates(string directory)
        {
            if (!smokeTest) throw new InvalidOperationException("Visual capture requires smoke-test mode.");
            Directory.CreateDirectory(directory);
            foreach (string language in new[] { "nl", "en" })
            {
                config.Language = language; page = 0;
                RenderPage(); Overlay.Visibility = Visibility.Collapsed;
                string prefix = language == "nl" ? "launcher-" : "launcher-english-";
                if (PlaySubtitle.Text != (language == "nl" ? "Kies een avontuur" : "Pick an adventure"))
                    throw new InvalidOperationException("Launcher language did not update.");
                Capture(directory, prefix + "default.png");
                Next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (PageLabel.Text != (language == "nl" ? "Pagina 2 van 2" : "Page 2 of 2") || Tiles.Children.Count != 1)
                    throw new InvalidOperationException("Launcher pagination smoke check failed.");
                Capture(directory, prefix + "page-2.png");
                Previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (PageLabel.Text != (language == "nl" ? "Pagina 1 van 2" : "Page 1 of 2") || Tiles.Children.Count != 12)
                    throw new InvalidOperationException("Launcher return-page smoke check failed.");
                ShowOverlay(L("Let's go!", "Daar gaan we!"), L("Opening ", "We starten ") + "Putt-Putt Saves the Zoo…", false);
                Capture(directory, prefix + "loading.png");
                Error(L("That adventure couldn't start.", "Dit avontuur kon niet starten."), new IOException("Visual smoke test error state."));
                Capture(directory, prefix + "error.png");
                config.Games.ForEach(game => game.Visible = false);
                RenderPage(); Overlay.Visibility = Visibility.Collapsed;
                Capture(directory, prefix + "empty.png");
                config.Games.ForEach(game => game.Visible = true);
            }
            File.WriteAllText(Path.Combine(directory, "launcher-checks.txt"), "PASS: Dutch and English headings, pagination, loading, error and empty states rendered. 12 tiles on page 1; 1 on page 2; previous/next Click events update pages.");
            authorizedExit = true;
        }

        private void Capture(string directory, string filename)
        {
            // Windows runners may expose a desktop smaller than 1920×1080.
            // A fresh parent resumes layout after detachment; a detached WPF
            // subtree by itself is layout-suspended and ignores Measure/Arrange.
            var host = new Grid { Width = 1920, Height = 1080 };
            host.SetValue(TextBlock.ForegroundProperty, Foreground);
            Content = null;
            try
            {
                UpdateLayout();
                host.Children.Add(Backdrop);
                host.Measure(new Size(1920, 1080));
                host.Arrange(new Rect(0, 0, 1920, 1080));
                host.UpdateLayout();
                if (!Equals(OverlayTitle.Foreground, Foreground) || !Equals(PlaySubtitle.Foreground, Foreground))
                    throw new InvalidOperationException("Offscreen capture lost the window text color.");
                var image = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
                image.Render(Backdrop);
                var corner = new byte[4];
                image.CopyPixels(new Int32Rect(1919, 1079, 1, 1), corner, 4, 0);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(directory, filename))) encoder.Save(file);
                if (Backdrop.ActualWidth != 1920 || Backdrop.ActualHeight != 1080 || corner[3] != 255)
                    throw new InvalidOperationException("Launcher smoke capture does not fill its 1920×1080 canvas. Render size: " + Backdrop.RenderSize +
                        "; offset: " + VisualTreeHelper.GetOffset(Backdrop) + "; DPI: " + VisualTreeHelper.GetDpi(Backdrop).DpiScaleX + "; corner alpha: " + corner[3]);
            }
            finally { host.Children.Clear(); Content = Backdrop; }
        }

        private async void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                e.Handled = true;
                if (!OperationActive && !adultOpen && !e.IsRepeat) await OpenAdultAsync();
            }
        }

        private async Task OpenAdultAsync()
        {
            adultOpen = true;
            Mouse.OverrideCursor = null;
            try
            {
                if (config == null || config.Controls == null || string.IsNullOrWhiteSpace(config.Controls.PinHash))
                {
                    MessageBox.Show(this, "No adult PIN is configured. Open KidLauncher.Configurator.exe separately to set a PIN in Controls. Settings and desktop exit stay locked until a PIN is set.", "Adult access", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (!VerifyAdultPin()) return;
                var dialog = AdultWindow("Grown-up controls");
                dialog.Closing += (s, e) => { if (busy && !authorizedExit) e.Cancel = true; };
                var panel = new StackPanel { Margin = new Thickness(24) };
                panel.Children.Add(new TextBlock { Text = "Grown-up controls", FontSize = 26, Margin = new Thickness(0, 0, 0, 18) });
                bool configure = false;
                var settings = AdultButton("Open configurator");
                settings.Click += (s, e) => { configure = true; dialog.Close(); };
                panel.Children.Add(settings);
                var diagnostics = AdultButton("Show adult diagnostics");
                diagnostics.Click += (s, e) => MessageBox.Show(dialog, diagnostic + "\n\nData / logs: " + store.DataDirectory + "\nHotkey: " + (hotkey == null ? "not registered" : hotkey.Strategy), "Diagnostics");
                panel.Children.Add(diagnostics);
                var exit = AdultButton("Exit launcher to Windows");
                exit.Click += async (s, e) =>
                {
                    busy = true;
                    panel.IsEnabled = false;
                    try
                    {
                        // Retry pending restoration before an intentional desktop exit.
                        if (session != null) await session.RecoverAsync();
                        authorizedExit = true;
                        dialog.Close();
                    }
                    catch (Exception ex)
                    {
                        diagnostic = ex.ToString();
                        log.Write("Adult exit blocked by pending recovery: " + diagnostic);
                        MessageBox.Show(dialog, "Recovery must finish before exiting. Close any remaining game, then retry.\n\n" + diagnostic, "Adult diagnostics");
                    }
                    finally { busy = false; panel.IsEnabled = true; }
                };
                panel.Children.Add(exit);
                var back = AdultButton("Back to games");
                back.Click += (s, e) => dialog.Close();
                panel.Children.Add(back);
                dialog.Content = panel;
                dialog.ShowDialog();
                if (authorizedExit) { Close(); return; }
                if (configure)
                {
                    busy = true;
                    ShowOverlay(L("Grown-up time", "Even tijd voor de grote mensen"), L("Your games will be back soon.", "Je spelletjes zijn zo weer terug."), false);
                    Topmost = false;
                    if (hotkey != null) hotkey.Enabled = false;
                    try
                    {
                        var path = Path.Combine(store.ApplicationDirectory, "KidLauncher.Configurator.exe");
                        using (var process = Process.Start(new ProcessStartInfo(path) { WorkingDirectory = store.ApplicationDirectory, UseShellExecute = true }))
                        {
                            if (process == null) throw new InvalidOperationException("Configurator did not start.");
                            await Task.Run(() => process.WaitForExit());
                        }
                        await InitializeAsync();
                    }
                    catch (Exception ex) { Error(L("The settings need a grown-up's help.", "Vraag een volwassene om naar de instellingen te kijken."), ex); }
                    finally { busy = false; BringBack(); }
                }
            }
            finally { adultOpen = false; lastMove = DateTime.UtcNow; }
        }

        private bool VerifyAdultPin()
        {
            var dialog = AdultWindow("Adult PIN");
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "Enter your adult PIN", FontSize = 24, Margin = new Thickness(0, 0, 0, 16) });
            var pin = new PasswordBox { FontSize = 26, MaxLength = 64, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(pin);
            var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(message);
            var unlock = AdultButton("Unlock");
            unlock.IsDefault = true;
            int attempts = 0;
            unlock.Click += (s, e) =>
            {
                if (PinSecurity.VerifyPin(config.Controls, pin.Password)) dialog.DialogResult = true;
                else
                {
                    pin.Clear();
                    message.Text = "That PIN did not match.";
                    if (++attempts >= 3) dialog.DialogResult = false;
                }
            };
            panel.Children.Add(unlock);
            var cancel = AdultButton("Cancel");
            cancel.IsCancel = true;
            panel.Children.Add(cancel);
            dialog.Content = panel;
            dialog.Loaded += (s, e) => pin.Focus();
            return dialog.ShowDialog() == true;
        }

        private Window AdultWindow(string title)
        {
            return new Window { Owner = this, Title = title, Width = 500, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(25, 41, 64)), Foreground = Brushes.White, ShowInTaskbar = false };
        }
        private static Button AdultButton(string text) { return new Button { Content = text, Margin = new Thickness(0, 6, 0, 6), FontSize = 20 }; }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!authorizedExit || OperationActive) { e.Cancel = true; return; }
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            idleTimer.Stop();
            if (hotkey != null) hotkey.Dispose();
            Mouse.OverrideCursor = null;
        }

        private ImageSource LoadArtwork(string artwork, string executable)
        {
            if (!string.IsNullOrWhiteSpace(artwork))
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 720;
                    image.UriSource = new Uri(store.ResolvePath(artwork), UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch (Exception ex) { log.Write("Artwork fallback: " + ex.Message); }
            }
            if (!string.IsNullOrWhiteSpace(executable))
            {
                IntPtr icon = IntPtr.Zero;
                try
                {
                    var info = new ShellFileInfo();
                    if (SHGetFileInfo(store.ResolvePath(executable), 0, ref info, (uint)Marshal.SizeOf(typeof(ShellFileInfo)), 0x100) == IntPtr.Zero) return null;
                    icon = info.Icon;
                    if (icon == IntPtr.Zero) return null;
                    var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    return bitmap;
                }
                catch (Exception ex) { log.Write("Executable icon fallback: " + ex.Message); }
                finally { if (icon != IntPtr.Zero) DestroyIcon(icon); }
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellFileInfo
        {
            public IntPtr Icon;
            public int IconIndex;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    }
}
