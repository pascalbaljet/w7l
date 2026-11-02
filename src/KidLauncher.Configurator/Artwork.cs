using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KidLauncher.Core;

namespace KidLauncher.Configurator
{
    internal static class Artwork
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        public static BitmapSource Image(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(Path.GetFullPath(path)); image.EndInit(); image.Freeze(); return image;
            }
            catch { return null; }
        }
        public static BitmapSource Icon(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return null;
            var large = new IntPtr[1]; var small = new IntPtr[1];
            try
            {
                int index = 0, comma = location.LastIndexOf(',');
                if (comma > 0 && int.TryParse(location.Substring(comma + 1), out index)) location = location.Substring(0, comma);
                location = Environment.ExpandEnvironmentVariables(location.Trim('"'));
                if (Path.GetExtension(location).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var link = WindowsShortcutService.Read(location); location = link.IconPath ?? link.TargetPath;
                    comma = location.LastIndexOf(',');
                    if (comma > 0 && int.TryParse(location.Substring(comma + 1), out index)) location = location.Substring(0, comma);
                }
                if (!File.Exists(location)) return null;
                ExtractIconEx(location, index, large, small, 1);
                IntPtr handle = large[0] != IntPtr.Zero ? large[0] : small[0]; if (handle == IntPtr.Zero) return null;
                var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); image.Freeze(); return image;
            }
            catch { return null; }
            finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); if (small[0] != IntPtr.Zero && small[0] != large[0]) DestroyIcon(small[0]); }
        }
        public static BitmapSource Load(ConfigurationStore store, Game game)
        {
            try { return Image(store.ResolvePath(game.ArtworkPath)) ?? Icon(store.ResolvePath(game.ExecutablePath)); }
            catch { return null; }
        }
        public static string SaveIcon(ConfigurationStore store, string iconPath, string target)
        {
            var image = Icon(iconPath) ?? Icon(target); if (image == null) return null;
            string relative = Path.Combine("Assets", Guid.NewGuid().ToString("N") + ".png");
            Directory.CreateDirectory(Path.Combine(store.ApplicationDirectory, "Assets"));
            SavePng(image, Path.Combine(store.ApplicationDirectory, relative)); return relative;
        }
        public static void SavePng(BitmapSource image, string path)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { encoder.Save(stream); stream.Flush(true); }
        }
    }

    internal sealed class ArtworkEditor : Window
    {
        readonly BitmapSource source;
        readonly double aspect;
        readonly Image image = new Image { Stretch = Stretch.Uniform };
        readonly Slider zoom = new Slider { Minimum = 1, Maximum = 5, Value = 1, TickFrequency = .1 };
        readonly Slider x = new Slider { Minimum = 0, Maximum = 1, Value = .5 };
        readonly Slider y = new Slider { Minimum = 0, Maximum = 1, Value = .5 };
        BitmapSource cropped;
        public ArtworkEditor(string path, double tileAspect)
        {
            Title = "Crop tile artwork"; Width = 760; Height = 680; MinWidth = 500; MinHeight = 450;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            aspect = Math.Max(.02, Math.Min(100, tileAspect));
            source = Artwork.Image(path) ?? throw new InvalidOperationException("This image could not be read. Choose a valid PNG or JPEG.");
            var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
            var controls = new StackPanel(); DockPanel.SetDock(controls, Dock.Bottom); root.Children.Add(controls);
            controls.Children.Add(new TextBlock { Text = "Zoom" }); controls.Children.Add(zoom);
            controls.Children.Add(new TextBlock { Text = "Horizontal crop position" }); controls.Children.Add(x);
            controls.Children.Add(new TextBlock { Text = "Vertical crop position" }); controls.Children.Add(y);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var center = new Button { Content = "Center", Padding = new Thickness(15, 6, 15, 6), Margin = new Thickness(4) }; center.Click += (s, e) => { x.Value = .5; y.Value = .5; };
            var reset = new Button { Content = "Reset", Padding = new Thickness(15, 6, 15, 6), Margin = new Thickness(4) }; reset.Click += (s, e) => { zoom.Value = 1; x.Value = .5; y.Value = .5; };
            var save = new Button { Content = "Use this crop", Padding = new Thickness(15, 6, 15, 6), Margin = new Thickness(4), IsDefault = true }; save.Click += (s, e) => DialogResult = true;
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(15, 6, 15, 6), Margin = new Thickness(4), IsCancel = true };
            buttons.Children.Add(center); buttons.Children.Add(reset); buttons.Children.Add(cancel); buttons.Children.Add(save); controls.Children.Add(buttons);
            root.Children.Add(new Border { Background = Brushes.DimGray, Padding = new Thickness(12), Child = image });
            zoom.ValueChanged += (s, e) => UpdateCrop(); x.ValueChanged += (s, e) => UpdateCrop(); y.ValueChanged += (s, e) => UpdateCrop();
            UpdateCrop();
        }
        void UpdateCrop()
        {
            double width = Math.Min(source.PixelWidth, source.PixelHeight * aspect) / zoom.Value, height = width / aspect;
            int w = Math.Max(1, Math.Min(source.PixelWidth, (int)Math.Round(width))), h = Math.Max(1, Math.Min(source.PixelHeight, (int)Math.Round(height)));
            int left = (int)Math.Round((source.PixelWidth - w) * x.Value), top = (int)Math.Round((source.PixelHeight - h) * y.Value);
            var crop = new CroppedBitmap(source, new Int32Rect(left, top, w, h)); crop.Freeze(); cropped = crop; image.Source = crop;
        }
        public void Save(string path)
        {
            int width = 800, height = Math.Max(1, (int)Math.Round(width / aspect));
            if (height > 1600) { height = 1600; width = Math.Max(1, (int)Math.Round(height * aspect)); }
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawImage(cropped, new Rect(0, 0, width, height));
            var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); output.Render(visual); output.Freeze(); Artwork.SavePng(output, path);
        }
    }
}
