using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;

namespace KidLauncher.E2E
{
    internal static class Program
    {
        const string Cooperative = "E2E cooperative game";
        const string Stubborn = "E2E stubborn game";
        const string Missing = "E2E missing game";
        const string Pin = "4931";
        static readonly List<Process> owned = new List<Process>();
        static StreamWriter report;
        static string app, output;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--test-game")
                return TestGame.Run(args[1], args.Contains("--ignore-close"));
            bool installer = args.Length == 4 && args[0] == "--installer";
            if (!installer && (args.Length < 2 || args.Length > 3)) { Console.Error.WriteLine("Usage: KidLauncher.E2E.exe <app directory> <evidence directory> [--negative-control], or --installer <setup.exe> <destination> <evidence directory>"); return 2; }
            app = Path.GetFullPath(args[installer ? 2 : 0]); output = Path.GetFullPath(args[installer ? 3 : 1]);
            Directory.CreateDirectory(output);
            using (report = new StreamWriter(Path.Combine(output, "results.txt")) { AutoFlush = true })
            {
                bool? originalAnimation = null;
                try
                {
                    Log("Testing normal packaged applications via out-of-process Windows UI Automation and SendInput.");
                    if (!installer)
                    {
                        originalAnimation = Desktop.ClientAreaAnimation;
                        Log("Original client-area animation setting: " + originalAnimation);
                        Desktop.ClientAreaAnimation = true;
                        Check(Desktop.ClientAreaAnimation, "Windows animations enabled for the visual journey");
                    }
                    if (installer) RunInstaller(Path.GetFullPath(args[1]));
                    else Run(args.Contains("--negative-control"));
                    Log("PASS: external packaged-app E2E journey completed.");
                    return 0;
                }
                catch (Exception error)
                {
                    Log("FAIL: " + error);
                    try { Desktop.Capture(Path.Combine(output, "failure.png")); DumpWindows(); }
                    catch (Exception capture) { Log("Failure evidence error: " + capture); }
                    return 1;
                }
                finally
                {
                    foreach (var process in owned.AsEnumerable().Reverse())
                    {
                        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
                        catch (Exception error) { Log("Fixture cleanup error: " + error.Message); }
                        process.Dispose();
                    }
                    if (originalAnimation.HasValue) Desktop.ClientAreaAnimation = originalAnimation.Value;
                }
            }
        }

        static void RunInstaller(string executable)
        {
            var process = Process.Start(new ProcessStartInfo(executable, "--install-dir " + Quote(app)) { UseShellExecute = true });
            owned.Add(process);
            var window = Window(process, "KidLauncher Setup");
            using (new DesktopRecording(Path.Combine(output, "frames")))
            {
                Desktop.Capture(Path.Combine(output, "installer-ready.png"));
                Move(Find(window, ControlType.Button, "Close"));
                Click(Find(window, ControlType.Button, "Install / Update"));
                Until(() => window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                    .Cast<AutomationElement>().Any(element => element.Current.Name.StartsWith("Installation complete.", StringComparison.Ordinal)),
                    "installer reports success");
                Check(File.Exists(Path.Combine(app, "KidLauncher.exe")), "installer writes the launcher");
                Desktop.Capture(Path.Combine(output, "installer-complete.png"));
                Click(Find(window, ControlType.Button, "Close"));
                Until(() => process.HasExited, "installer closes");
                Check(process.ExitCode == 0, "installer exits successfully");
            }
        }

