using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KidLauncher.Configurator
{
    public sealed partial class MainWindow
    {
        static string SampleArtwork(string directory, string caption, Color color, string symbol)
        {
            Directory.CreateDirectory(Path.Combine(directory, "Assets"));
            string relative = Path.Combine("Assets", Guid.NewGuid().ToString("N") + ".png");
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(color, Color.FromRgb(25, 45, 80), 90), null, new Rect(0, 0, 800, 480));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), null, new Point(650, 100), 190, 190);
                var text = new FormattedText(symbol, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Symbol"), 180, Brushes.White, 1.0);
                dc.DrawText(text, new Point(295, 65));
                dc.DrawText(new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 42, Brushes.White, 1.0), new Point(75, 320));
            }
            var bitmap = new RenderTargetBitmap(800, 480, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            Artwork.SavePng(bitmap, Path.Combine(directory, relative)); return relative;
        }

        internal async Task CaptureSmokeAsync(string screenshotPath)
        {
            if (!smoke) throw new InvalidOperationException("Smoke assertions require isolated smoke mode.");
            screenshotPath = Path.GetFullPath(screenshotPath); Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath));
            string stem = Path.Combine(Path.GetDirectoryName(screenshotPath), Path.GetFileNameWithoutExtension(screenshotPath));
            var report = new StringBuilder("KidLauncher.Configurator native WPF render smoke assertions\r\n");
            Assert(tabs.Items.Count == 6, "Six configuration tabs exist.", report);
            Assert(config.Games.Count == 14 && games.Items.Count == 14, "Representative games, including a second page, are loaded.", report);
            Assert(!File.Exists(store.ConfigurationPath), "Temporary smoke store has no saved real configuration.", report);
            Assert(Artwork.Load(store, config.Games[0]) != null, "Managed artwork decodes.", report);
            var indices = new[] { 0, 1, 2, 3, 5 };
            var names = new[] { "Games", "Appearance", "Controls", "Virtual-drives", "Diagnostics" };
            for (int i = 0; i < indices.Length; i++)
            {
                tabs.SelectedIndex = indices[i];
                await Dispatcher.InvokeAsync(() => { UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
                var tab = (TabItem)tabs.SelectedItem;
                var nodes = Descendants((DependencyObject)tab.Content).ToArray();
                Assert(ActualWidth >= 900 && ActualHeight >= 600, names[i] + ": window has a useful rendered size.", report);
                Assert(nodes.OfType<Button>().Any(b => b.IsVisible && b.IsEnabled), names[i] + ": actionable native buttons exist.", report);
                Assert(nodes.OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text)), names[i] + ": explanatory labels render.", report);
                Assert(((FrameworkElement)tab.Content).ActualWidth > 100 && ((FrameworkElement)tab.Content).ActualHeight > 100, names[i] + ": active tab is laid out.", report);
                if (indices[i] == 0)
                {
                    Assert(games.SelectedItem is KidLauncher.Core.Game && nodes.OfType<TextBox>().Count() >= 8, "Games: editable game fields and a selected game exist.", report);
                    Assert(nodes.OfType<ComboBox>().Any(), "Games: driver-mode dropdown exists.", report);
                }
                if (indices[i] == 1)
                {
                    Assert(preview.Width == 1920 && preview.Height == 1080 && preview.Children.Count > 10, "Appearance: scaled native 1920×1080 tile preview is populated.", report);
                    Assert(pageLabel.Text.Contains("of 2"), "Appearance: pagination includes a second page.", report);
                    previewPage = 1; RefreshPreview(); Assert(pageLabel.Text.Contains("Page 2"), "Appearance: second-page navigation works.", report);
                    previewPage = 0; RefreshPreview(); UpdateLayout();
                }
                if (indices[i] == 2) Assert(nodes.OfType<PasswordBox>().Count() == 3, "Controls: current/new/confirmation PIN fields exist; no authentication performed.", report);
                if (indices[i] == 3) Assert(nodes.OfType<TextBox>().Any(), "Virtual drives: editable VCD utility path exists; no drive operation performed.", report);
                string sibling = stem + "-" + names[i] + ".png"; CaptureWindow(sibling);
                if (i == 0) CaptureWindow(screenshotPath);
                if (indices[i] == 2)
                {
                    var changePin = nodes.OfType<Button>().Single(b => Equals(b.Content, "Change PIN"));
                    changePin.BringIntoView();
                    await Dispatcher.InvokeAsync(() => { UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
                    var bounds = changePin.TransformToAncestor(tabs).TransformBounds(new Rect(changePin.RenderSize));
                    Assert(bounds.Top >= 0 && bounds.Bottom <= tabs.ActualHeight, "Controls: scrolling brings Change PIN fully into view.", report);
                    CaptureWindow(stem + "-Controls-PIN.png");
                }
            }
            Assert(!busy && session == null && testHotkey == null && !File.Exists(DisplayTestJournal) && !File.Exists(DiscTestJournal), "Smoke mode did not launch, mount, change display mode or register a global key.", report);
            report.AppendLine("PASS: all assertions completed. PNGs are native WPF RenderTargetBitmap captures, not browser surrogates.");
            File.WriteAllText(stem + ".assertions.txt", report.ToString());
        }
        void CaptureWindow(string path)
        {
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
        static void Assert(bool condition, string description, StringBuilder report)
        {
            if (!condition) throw new InvalidOperationException("Smoke assertion failed: " + description);
            report.AppendLine("PASS: " + description);
        }
        static IEnumerable<DependencyObject> Descendants(DependencyObject node)
        {
            yield return node;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(node, i))) yield return child;
        }
    }
}
