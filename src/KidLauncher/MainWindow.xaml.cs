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
using System.Windows.Media.Animation;
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
        private readonly Cursor childCursor;
        private LauncherConfiguration config;
        private GameSession session;
        private WindowsHotkey hotkey;
        private WindowsKeyBlocker keyBlocker;
        // One shuffle per launcher start, so a returning child finds the card she just played where she left it.
        private readonly int shuffleSeed = Environment.TickCount;
        private List<Game> games = new List<Game>();
        private int page;
        private int pageSize = 12;
        private bool busy = true;
        private bool recovered;
        private bool authorizedExit;
        private bool adultOpen;
        private bool exitRequested;
        private bool started;
        private bool twinkling;
        private readonly DispatcherTimer surpriseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private readonly Random random = new Random();
        private int lastSurprise = -1;
        private readonly List<TextBlock> titles = new List<TextBlock>();
        private readonly bool smokeTest;
        private DateTime lastMove = DateTime.UtcNow;
        private DateTime lastWheel = DateTime.MinValue;
        private string diagnostic = "No errors reported.";
        private readonly string[] colors = { "#B9D8CC", "#EDC7B5", "#CFC5E6", "#BFD6E6", "#D3D8B6", "#E6C1D2" };
        public bool OperationActive { get { return busy || (session != null && session.IsRunning); } }

        public MainWindow(ConfigurationStore store, ILog log, bool smokeTest = false)
        {
            InitializeComponent();
            using (var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Pointer.cur")).Stream)
                childCursor = new Cursor(resource);
            Cursor = childCursor;
            ForceCursor = true;
            this.store = store;
            this.log = log;
            this.smokeTest = smokeTest;
            PlayRoom.SizeChanged += (s, e) => SizeTiles();
            Overlay.IsVisibleChanged += (s, e) => { if (Overlay.IsVisible) StopScenery(); AnimateOverlayStar(); };
            IsVisibleChanged += (s, e) => { if (!IsVisible) StopScenery(); };
            Moon.MouseEnter += (s, e) =>
            {
                if (!CanPlayWithScenery) return;
                PulseScenery(MoonBody, 1.08, -8);
                AnimateValue(MoonHalo, UIElement.OpacityProperty, 0.6, 180);
            };
            Moon.MouseLeave += (s, e) => AnimateValue(MoonHalo, UIElement.OpacityProperty, 0, 180);
            Moon.GotKeyboardFocus += (s, e) => { if (CanPlayWithScenery) PulseScenery(MoonBody, 1.08, -8); };
            WishingStars.MouseEnter += (s, e) => WakeStars();
            WishingStars.GotKeyboardFocus += (s, e) => WakeStars();
            Owl.MouseEnter += (s, e) => WakeOwl(false);
            Owl.GotKeyboardFocus += (s, e) => WakeOwl(false);
            Fireflies.MouseEnter += (s, e) => StirFireflies(false);
            Fireflies.GotKeyboardFocus += (s, e) => StirFireflies(false);
            LayoutUpdated += (s, e) => PlaceCornerToys();
            SystemParameters.StaticPropertyChanged += MotionSettingsChanged;
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
            Closed += (s, e) => { childCursor.Dispose(); Application.Current.Shutdown(); };
            PreviewKeyDown += OnKeyDown;
            PreviewMouseMove += (s, e) =>
            {
                lastMove = DateTime.UtcNow; Mouse.OverrideCursor = null;
                UpdateTwinkle();
            };
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
                UpdateTwinkle();
                if (IsActive && !adultOpen && config != null && config.Appearance != null && config.Appearance.CursorHideSeconds > 0 &&
                    DateTime.UtcNow - lastMove >= TimeSpan.FromSeconds(config.Appearance.CursorHideSeconds))
                    Mouse.OverrideCursor = Cursors.None;
            };
            idleTimer.Start();
            surpriseTimer.Tick += (s, e) => PlaySurprise();
            surpriseTimer.Start();
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
                // Tier 2 means WPF composites animations on the GPU; tier 0 means software rendering.
                log.Write("Rendering tier " + (RenderCapability.Tier >> 16) + "; Windows animations " +
                    (SystemParameters.ClientAreaAnimation ? "on" : "off"));
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
            finally { busy = false; UpdateWindowsKeyBlock(); BringBack(); }
        }

        // Blocks the Windows keys while the child uses the launcher or a game; grown-up time unblocks them.
        private void UpdateWindowsKeyBlock()
        {
            bool block = !smokeTest && !adultOpen && config?.Controls?.BlockWindowsKey != false;
            try
            {
                if (block && keyBlocker == null) keyBlocker = new WindowsKeyBlocker();
                if (keyBlocker != null && keyBlocker.Enabled != block)
                {
                    keyBlocker.Enabled = block;
                    log.Write("Windows key " + (block ? "blocked" : "allowed"));
                }
            }
            catch (Exception ex) { log.Write("Windows key block unavailable: " + ex.Message); }
        }

        private string L(string english, string dutch) => config?.Language == "en" ? english : dutch;

        private void ApplyLanguage()
        {
            ReturnButton.Content = L("Back to my games", "Terug naar mijn spelletjes");
            AutomationProperties.SetName(Previous, L("Previous page", "Vorige pagina"));
            AutomationProperties.SetName(Next, L("Next page", "Volgende pagina"));
            AutomationProperties.SetName(Moon, L("Make a shooting star", "Maak een vallende ster"));
            AutomationProperties.SetName(WishingStars, L("Wake the stars", "Maak de sterren wakker"));
            AutomationProperties.SetName(Owl, L("Wake the owl", "Maak de uil wakker"));
            AutomationProperties.SetName(Fireflies, L("Wake the fireflies", "Laat de vuurvliegjes dansen"));
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
            int columns = Math.Max(1, Math.Min(12, a.Columns));
            // On one screen, SizeTiles picks the columns that give the largest cards.
            pageSize = a.AllGamesOnOneScreen ? TileGrid.MaxOnOneScreen : columns * Math.Max(1, Math.Min(12 / columns, a.Rows));
            games = config.Games.Where(g => g.Visible).OrderBy(g => g.SortOrder).ThenBy(g => g.Title).ToList();
            if (a.ShuffleGames && !smokeTest) { var shuffle = new Random(shuffleSeed); games = games.OrderBy(g => shuffle.Next()).ToList(); }
            var count = Math.Max(1, (games.Count + pageSize - 1) / pageSize);
            page = Math.Max(0, Math.Min(page, count - 1));
            var background = (Brush)new BrushConverter().ConvertFromString(a.BackgroundColor);
            Backdrop.Background = background;
            var wallpaper = LoadArtwork(a.BackgroundImage, null);
            if (wallpaper != null) Backdrop.Background = new ImageBrush(wallpaper) { Stretch = Stretch.UniformToFill };
            // A configured wallpaper is always left untouched, even if it could not load.
            BackdropAccents.Visibility = string.IsNullOrWhiteSpace(a.BackgroundImage) ? Visibility.Visible : Visibility.Collapsed;
            Tiles.Children.Clear();
            titles.Clear();
            foreach (var game in games.Skip(page * pageSize).Take(pageSize)) Tiles.Children.Add(CreateTile(game));
            Tiles.Columns = Math.Min(columns, Math.Max(1, Tiles.Children.Count));
            Tiles.Rows = Math.Max(1, (Tiles.Children.Count + Tiles.Columns - 1) / Tiles.Columns);
            if (MotionEnabled)
                for (int i = 0; i < Tiles.Children.Count; i++)
                    if (Tiles.Children[i] is Button tile) EnterCard((Grid)tile.Content, i * 45);
            Previous.IsEnabled = page > 0;
            Next.IsEnabled = page < count - 1;
            Navigation.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (games.Count == 0)
            {
                var empty = new TextBlock { Text = L("★\nReady for new adventures!\nAsk a grown-up to add your games.", "★\nKlaar voor nieuwe avonturen!\nVraag een volwassene om je spelletjes toe te voegen."), FontSize = 28, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Tiles.Children.Add(empty);
            }
            SizeTiles();
        }

        private void SizeTiles()
        {
            if (PlayRoom.ActualWidth <= 0 || PlayRoom.ActualHeight <= 0 || config == null) return;
            double availableHeight = Math.Max(1, PlayRoom.ActualHeight - (Navigation.Visibility == Visibility.Visible ? 88 : 0));
            double ratio = TileGrid.CardRatio(config.Appearance.ShowTitles);
            int cards = Tiles.Children.OfType<Button>().Count();
            Func<double, double> fit = width =>
            {
                if (config.Appearance.AllGamesOnOneScreen && cards > 0)
                {
                    Tiles.Columns = TileGrid.FitColumns(cards, width, availableHeight, ratio);
                    Tiles.Rows = (cards + Tiles.Columns - 1) / Tiles.Columns;
                }
                return Math.Min(340, Math.Min(width / Tiles.Columns, availableHeight / Tiles.Rows / ratio));
            };
            double cell = fit(PlayRoom.ActualWidth);
            // Leave the corner toys a strip on each side of the cards, so a full screen keeps its moon,
            // stars, owl and fireflies. When that would shrink the cards by more than an eighth, the
            // cards keep their size and PlaceCornerToys hides the toys they cover instead.
            if (BackdropAccents.Visibility == Visibility.Visible)
            {
                // DesiredSize includes each toy's corner margin and the constellation's upright rotation.
                // PlaceCornerToys pads toys by 12 and treats touching edges as overlap, so leave a little more.
                double strip = new FrameworkElement[] { Moon, WishingStars, Owl, Fireflies }.Max(toy => toy.DesiredSize.Width) + 16;
                double narrower = Backdrop.ActualWidth - 2 * strip;
                if (narrower > 0 && narrower < PlayRoom.ActualWidth)
                {
                    double smaller = fit(narrower);
                    cell = smaller >= cell * 0.88 ? smaller : fit(PlayRoom.ActualWidth);
                }
            }
            Tiles.Width = cell * Tiles.Columns;
            Tiles.Height = cell * ratio * Tiles.Rows;
            foreach (var title in titles) title.FontSize = Math.Max(12, Math.Min(22, cell * 0.068));
        }

        private Button CreateTile(Game game)
        {
            var color = (Color)ColorConverter.ConvertFromString(colors[Math.Abs(games.IndexOf(game) % colors.Length)]);
            // Storybook paper: warm cream cards with a tan edge and brown titles.
            var cream = new SolidColorBrush(Color.FromRgb(255, 246, 227));
            var layout = new Grid();
            layout.SizeChanged += (s, e) => layout.Clip = new RectangleGeometry(new Rect(layout.RenderSize),
                Math.Max(0, config.Appearance.CornerRadius), Math.Max(0, config.Appearance.CornerRadius));
            // The picture row stays close to square, so square artwork fills it with little cropping.
            layout.RowDefinitions.Add(new RowDefinition());
            if (config.Appearance.ShowTitles) layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.2, GridUnitType.Star) });
            // Cover art can fill its frame. A 32px executable icon cannot, even when the
            // configurator saved one as the game's artwork; it goes in the badge instead.
            var art = LoadArtwork(game.ArtworkPath, null);
            if (art is BitmapSource picture && picture.PixelWidth >= 160)
                layout.Children.Add(new Image { Source = art, Stretch = Stretch.UniformToFill, ClipToBounds = true });
            else
            {
                var cover = new Grid { Background = new LinearGradientBrush(color, Color.FromRgb(245, 239, 227), 55) };
                // Small cards shrink the medallion instead of cropping it.
                var medallion = new Grid { Width = 170, Height = 170 };
                cover.Children.Add(new Viewbox { Child = medallion, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(8) });
                medallion.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 170, Height = 170, Stroke = cream, StrokeThickness = 1, Opacity = 0.5,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                });
                art = art ?? LoadArtwork(null, game.ExecutablePath);
                var badge = new Border { Width = 112, Height = 112, CornerRadius = new CornerRadius(56), Background = cream,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (art != null)
                    badge.Child = new Image { Source = art, Width = Math.Min(64, art.Width), Height = Math.Min(64, art.Height), Stretch = Stretch.Uniform };
                else
                    badge.Child = new System.Windows.Shapes.Path
                    {
                        Data = Geometry.Parse("M24,0 L29,19 48,24 29,29 24,48 19,29 0,24 19,19Z"),
                        Fill = new SolidColorBrush(Color.FromRgb(167, 133, 100)), Width = 48, Height = 48,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    };
                medallion.Children.Add(badge);
                layout.Children.Add(cover);
            }
            if (config.Appearance.ShowTitles)
            {
                // One short line under the picture; children recognise games by their artwork.
                var title = new TextBlock { Text = game.Title, Foreground = new SolidColorBrush(Color.FromRgb(91, 58, 30)),
                    FontSize = 18, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 10, 0),
                    TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                titles.Add(title);
                var caption = new Border { Background = cream, Child = title };
                Grid.SetRow(caption, 1);
                layout.Children.Add(caption);
            }
            var scale = new ScaleTransform();
            var lift = new TranslateTransform();
            // Each card leans a little, like a picture pasted into a scrapbook. The lean is fixed for the
            // launcher session, so a card looks the same after a game, and straightens on hover.
            var lean = new Random(shuffleSeed ^ (game.Title ?? "").GetHashCode());
            double tilt = (0.6 + lean.NextDouble() * 1.2) * (lean.Next(2) == 0 ? -1 : 1);
            var tilting = new RotateTransform(tilt);
            var transforms = new TransformGroup();
            transforms.Children.Add(scale);
            transforms.Children.Add(lift);
            transforms.Children.Add(tilting);
            var visual = new Grid { IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = transforms };
            var radius = new CornerRadius(Math.Max(0, config.Appearance.CornerRadius));
            visual.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(55, 21, 15, 36)), CornerRadius = radius,
                RenderTransform = new TranslateTransform(0, 7) });
            visual.Children.Add(new Border
            {
                Background = cream, CornerRadius = radius, Child = layout, Padding = new Thickness(7),
                CacheMode = new BitmapCache(), BorderThickness = new Thickness(3),
                BorderBrush = new SolidColorBrush(Color.FromRgb(231, 201, 143))
            });
            var ring = new Border { CornerRadius = radius, BorderBrush = cream, BorderThickness = new Thickness(3), Opacity = 0 };
            visual.Children.Add(ring);
            var glint = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M16,0 L19,13 32,16 19,19 16,32 13,19 0,16 13,13Z M40,30 L42,37 49,39 42,41 40,48 38,41 31,39 38,37Z"),
                Fill = cream, Width = 49, Height = 48, Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -16, -14, 0), RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new RotateTransform() } }
            };
            visual.Children.Add(glint);
            var button = new Button
            {
                Content = visual, Background = Brushes.Transparent, Padding = new Thickness(0),
                Margin = new Thickness(Math.Max(0, config.Appearance.Spacing) / 2.0),
                FocusVisualStyle = null,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
            };
            // The transparent template remains the fixed hit target. Only its decorative
            // content moves, so hovering an edge never moves the target out from under it.
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "TileContent");
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.78, "TileContent"));
            template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "TileContent"));
            template.Triggers.Add(disabled);
            button.Template = template;
            Action twinkle = () =>
            {
                if (!MotionEnabled || !button.IsEnabled) return;
                var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620), FillBehavior = FillBehavior.Stop };
                flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.3), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
                glint.BeginAnimation(UIElement.OpacityProperty, flash);
                PulseScenery(glint, 1.2, 12);
            };
            bool highlighted = false;
            Action updateFeedback = () =>
            {
                bool active = button.IsEnabled && (button.IsMouseOver || button.IsKeyboardFocusWithin);
                if (active && !highlighted) twinkle();
                highlighted = active;
                AnimateValue(scale, ScaleTransform.ScaleXProperty, active ? 1.025 : 1, 180);
                AnimateValue(scale, ScaleTransform.ScaleYProperty, active ? 1.025 : 1, 180);
                AnimateValue(lift, TranslateTransform.YProperty, active ? -4 : 0, 180);
                AnimateValue(tilting, RotateTransform.AngleProperty, active ? 0 : tilt, 220);
                AnimateValue(ring, UIElement.OpacityProperty, active ? 1 : 0, 140);
                Panel.SetZIndex(button, active ? 1 : 0);
                if (!active) glint.BeginAnimation(UIElement.OpacityProperty, null);
            };
            button.MouseEnter += (s, e) => updateFeedback();
            button.MouseLeave += (s, e) => updateFeedback();
            button.IsKeyboardFocusWithinChanged += (s, e) => updateFeedback();
            button.IsEnabledChanged += (s, e) => updateFeedback();
            AutomationProperties.SetName(button, game.Title);
            button.Click += async (s, e) => { twinkle(); await LaunchAsync(game, visual); };
            return button;
        }

        private bool MotionEnabled => !smokeTest && SystemParameters.ClientAreaAnimation;

        private bool CanPlayWithScenery => MotionEnabled && !OperationActive && !adultOpen &&
            IsVisible && !Overlay.IsVisible && BackdropAccents.IsVisible;

        private void Moon_Click(object sender, RoutedEventArgs e)
        {
            if (!CanPlayWithScenery) return;
            PulseScenery(MoonBody, 1.1, -10);
            var origin = Moon.TranslatePoint(new Point(12, 12), BackdropAccents);
            SendShootingStar(origin, Math.Max(12, origin.X - 340), origin.Y + 70, 900);
        }

        private void SendShootingStar(Point from, double toX, double toY, int milliseconds)
        {
            var travel = (TranslateTransform)ShootingStar.RenderTransform;
            AnimateValue(travel, TranslateTransform.XProperty, toX, milliseconds, from.X);
            AnimateValue(travel, TranslateTransform.YProperty, toY, milliseconds, from.Y);
            var glow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(milliseconds), FillBehavior = FillBehavior.Stop };
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.1)));
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.55)));
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            ShootingStar.BeginAnimation(UIElement.OpacityProperty, glow);
        }

        private void Stars_Click(object sender, RoutedEventArgs e) { WakeStars(); }

        private void WakeStars()
        {
            if (!CanPlayWithScenery) return;
            int delay = 0;
            foreach (var star in new[] { StarOne, StarTwo, StarThree, StarFour, StarFive, StarSix })
            {
                PulseScenery(star, 1.55, 18, delay);
                delay += 100;
            }
            Swing(StarThread, UIElement.OpacityProperty, 0.6, 900);
            Hold(StarTrace, UIElement.OpacityProperty, 0.85, 1300);
            StarTrace.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, new DoubleAnimation(130, 0, TimeSpan.FromMilliseconds(820))
            {
                FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        private void Owl_Click(object sender, RoutedEventArgs e) { WakeOwl(true); }

        private void WakeOwl(bool hop)
        {
            if (!CanPlayWithScenery) return;
            // Hover peeks; a click makes the owl hop and flap. Either way it falls back asleep.
            int awake = hop ? 1900 : 1300;
            Hold(OwlEyes, UIElement.OpacityProperty, 1, awake);
            Hold(OwlSleep, UIElement.OpacityProperty, 0, awake);
            Hold(OwlDreams, UIElement.OpacityProperty, 0, awake);
            var body = ((TransformGroup)OwlBird.RenderTransform).Children;
            Swing(body[1], RotateTransform.AngleProperty, hop ? -7 : 6, hop ? 1000 : 800);
            if (!hop) return;
            Swing(body[2], TranslateTransform.YProperty, -16, 560, peakAt: 0.45);
            Swing(body[0], ScaleTransform.ScaleYProperty, 1.05, 560, peakAt: 0.45);
            foreach (var wing in new[] { OwlWingLeft, OwlWingRight })
            {
                var flap = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(640), FillBehavior = FillBehavior.Stop };
                double angle = wing == OwlWingLeft ? 32 : -32;
                foreach (var step in new[] { 0.25, 0.5, 0.75, 1.0 })
                    flap.KeyFrames.Add(new EasingDoubleKeyFrame(step == 0.5 || step == 1.0 ? 0 : angle, KeyTime.FromPercent(step),
                        new SineEase { EasingMode = EasingMode.EaseInOut }));
                wing.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, flap);
            }
        }

        private void Fireflies_Click(object sender, RoutedEventArgs e) { StirFireflies(true); }

        private void StirFireflies(bool burst)
        {
            if (!CanPlayWithScenery) return;
            // Hover wakes the three resting fireflies. A click also lifts three hidden ones out of the grass.
            var flies = new[] { Firefly1, Firefly2, Firefly3, Firefly4, Firefly5, Firefly6 };
            double[] dx = { -16, 12, 18, -20, 8, 22 }, dy = { -20, -28, -18, -48, -54, -42 };
            double reach = burst ? 1 : 0.45;
            int duration = burst ? 1800 : 1200;
            for (int i = 0; i < (burst ? flies.Length : 3); i++)
            {
                int delay = i * 90;
                var drift = flies[i].RenderTransform;
                // Different peak times for X and Y give each firefly a curved loop instead of a straight line.
                Swing(drift, TranslateTransform.XProperty, dx[i] * reach, duration, delay, 0.3);
                Swing(drift, TranslateTransform.YProperty, dy[i] * reach, duration, delay, 0.55);
                Hold(flies[i], UIElement.OpacityProperty, 1, duration, delay);
            }
        }

        // Cards rise and fade in one after another. Only the decoration moves; the button stays put.
        private static void EnterCard(Grid visual, int delay)
        {
            var lift = ((TransformGroup)visual.RenderTransform).Children[1];
            double total = delay + 460, start = delay / total;
            foreach (var step in new[] { Tuple.Create<IAnimatable, DependencyProperty, double>(visual, UIElement.OpacityProperty, 0),
                Tuple.Create<IAnimatable, DependencyProperty, double>(lift, TranslateTransform.YProperty, 22) })
            {
                var enter = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(total), FillBehavior = FillBehavior.Stop };
                enter.KeyFrames.Add(new DiscreteDoubleKeyFrame(step.Item3, KeyTime.FromPercent(0)));
                enter.KeyFrames.Add(new DiscreteDoubleKeyFrame(step.Item3, KeyTime.FromPercent(start)));
                enter.KeyFrames.Add(new EasingDoubleKeyFrame((double)step.Item1.GetAnimationBaseValue(step.Item2), KeyTime.FromPercent(1),
                    new CubicEase { EasingMode = EasingMode.EaseOut }));
                step.Item1.BeginAnimation(step.Item2, enter);
            }
        }

        private bool IdleMagic => config?.Appearance?.IdleAnimations != false;

        // While the launcher rests, something small happens every few seconds. The first surprise
        // comes soon after the scene appears; later ones wait 5 to 9 seconds and never repeat in a row.
        private void PlaySurprise()
        {
            // Retry soon while a game, overlay, or menu blocks the scene, so a surprise follows shortly after it returns.
            if (!IdleMagic || !CanPlayWithScenery) { surpriseTimer.Interval = TimeSpan.FromSeconds(3); return; }
            surpriseTimer.Interval = TimeSpan.FromMilliseconds(random.Next(5000, 9000));
            // Cards can cover any corner toy on full screens; only pick surprises the child can see.
            // The shooting star needs no toy, so at least one choice always remains.
            var visible = new[] { 0, 1, 2, 3, 4 }.Where(choice => (choice != 1 || Owl.Visibility == Visibility.Visible) &&
                (choice != 2 || Fireflies.Visibility == Visibility.Visible) && (choice != 3 || WishingStars.Visibility == Visibility.Visible) &&
                (choice != 4 || Moon.Visibility == Visibility.Visible)).ToArray();
            var choices = visible.Length > 1 ? visible.Where(choice => choice != lastSurprise).ToArray() : visible;
            int next = choices[random.Next(choices.Length)];
            lastSurprise = next;
            switch (next)
            {
                case 0:
                    double width = BackdropAccents.ActualWidth;
                    var from = new Point(width * (0.45 + random.NextDouble() * 0.3), 30 + random.NextDouble() * 60);
                    SendShootingStar(from, from.X - width * 0.22, from.Y + 60 + random.NextDouble() * 40, 1100);
                    break;
                case 1: WakeOwl(false); break;
                case 2: StirFireflies(false); break;
                case 3: WakeStars(); break;
                default:
                    PulseScenery(MoonBody, 1.06, -7);
                    Hold(MoonHalo, UIElement.OpacityProperty, 0.55, 1200);
                    break;
            }
        }

        // The sky twinkles and the moon breathes. With idle surprises on, this continues while the
        // launcher rests. With them off, it runs only while the pointer moves and fades back to the
        // resting look three seconds after the pointer stops, leaving the scene still.
        private void UpdateTwinkle()
        {
            bool wanted = CanPlayWithScenery && (IdleMagic || DateTime.UtcNow - lastMove < TimeSpan.FromSeconds(3));
            if (wanted == twinkling) return;
            twinkling = wanted;
            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
            int i = 0;
            foreach (UIElement star in Twinkles.Children)
            {
                if (!wanted) { Settle(star, UIElement.OpacityProperty); continue; }
                var twinkle = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(2200 + i % 4 * 450), BeginTime = TimeSpan.FromMilliseconds(i * 230),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                twinkle.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.3), sine));
                twinkle.KeyFrames.Add(new EasingDoubleKeyFrame(0.25, KeyTime.FromPercent(0.7), sine));
                twinkle.KeyFrames.Add(new EasingDoubleKeyFrame(0.6, KeyTime.FromPercent(1), sine));
                // Gentle fades look the same at 30 fps and cost half as much on older machines.
                Timeline.SetDesiredFrameRate(twinkle, 30);
                star.BeginAnimation(UIElement.OpacityProperty, twinkle);
                i++;
            }
            if (!wanted) { Settle(MoonGlow, UIElement.OpacityProperty); return; }
            var breathe = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(3600), RepeatBehavior = RepeatBehavior.Forever };
            breathe.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromPercent(0.5), sine));
            breathe.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), sine));
            Timeline.SetDesiredFrameRate(breathe, 30);
            MoonGlow.BeginAnimation(UIElement.OpacityProperty, breathe);
        }

        // Ease from the value on screen back to the resting value, then release the property.
        private static void Settle(IAnimatable target, DependencyProperty property)
        {
            target.BeginAnimation(property, new DoubleAnimation
            {
                To = (double)target.GetAnimationBaseValue(property), Duration = TimeSpan.FromMilliseconds(600),
                FillBehavior = FillBehavior.Stop, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        private void PlaceCornerToys()
        {
            // The sky and garden toys sit in the corners. Hide any toy rather than let it peek out
            // from under a card or page arrow on small screens or full pages.
            if (BackdropAccents.Visibility != Visibility.Visible || Tiles.ActualWidth <= 0) return;
            var blocked = new List<Rect> { Bounds(Tiles) };
            if (Navigation.IsVisible) { blocked.Add(Bounds(Previous)); blocked.Add(Bounds(Next)); }
            foreach (var toy in new FrameworkElement[] { Moon, WishingStars, Owl, Fireflies })
            {
                var area = Bounds(toy);
                area.Inflate(12, 12);
                var visibility = blocked.Any(r => r.IntersectsWith(area)) ? Visibility.Hidden : Visibility.Visible;
                if (toy.Visibility != visibility) toy.Visibility = visibility;
            }
        }

        // TransformBounds includes layout transforms, such as the upright constellation.
        private Rect Bounds(FrameworkElement element) { return element.TransformToVisual(Backdrop).TransformBounds(new Rect(element.RenderSize)); }

        private void PulseScenery(FrameworkElement glyph, double size, double angle, int delay = 0)
        {
            var transforms = ((TransformGroup)glyph.RenderTransform).Children;
            Swing(transforms[0], ScaleTransform.ScaleXProperty, size, 680, delay);
            Swing(transforms[0], ScaleTransform.ScaleYProperty, size, 680, delay);
            // Tilt relative to the glyph's resting angle, so pre-tilted stars nod around their own pose.
            double rest = (double)transforms[1].GetAnimationBaseValue(RotateTransform.AngleProperty);
            Swing(transforms[1], RotateTransform.AngleProperty, rest + angle, 680, delay);
        }

        // Out-and-back motion. With no keyframe at time zero, a retrigger starts from the value
        // currently on screen, so repeated hovers and clicks never jump.
        private static void Swing(IAnimatable target, DependencyProperty property, double peak, int milliseconds, int delay = 0, double peakAt = 0.4)
        {
            var swing = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(milliseconds), BeginTime = TimeSpan.FromMilliseconds(delay), FillBehavior = FillBehavior.Stop
            };
            swing.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(peakAt), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            swing.KeyFrames.Add(new EasingDoubleKeyFrame((double)target.GetAnimationBaseValue(property), KeyTime.FromPercent(1),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            target.BeginAnimation(property, swing);
        }

        // Fade to a value, stay there, then fade back to rest.
        private static void Hold(IAnimatable target, DependencyProperty property, double value, int milliseconds, int delay = 0)
        {
            var hold = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(milliseconds), BeginTime = TimeSpan.FromMilliseconds(delay), FillBehavior = FillBehavior.Stop
            };
            hold.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromPercent(0.15), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            hold.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromPercent(0.75)));
            hold.KeyFrames.Add(new EasingDoubleKeyFrame((double)target.GetAnimationBaseValue(property), KeyTime.FromPercent(1),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            target.BeginAnimation(property, hold);
        }

        private void StopScenery()
        {
            foreach (var glyph in new FrameworkElement[] { MoonBody, StarOne, StarTwo, StarThree, StarFour, StarFive, StarSix, OwlBird })
                foreach (var part in ((TransformGroup)glyph.RenderTransform).Children)
                    foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty, RotateTransform.AngleProperty,
                        TranslateTransform.XProperty, TranslateTransform.YProperty })
                        if (property.OwnerType.IsInstanceOfType(part)) part.BeginAnimation(property, null);
            MoonHalo.BeginAnimation(UIElement.OpacityProperty, null); MoonHalo.Opacity = 0;
            twinkling = false;
            MoonGlow.BeginAnimation(UIElement.OpacityProperty, null);
            foreach (UIElement star in Twinkles.Children) star.BeginAnimation(UIElement.OpacityProperty, null);
            foreach (var element in new UIElement[] { StarThread, StarTrace, ShootingStar, OwlEyes, OwlSleep, OwlDreams,
                Firefly1, Firefly2, Firefly3, Firefly4, Firefly5, Firefly6 })
                element.BeginAnimation(UIElement.OpacityProperty, null);
            StarTrace.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
            OwlWingLeft.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            OwlWingRight.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            foreach (var drift in new[] { ShootingStar.RenderTransform, Firefly1.RenderTransform, Firefly2.RenderTransform, Firefly3.RenderTransform,
                Firefly4.RenderTransform, Firefly5.RenderTransform, Firefly6.RenderTransform })
            {
                drift.BeginAnimation(TranslateTransform.XProperty, null);
                drift.BeginAnimation(TranslateTransform.YProperty, null);
            }
        }

        private void MotionSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation) && !MotionEnabled) StopScenery();
        }

        private void AnimateValue(DependencyObject target, DependencyProperty property, double value, int milliseconds, double? from = null)
        {
            double start = from ?? (double)target.GetValue(property);
            target.SetValue(property, value);
            ((IAnimatable)target).BeginAnimation(property, MotionEnabled ? new DoubleAnimation(start, value, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            } : null);
        }

        private async Task LaunchAsync(Game game, FrameworkElement card)
        {
            if (OperationActive || !recovered || Overlay.Visibility == Visibility.Visible || adultOpen) return;
            busy = true;
            try
            {
                // Let the chosen card pop before the loading screen covers it. busy already blocks a second launch.
                if (MotionEnabled) { Celebrate(card); await Task.Delay(450); }
                PlayRoom.IsEnabled = false;
                ShowOverlay(L("Let's go!", "Daar gaan we!"), L("Opening ", "We starten ") + game.Title + "…", false);
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
                Sparkles.Children.Clear();
                if (hotkey != null) hotkey.Enabled = false;
                busy = false;
                PlayRoom.IsEnabled = true;
                BringBack();
            }
        }

        // The card bounces and a ring of little stars bursts out of it, like a storybook page opening.
        private void Celebrate(FrameworkElement card)
        {
            Sparkles.Children.Clear();
            var scale = (ScaleTransform)((TransformGroup)card.RenderTransform).Children[0];
            Swing(scale, ScaleTransform.ScaleXProperty, 1.08, 420, 0, 0.35);
            Swing(scale, ScaleTransform.ScaleYProperty, 1.08, 420, 0, 0.35);
            var center = card.TranslatePoint(new Point(card.ActualWidth / 2, card.ActualHeight / 2), Sparkles);
            var colors = new[] { Color.FromRgb(255, 241, 205), Color.FromRgb(247, 229, 138), Color.FromRgb(242, 198, 214), Color.FromRgb(207, 197, 230) };
            var shape = Geometry.Parse("M12,0 L14.6,9.4 24,12 14.6,14.6 12,24 9.4,14.6 0,12 9.4,9.4Z");
            var duration = TimeSpan.FromMilliseconds(600);
            for (int i = 0; i < 12; i++)
            {
                double angle = (i + random.NextDouble() * 0.6) * Math.PI / 6, size = 14 + random.Next(12);
                double from = card.ActualWidth * 0.25, to = card.ActualWidth * (0.6 + random.NextDouble() * 0.25);
                var grow = new ScaleTransform(0.4, 0.4);
                var move = new TranslateTransform();
                var transforms = new TransformGroup();
                transforms.Children.Add(grow);
                transforms.Children.Add(new RotateTransform(random.Next(-30, 30)));
                transforms.Children.Add(move);
                var star = new System.Windows.Shapes.Path
                {
                    Data = shape, Stretch = Stretch.Uniform, Width = size, Height = size, Fill = new SolidColorBrush(colors[i % colors.Length]),
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = transforms
                };
                Canvas.SetLeft(star, center.X - size / 2);
                Canvas.SetTop(star, center.Y - size / 2);
                Sparkles.Children.Add(star);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Math.Cos(angle) * from, Math.Cos(angle) * to, duration) { EasingFunction = ease });
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Math.Sin(angle) * from, Math.Sin(angle) * to, duration) { EasingFunction = ease });
                var swell = new DoubleAnimationUsingKeyFrames { Duration = duration };
                swell.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromPercent(0.3), ease));
                swell.KeyFrames.Add(new LinearDoubleKeyFrame(0.6, KeyTime.FromPercent(1)));
                grow.BeginAnimation(ScaleTransform.ScaleXProperty, swell);
                grow.BeginAnimation(ScaleTransform.ScaleYProperty, swell);
                var fade = new DoubleAnimationUsingKeyFrames { Duration = duration };
                fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.5)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                star.BeginAnimation(UIElement.OpacityProperty, fade);
            }
        }

        public async void RequestGameExit()
        {
            if (session == null || !session.IsRunning || exitRequested) return;
            exitRequested = true;
            try { log.Write("Global exit-game key pressed; source=" + hotkey.LastTrigger); await session.RequestExitAsync(); }
            catch (Exception ex) { Error(L("We need a grown-up's help to come back.", "Vraag een volwassene om je terug te helpen."), ex); }
            finally { exitRequested = false; }
        }

        private void BringBack()
        {
            if (session != null && session.IsRunning) return;
            // Replace the remembered child before Show can restore its logical focus.
            FocusManager.SetFocusedElement(this, Backdrop);
            Show();
            EnterFullscreen();
            Topmost = true;
            Activate();
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
            // A clicked tile retains logical focus while its game owns the desktop.
            // Return focus to the room so keyboard-focus feedback cannot pin its hover.
            Keyboard.Focus(Backdrop);
            Mouse.Synchronize();
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
            AnimateOverlayStar();
            Mouse.OverrideCursor = null;
        }

        private void AnimateOverlayStar()
        {
            var transforms = (TransformGroup)OverlaySymbol.RenderTransform;
            var scale = (ScaleTransform)transforms.Children[0];
            var rotation = (RotateTransform)transforms.Children[1];
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            // A short greeting, never an endless spinner; errors remain calm and still.
            if (!MotionEnabled || !Overlay.IsVisible || ReturnButton.Visibility == Visibility.Visible) return;
            var pulse = new DoubleAnimation(1, 1.09, TimeSpan.FromMilliseconds(600))
            {
                AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), FillBehavior = FillBehavior.Stop,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            var sway = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(1200), RepeatBehavior = new RepeatBehavior(2), FillBehavior = FillBehavior.Stop
            };
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            sway.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            sway.KeyFrames.Add(new EasingDoubleKeyFrame(-6, KeyTime.FromPercent(0.25), ease));
            sway.KeyFrames.Add(new EasingDoubleKeyFrame(6, KeyTime.FromPercent(0.75), ease));
            sway.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), ease));
            rotation.BeginAnimation(RotateTransform.AngleProperty, sway);
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
                if (!ReturnButton.Content.Equals(language == "nl" ? "Terug naar mijn spelletjes" : "Back to my games"))
                    throw new InvalidOperationException("Launcher language did not update.");
                if (Tiles.Children.Count != config.Games.Count || Navigation.Visibility == Visibility.Visible)
                    throw new InvalidOperationException("All games must fit on one screen without page arrows.");
                Capture(directory, prefix + "default.png");
                if (new FrameworkElement[] { Moon, WishingStars, Owl, Fireflies }.Any(toy => toy.Visibility != Visibility.Visible))
                    throw new InvalidOperationException("With all " + config.Games.Count + " games on one screen, the moon, stars, owl and fireflies must stay visible beside the cards.");
                config.Appearance.AllGamesOnOneScreen = false;
                RenderPage();
                Next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (page != 1 || Tiles.Children.Count != config.Games.Count - 12 || Next.IsEnabled || !Previous.IsEnabled)
                    throw new InvalidOperationException("Launcher pagination smoke check failed.");
                Capture(directory, prefix + "page-2.png");
                Previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (page != 0 || Tiles.Children.Count != 12 || !Next.IsEnabled || Previous.IsEnabled)
                    throw new InvalidOperationException("Launcher return-page smoke check failed.");
                config.Appearance.AllGamesOnOneScreen = true;
                RenderPage();
                ShowOverlay(L("Let's go!", "Daar gaan we!"), L("Opening ", "We starten ") + "Putt-Putt Saves the Zoo…", false);
                Capture(directory, prefix + "loading.png");
                Error(L("That adventure couldn't start.", "Dit avontuur kon niet starten."), new IOException("Visual smoke test error state."));
                Capture(directory, prefix + "error.png");
                config.Games.ForEach(game => game.Visible = false);
                RenderPage(); Overlay.Visibility = Visibility.Collapsed;
                Capture(directory, prefix + "empty.png");
                config.Games.ForEach(game => game.Visible = true);
            }
            // Reuse a rendered image as a test bitmap, not as bundled game artwork.
            config.Language = "nl";
            for (int i = 0; i < config.Games.Count; i++) config.Games[i].Visible = i < 3;
            config.Games[0].ArtworkPath = Path.Combine(directory, "launcher-default.png");
            config.Games[1].ExecutablePath = typeof(App).Assembly.Location;
            RenderPage(); Overlay.Visibility = Visibility.Collapsed;
            Capture(directory, "launcher-artwork.png");
            config.Appearance.ShowTitles = false;
            RenderPage();
            Capture(directory, "launcher-no-titles.png");
            config.Appearance.BackgroundImage = config.Games[0].ArtworkPath;
            RenderPage();
            if (BackdropAccents.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Wallpaper must suppress scenery.");
            Capture(directory, "launcher-wallpaper.png");
            File.WriteAllText(Path.Combine(directory, "launcher-checks.txt"), "PASS: Dutch and English loading, error and empty states rendered. All " + config.Games.Count + " games fit on one screen without page arrows, with the moon, stars, owl and fireflies visible beside them; with paging on, 12 tiles on page 1 and the rest on page 2; previous/next Click events update pages without page text or headings.");
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
                PlaceCornerToys();
                if (!Equals(OverlayTitle.Foreground, Foreground))
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
            UpdateWindowsKeyBlock();
            StopScenery();
            Mouse.OverrideCursor = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(config?.Controls?.PinHash) && !VerifyAdultPin()) return;
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
            finally
            {
                adultOpen = false; lastMove = DateTime.UtcNow;
                if (!authorizedExit) UpdateWindowsKeyBlock();
            }
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
            SystemParameters.StaticPropertyChanged -= MotionSettingsChanged;
            idleTimer.Stop();
            surpriseTimer.Stop();
            if (hotkey != null) hotkey.Dispose();
            if (keyBlocker != null) keyBlocker.Dispose();
            Mouse.OverrideCursor = null;
        }

        private ImageSource LoadArtwork(string artwork, string executable)
        {
            if (!string.IsNullOrWhiteSpace(artwork))
            {
                try
                {
                    // Decode large images at 720px to save memory, but never upscale small ones:
                    // the tile needs their real size to tell an icon from cover art.
                    var uri = new Uri(store.ResolvePath(artwork), UriKind.Absolute);
                    BitmapImage image = null;
                    foreach (int width in new[] { 0, 720 })
                    {
                        image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.DecodePixelWidth = width;
                        image.UriSource = uri;
                        image.EndInit();
                        if (image.PixelWidth <= 720) break;
                    }
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