        static void Run(bool negativeControl)
        {
            string fixture = Path.Combine(app, "E2EGame.exe"), missing = Path.Combine(app, "MissingGame.exe");
            File.Copy(typeof(Program).Assembly.Location, fixture);
            File.Copy(fixture, missing);
            string normalEvents = Path.Combine(output, "cooperative.events"), stubbornEvents = Path.Combine(output, "stubborn.events");
            var configurator = Start("KidLauncher.Configurator.exe");
            var settings = Window(configurator, "KidLauncher · Parent configurator");
            ((WindowPattern)settings.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);
            ConfigureGame(settings, Cooperative, fixture, normalEvents, false);
            ConfigureGame(settings, Stubborn, fixture, stubbornEvents, true);
            ConfigureGame(settings, Missing, missing, Path.Combine(output, "missing.events"), false);
            if (!negativeControl)
            {
                Invoke(settings, "Save configuration");
                Until(() => Games().Length == 3, "games saved before testing PIN-free exit");
                Close(configurator, settings);
                var noPinLauncher = Start("KidLauncher.exe");
                var noPinRoom = Window(noPinLauncher, "KidLauncher");
                Ready(noPinRoom, noPinLauncher);
                using (new DesktopRecording(Path.Combine(output, "pin-free-frames")))
                {
                    Desktop.Activate(Handle(noPinRoom));
                    Desktop.Chord(0x11, 0x12, 0x53);
                    var adult = Window(noPinLauncher, "Grown-up controls");
                    Desktop.Capture(Path.Combine(output, "00-pin-free-exit.png"));
                    Click(Find(adult, ControlType.Button, "Exit launcher to Windows"));
                    Until(() => noPinLauncher.HasExited, "launcher without a configured PIN exits through the adult menu");
                }
                configurator = Start("KidLauncher.Configurator.exe");
                settings = Window(configurator, "KidLauncher · Parent configurator");
                ((WindowPattern)settings.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Maximized);
            }
            ((SelectionItemPattern)Find(settings, ControlType.TabItem, "Controls").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            if (negativeControl)
            {
                Desktop.Activate(Handle(settings));
                Find(settings, ControlType.Edit, "Exit-game key").SetFocus();
                Desktop.Key(0x76); // Deliberately configure F7; the F6 journey must fail.
            }
            foreach (AutomationElement element in settings.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true)))
            {
                var scroll = (ScrollPattern)element.GetCurrentPattern(ScrollPattern.Pattern);
                if (!element.Current.IsOffscreen && scroll.Current.VerticallyScrollable)
                    scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
            }
            Password(settings, Find(settings, ControlType.Edit, "New PIN"), Pin);
            Password(settings, Find(settings, ControlType.Edit, "Repeat new PIN"), Pin);
            Invoke(settings, "Change PIN");
            Invoke(settings, "Save configuration");
            Until(() => File.Exists(Path.Combine(app, "games.json")) && Games().Length == 3 &&
                !string.IsNullOrEmpty((string)((Dictionary<string, object>)Configuration()["Controls"])["PinHash"]), "configuration saved by the GUI");
            Check((string)Configuration()["Language"] == "nl", "launcher defaults to Dutch");
            ((SelectionItemPattern)Find(settings, ControlType.TabItem, "General").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var language = Find(settings, ControlType.ComboBox, "Launcher language");
            var languages = (ExpandCollapsePattern)language.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            languages.Expand();
            ((SelectionItemPattern)Find(language, ControlType.ListItem, "English").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            languages.Collapse(); Invoke(settings, "Save configuration");
            Until(() => (string)Configuration()["Language"] == "en", "GUI persists English language selection");
            if (!negativeControl)
            {
                languages.Expand();
                ((SelectionItemPattern)Find(language, ControlType.ListItem, "Nederlands").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                languages.Collapse(); Invoke(settings, "Save configuration");
                Until(() => (string)Configuration()["Language"] == "nl", "GUI persists Dutch language selection");
            }
            var games = Games();
            Check((string)games[0]["Title"] == Cooperative && (string)games[1]["Title"] == Stubborn &&
                (string)games[2]["Title"] == Missing && (string)games[0]["ExecutablePath"] == fixture &&
                ((string)games[1]["Arguments"]).Contains("--ignore-close"), "GUI saves distinct game titles, executable and arguments");
            Check((string)((Dictionary<string, object>)Configuration()["Controls"])["PinHash"] != Pin,
                "GUI persists a PIN hash rather than the plaintext PIN");
            Check(Convert.ToInt32(((Dictionary<string, object>)Configuration()["Controls"])["ExitVirtualKey"]) == (negativeControl ? 0x76 : 0x75),
                "saved exit key matches the positive or deliberate negative control");
            Desktop.Capture(Path.Combine(output, "01-configured.png"));
            Close(configurator, settings);
            configurator = Start("KidLauncher.Configurator.exe");
            settings = Window(configurator, "KidLauncher · Parent configurator");
            Until(() => Value(settings, "Title") == Cooperative && Value(settings, "ExecutablePath") == fixture,
                "saved settings survive closing and reopening the configurator");
            Log("PASS: configuration and PIN created through the GUI, saved, and reloaded.");
            if (!negativeControl) TestInConfigurator(configurator, settings, normalEvents);
            Close(configurator, settings);

            var unrelated = Start("E2EGame.exe", "--test-game " + Quote(Path.Combine(output, "unrelated.events")));
            Until(() => File.Exists(Path.Combine(output, "unrelated.events")), "unrelated same-name control process starts");
            string originalMode = Desktop.DisplayMode();
            var launcher = Start("KidLauncher.exe");
            var room = Window(launcher, "KidLauncher");
            Ready(room, launcher);
            Check(FindNow(room, ControlType.Text, "Kies een avontuur") == null && FindNow(room, ControlType.Text, "SPELEN MAAR") == null &&
                FindNow(room, ControlType.Button, "Vorige pagina")?.Current.IsOffscreen != false, "single-page launcher has only game tiles, without headings or page controls");
            Desktop.MovePointer(4, 4);
            Desktop.ClientAreaAnimation = false;
            Check(!Desktop.ClientAreaAnimation, "Windows reports that client-area animations are disabled");
            Thread.Sleep(1500);
            Desktop.Capture(Path.Combine(output, "02-reduced-motion.png"));
            Thread.Sleep(500);
            Desktop.Capture(Path.Combine(output, "02-reduced-motion-later.png"));
            Check(File.ReadAllBytes(Path.Combine(output, "02-reduced-motion.png"))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(output, "02-reduced-motion-later.png"))),
                "reduced-motion preference leaves the launcher completely still");
            Desktop.ClientAreaAnimation = true;
            Thread.Sleep(400);
            Desktop.Capture(Path.Combine(output, "02-launcher-ready.png"));
            Thread.Sleep(400);
            Desktop.Capture(Path.Combine(output, "02-launcher-twinkle.png"));
            Check(!File.ReadAllBytes(Path.Combine(output, "02-launcher-ready.png"))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(output, "02-launcher-twinkle.png"))),
                "starfield motion resumes when Windows animations are enabled");
            using (new DesktopRecording(Path.Combine(output, "frames")))
            {
                var tile = Find(room, ControlType.Button, Cooperative);
                var neighbor = Find(room, ControlType.Button, Stubborn);
                var resting = tile.Current.BoundingRectangle;
                var neighborResting = neighbor.Current.BoundingRectangle;
                // Sample just outside the fixed button target. The decorative
                // tile grows into that margin without changing input geometry.
                Func<string, float> edgeBrightness = name =>
                {
                    var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
                    using (var image = new System.Drawing.Bitmap(Path.Combine(output, name)))
                        return image.GetPixel((int)resting.Left - 1 - screen.Left,
                            (int)(resting.Top + resting.Height / 2) - screen.Top).GetBrightness();
                };
                float restingEdge = edgeBrightness("02-launcher-ready.png");
                Move(tile); Thread.Sleep(400);
                Check(tile.Current.BoundingRectangle == resting && neighbor.Current.BoundingRectangle == neighborResting,
                    "hover keeps both the pointed tile's hit target and its neighbor fixed");
                var pointer = Desktop.CursorSize();
                Check(pointer.Width >= 48 && pointer.Height >= 64, "launcher uses a large native pointer over tiles");
                Desktop.Capture(Path.Combine(output, "02-launcher-hover.png"));
                Check(edgeBrightness("02-launcher-hover.png") > restingEdge + 0.15,
                    "hover visibly enlarges and highlights the decorative tile beyond its fixed hit target");
                Move(neighbor); Thread.Sleep(400);
                Desktop.Capture(Path.Combine(output, "02-neighbor-hover.png"));
                Check(Math.Abs(edgeBrightness("02-neighbor-hover.png") - restingEdge) < 0.02,
                    "tile decoration returns to its resting size after pointer leaves");
                Play(room, launcher, Cooperative, normalEvents, false, unrelated, originalMode);
                Play(room, launcher, Stubborn, stubbornEvents, true, unrelated, originalMode);
                File.Delete(missing);
                Click(Find(room, ControlType.Button, Missing));
                Find(room, ControlType.Text, "Dit avontuur kon niet starten.");
                Check(!File.Exists(Path.Combine(output, "missing.events")), "missing executable never starts a game");
                Desktop.Capture(Path.Combine(output, "05-launch-error.png"));
                File.Copy(fixture, missing);
                Click(Find(room, ControlType.Button, "Terug naar mijn spelletjes"));
                Ready(room, launcher);
                Check(!unrelated.HasExited, "unrelated same-name process survives both shutdown paths and launch failure");

                Desktop.Activate(Handle(room));
                Desktop.Chord(0x11, 0x12, 0x53);
                var pinWindow = Window(launcher, "Adult PIN");
                var pinBox = Wait(() => pinWindow.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.IsPasswordProperty, true)), "adult PIN password field");
                Password(pinWindow, pinBox, "1111");
                Invoke(pinWindow, "Unlock");
                Find(pinWindow, ControlType.Text, "That PIN did not match.");
                Check(!launcher.HasExited, "wrong PIN keeps the launcher running");
                Desktop.Capture(Path.Combine(output, "06-pin-denied.png"));
                Password(pinWindow, pinBox, Pin);
                Invoke(pinWindow, "Unlock");
                var adult = Window(launcher, "Grown-up controls");
                Desktop.Capture(Path.Combine(output, "07-pin-unlocked.png"));
                Invoke(adult, "Exit launcher to Windows");
                Until(() => launcher.HasExited, "PIN-authorized desktop exit");
                Check(Desktop.DisplayMode() == originalMode && !File.Exists(Journal), "adult exit leaves display unchanged and no pending recovery journal");
            }
            Log("PASS: wrong PIN rejected; saved PIN unlocks the adult route and permits a clean exit.");
        }

        static string Journal => Path.Combine(app, "Data", "display-session.json");
        static void ConfigureGame(AutomationElement settings, string title, string executable, string events, bool stubborn)
        {
            Invoke(settings, "＋ Add");
            Set(settings, "Title", title); Set(settings, "ExecutablePath", executable);
            Set(settings, "Arguments", "--test-game " + Quote(events) + (stubborn ? " --ignore-close" : ""));
            Set(settings, "WorkingDirectory", app);
        }

        static void TestInConfigurator(Process configurator, AutomationElement settings, string events)
        {
            var modes = Wait(() => settings.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)), "display mode dropdown");
            Desktop.Activate(Handle(settings)); modes.SetFocus();
            var dropdown = (ExpandCollapsePattern)modes.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            dropdown.Expand();
            Until(() => modes.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count > 1, "driver display choices are exposed");
            var choices = modes.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().Select(choice => choice.Current.Name).ToArray();
            Check(choices.All(name => !string.IsNullOrWhiteSpace(name)) && choices.Distinct().Count() == choices.Length,
                "display choices have distinct visible labels");
            Desktop.Capture(Path.Combine(output, "00-display-choices.png"));
            string selectedMode = choices.LastOrDefault(name => name.Contains(") 640 × 480 /")) ?? choices[1];
            ((SelectionItemPattern)Find(modes, ControlType.ListItem, selectedMode).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            dropdown.Collapse();
            modes.SetFocus();
            using (new DesktopRecording(Path.Combine(output, "resolution-frames")))
            {
                Move(modes);
                Desktop.Wheel(-120);
                Thread.Sleep(300);
                string afterWheel = ((SelectionPattern)modes.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection().Single().Current.Name;
                Log("Display choice before wheel: " + selectedMode + "; after wheel: " + afterWheel);
                Check(afterWheel == selectedMode, "scrolling over the closed resolution dropdown must not change the selected mode");
            }
            Invoke(settings, "Save configuration");
            string expectedDisplay = selectedMode.Substring(selectedMode.IndexOf(") ", StringComparison.Ordinal) + 2)
                .Split('/')[0].Trim().Replace(" × ", "x") + " ";
            Until(() => Games()[0]["Resolution"] != null, "selected resolution is saved");
            var resolution = (Dictionary<string, object>)Games()[0]["Resolution"];
            Check(expectedDisplay == resolution["Width"] + "x" + resolution["Height"] + " ", "saved mode matches the selected dimensions");
            Check(selectedMode.StartsWith("(" + resolution["VariantNumber"] + ") ", StringComparison.Ordinal) &&
                resolution["EnumerationIndex"] != null && resolution["NativeData"] != null,
                "saved mode preserves the selected variant, driver index and native data");
            string mode = Desktop.DisplayMode();
            Invoke(settings, "Test selected resolution (15 seconds, always restore)");
            var displayTest = Window(configurator, "Display test — automatic restore");
            Check(displayTest.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                .Any(text => text.Current.Name.StartsWith(selectedMode, StringComparison.Ordinal)),
                "display tester identifies the selected numbered mode");
            Desktop.Capture(Path.Combine(output, "00-numbered-display-test.png"));
            Invoke(displayTest, "Looks good — restore now");
            Until(() => Desktop.DisplayMode() == mode, "display tester restores the original mode");
            Until(() =>
            {
                string displayLog = File.ReadAllText(Path.Combine(app, "Data", "Logs", "launcher.log"));
                return displayLog.Contains("Display test requested: " + selectedMode + "; enumeration index=" + resolution["EnumerationIndex"]) &&
                    displayLog.Contains("native DEVMODE base64=") && displayLog.Contains("Display test actual:") &&
                    displayLog.Contains("Display test restored:");
            }, "display test logs selected native variant, actual mode and restoration");
            Invoke(settings, "▶ Test game (F6 exits)");
            Until(() => File.Exists(events) && File.ReadAllLines(events).Any(l => l.StartsWith("START ")), "configurator test game starts");
            int pid = int.Parse(File.ReadAllLines(events).Single(l => l.StartsWith("START ")).Substring(6));
            var game = Process.GetProcessById(pid); owned.Add(game);
            var window = Window(game, null);
            Until(() => Desktop.Foreground == Handle(window), "configurator test game receives foreground input");
            Check(File.ReadAllLines(events).Any(line => line.StartsWith("DISPLAY " + expectedDisplay, StringComparison.Ordinal)),
                "configurator launches the game at the selected resolution " + expectedDisplay);
            Desktop.Capture(Path.Combine(output, "00-selected-resolution-game.png"));
            Desktop.Key(0x75);
            Until(() => game.HasExited, "configurator test game exits after one F6");
            var observed = Window(configurator, "Observed processes — " + Cooperative);
            Check(!File.Exists(Journal) && Desktop.DisplayMode() == mode, "configurator completes recovery before showing observed processes");
            Check(File.ReadAllLines(events).Any(l => l.StartsWith("CLOSE_ACCEPTED ")), "configurator F6 requests graceful shutdown");
            var item = Wait(() => observed.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)), "observed game process candidate");
            Check(item.Current.Name.StartsWith("E2EGame "), "observed process list identifies the fixture game");
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Desktop.Capture(Path.Combine(output, "01-configurator-observed.png"));
            Invoke(observed, "Use selected process names");
            Invoke(settings, "Save configuration");
            Until(() => ((object[])Games()[0]["ProcessNames"]).Contains("E2EGame"), "GUI saves the observed process selection");
            File.Move(events, Path.Combine(output, "configurator.events"));
            Log("PASS: configurator one-press F6 shutdown, recovery and observed-process selection.");
        }

        static void Play(AutomationElement room, Process launcher, string title, string events, bool stubborn, Process unrelated, string mode)
        {
            Desktop.Activate(Handle(room));
            var tile = Find(room, ControlType.Button, title);
            Click(tile);
            // A second activation while loading must not launch another instance.
            Desktop.Key(0x0D);
            Until(() => File.Exists(events) && File.ReadAllLines(events).Any(l => l.StartsWith("START ")), title + " starts");
            int pid = int.Parse(File.ReadAllLines(events).Single(l => l.StartsWith("START ")).Substring(6));
            var game = Process.GetProcessById(pid); owned.Add(game);
            var window = Window(game, null);
            Until(() => Desktop.Foreground == Handle(window), title + " receives foreground input");
            Check(!Desktop.Visible(Handle(room)), "launcher hides while " + title + " is running");
            Until(() => File.Exists(Journal), "real game session persists recovery state");
            Desktop.Capture(Path.Combine(output, stubborn ? "04-stubborn-game.png" : "03-cooperative-game.png"));
            Check(!game.HasExited && !unrelated.HasExited, "game and unrelated control are alive before F6");
            Desktop.Key(0x75); // One physical F6 down/up, not WM_HOTKEY or a direct session call.
            Until(() => game.HasExited, title + " exits after a single F6", 20000);
            Ready(room, launcher);
            Until(() => !File.Exists(Journal), "recovery finishes before reusing the launcher");
            string[] lines = File.ReadAllLines(events);
            var requested = Games().Single(configured => (string)configured["Title"] == title)["Resolution"] as Dictionary<string, object>;
            if (requested != null)
                Check(lines.Any(line => line.StartsWith("DISPLAY " + requested["Width"] + "x" + requested["Height"] + " ", StringComparison.Ordinal)),
                    "installed launcher starts the game at its configured resolution");
            Check(lines.Count(l => l.StartsWith("START ")) == 1, "repeated activation starts exactly one game");
            Check(lines.Any(l => l.StartsWith("CLOSE_REQUEST ")), "F6 reaches the real game's close handler");
            Check(lines.Any(l => l.StartsWith("CLOSE_ACCEPTED ")) != stubborn,
                stubborn ? "force exit kills a game that refuses graceful close" : "cooperative game accepts graceful close");
            Check(!unrelated.HasExited && Desktop.DisplayMode() == mode, "shutdown preserves the unrelated process and original display mode");
            Log("PASS: " + (stubborn ? "forced" : "graceful") + " one-press F6 shutdown and foreground launcher return.");
        }

        static void Ready(AutomationElement room, Process launcher)
        {
            Until(() => !launcher.HasExited && Desktop.Visible(Handle(room)) &&
                Desktop.Foreground == Handle(room) && FindNow(room, ControlType.Button, Cooperative)?.Current.IsEnabled == true,
                "launcher is visible, foreground and ready");
        }
        static Dictionary<string, object> Configuration() => (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(app, "games.json")));
        static Dictionary<string, object>[] Games() => ((object[])Configuration()["Games"]).Cast<Dictionary<string, object>>().ToArray();
        static string Quote(string text) => "\"" + text + "\"";
        static Process Start(string executable, string arguments = "")
        {
            var process = Process.Start(new ProcessStartInfo(Path.Combine(app, executable), arguments) { WorkingDirectory = app, UseShellExecute = false, CreateNoWindow = true });
            owned.Add(process); return process;
        }
        static void Close(Process process, AutomationElement window)
        {
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            Until(() => process.HasExited, "configurator closes without an unsaved-changes prompt");
        }
        static AutomationElement Window(Process process, string title) => Wait(() =>
        {
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)))
            {
                if (title == null || window.Current.Name == title) return window;
                // WPF exposes owned modal windows below their owner in the UIA tree.
                var dialog = window.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                    new PropertyCondition(AutomationElement.NameProperty, title)));
                if (dialog != null) return dialog;
            }
            return null;
        }, "window for PID " + process.Id + " " + title);
        static AutomationElement FindNow(AutomationElement root, ControlType type, string name) => root.FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, type), new PropertyCondition(AutomationElement.NameProperty, name)));
        static AutomationElement Find(AutomationElement root, ControlType type, string name) => Wait(() => FindNow(root, type, name), "control " + name);
        static AutomationElement Field(AutomationElement root, string id) => Wait(() => root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id)), "editable field " + id);
        static void Set(AutomationElement root, string id, string value) => ((ValuePattern)Field(root, id).GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
        static string Value(AutomationElement root, string id) => ((ValuePattern)Field(root, id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
        static void Invoke(AutomationElement root, string name) => ((InvokePattern)Find(root, ControlType.Button, name).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        static IntPtr Handle(AutomationElement element) => new IntPtr(element.Current.NativeWindowHandle);
        static void Password(AutomationElement window, AutomationElement box, string text)
        {
            Desktop.Activate(Handle(window)); box.SetFocus(); Desktop.Type(text);
        }
        static void Move(AutomationElement element)
        {
            var point = element.GetClickablePoint(); Desktop.MovePointer((int)point.X, (int)point.Y);
        }
        static void Click(AutomationElement element)
        {
            var point = element.GetClickablePoint(); Desktop.Click((int)point.X, (int)point.Y);
        }
        static T Wait<T>(Func<T> probe, string description) where T : class
        {
            T result = null; Until(() => (result = probe()) != null, description); return result;
        }
        static void Until(Func<bool> probe, string description, int timeout = 20000)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeout)
            {
                try { if (probe()) { Log("OK: " + description); return; } }
                catch (ElementNotAvailableException) { }
                catch (COMException) { }
                catch (IOException) { }
                Thread.Sleep(100);
            }
            throw new TimeoutException(description);
        }
        static void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            Log("OK: " + description);
        }
        static void Log(string text) { Console.WriteLine(text); report.WriteLine(DateTime.UtcNow.ToString("O") + " " + text); }
        static void DumpWindows()
        {
            var text = new StringBuilder();
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                text.AppendLine(window.Current.ProcessId + " " + window.Current.Name);
                if (!owned.Any(p => p.Id == window.Current.ProcessId)) continue;
                foreach (AutomationElement node in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    text.AppendLine("  " + node.Current.ControlType.ProgrammaticName + " " + node.Current.AutomationId + " " + node.Current.Name + " offscreen=" + node.Current.IsOffscreen);
            }
            File.WriteAllText(Path.Combine(output, "windows.uia.txt"), text.ToString());
        }
    }
}
