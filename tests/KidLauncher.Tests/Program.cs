using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using KidLauncher.Core;

namespace KidLauncher.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Contains("--process-fixture")) { Thread.Sleep(30000); return 0; }
            var tests = new Action[] { RuntimeContract, ConfigurationRoundTrip, Validation, PinHash, ForcedExitAndChildHandoff,
                GracefulExit, LaunchFailureRestores, MountFailurePreventsLaunch, ExistingDiscIsPreserved,
                PreviousDiscRestored, RecoveryWaitsForGame, FailedRestorationIsRetried, ConcurrentSessionsRejected,
                DisabledForceExitKeepsResources, UnknownPriorImageDoesNotChangeDisc, WindowsProcessIdentity,
                FutureSchemaCannotBeOverwritten, PreviousDiscRecoveryIsIdempotent, RecoveryFindsUnrecordedChild,
                ReportingFailureDoesNotLockSession, UncertainStartRequiresAdultRecovery, SelectedProcessNamesLeaveHelpersAlone,
                WindowsShortcutLaunch };
            int failed = 0;
            foreach (var test in tests)
            {
                try { test(); Console.WriteLine("PASS " + test.Method.Name); }
                catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + e); }
            }
            Console.WriteLine((tests.Length - failed) + "/" + tests.Length + " tests passed");
            return failed == 0 ? 0 : 1;
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Throws(Action action)
        {
            bool thrown = false; try { action(); } catch { thrown = true; }
            Assert(thrown, "Expected an exception.");
        }
        private static void Complete(Task task)
        {
            if (!task.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Session did not finish.");
            task.GetAwaiter().GetResult();
        }
        private static void RuntimeContract()
        {
            Assert(IntPtr.Size == 4, "Release tests must execute as x86.");
            var target = (System.Runtime.Versioning.TargetFrameworkAttribute)Attribute.GetCustomAttribute(
                typeof(GameSession).Assembly, typeof(System.Runtime.Versioning.TargetFrameworkAttribute));
            Assert(target.FrameworkName == ".NETFramework,Version=v4.8", "Core no longer targets exactly .NET Framework 4.8.");
        }
        private static void ConfigurationRoundTrip()
        {
            using (var fixture = new Fixture())
            {
                var configuration = new LauncherConfiguration();
                Assert(configuration.Language == "nl", "New configurations must default to Dutch.");
                File.WriteAllText(fixture.Store.ConfigurationPath, "{\"SchemaVersion\":1,\"Games\":[]}");
                Assert(fixture.Store.Load().Language == "nl", "Existing configurations without a language must default to Dutch.");
                configuration.Language = "en";
                configuration.Games.Add(fixture.Game);
                configuration.Games[0].Title = "Putt-Putt & friends – speel!";
                fixture.Store.Save(configuration);
                Assert(fixture.Store.Load().Language == "en", "Selected English language was not persisted.");
                configuration.Games[0].Title = "Updated";
                fixture.Store.Save(configuration);
                Assert(fixture.Store.Load().Games[0].Title == "Updated", "Configuration round-trip lost data.");
                File.WriteAllText(fixture.Store.ConfigurationPath, "broken json");
                Assert(fixture.Store.Load().Games[0].Title == "Putt-Putt & friends – speel!", "Valid backup not recovered.");
                fixture.Store.Save(configuration);
                Assert(File.ReadAllText(fixture.Store.ConfigurationPath + ".bak").Contains("Putt-Putt"), "Corrupt primary replaced valid backup.");
                var cwd = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = Path.GetTempPath();
                    Assert(fixture.Store.ResolvePath("Assets\\tile.png") == Path.Combine(fixture.Root, "Assets", "tile.png"), "Relative path depends on current directory.");
                    Assert(fixture.Store.ResolvePath(fixture.Game.ExecutablePath) == fixture.Game.ExecutablePath, "Absolute path changed.");
                }
                finally { Environment.CurrentDirectory = cwd; }
            }
        }
        private static void Validation()
        {
            using (var f = new Fixture())
            {
                var c = new LauncherConfiguration { Games = new List<Game> { f.Game } };
                Assert(f.Store.Validate(c, true).Count == 0, "Valid game rejected.");
                c.SchemaVersion = 2;
                Assert(f.Store.Validate(c).Count > 0, "Future schema silently accepted.");
                c.SchemaVersion = 1; c.Appearance.Columns = 5;
                Assert(f.Store.Validate(c).Count > 0, "More than 12 tiles accepted.");
                c.Appearance.Columns = 4; f.Game.ExecutablePath = "missing.exe";
                Assert(f.Store.Validate(c, true).Any(e => e.Contains("executable")), "Missing executable accepted.");
                f.Game.Disc.Enabled = true; f.Game.Disc.ExpectedDriveLetter = "V:"; f.Game.Disc.ImagePath = "disc.iso";
                f.Game.Disc.ReadinessFile = "..\\Windows\\file";
                Assert(f.Store.Validate(c).Any(e => e.Contains("readiness")), "Drive traversal accepted.");
            }
        }
        private static void PinHash()
        {
            var a = new ControlSettings(); var b = new ControlSettings();
            PinSecurity.SetPin(a, "6914"); PinSecurity.SetPin(b, "6914");
            Assert(a.PinHash != b.PinHash && a.PinSalt != b.PinSalt, "PINs are not salted.");
            Assert(PinSecurity.VerifyPin(a, "6914") && !PinSecurity.VerifyPin(a, "6915"), "PIN verification incorrect.");
            Assert(!PinSecurity.VerifyPin(new ControlSettings(), ""), "Unset PIN allows adult access.");
        }
        private static void ForcedExitAndChildHandoff()
        {
            using (var f = new Fixture())
            {
                f.Processes.ChildHandoff = true;
                f.Game.StartupGraceMs = 300;
                var task = f.Session.RunAsync(f.Game, CancellationToken.None);
                Assert(f.Session.IsRunning, "Bootstrap exit returned immediately.");
                Complete(f.Session.RequestExitAsync()); Complete(task);
                Assert(f.Processes.ChildTerminated && !f.Processes.UnrelatedTerminated, "Force exit must kill only owned child.");
                Assert(f.Display.Current.Width == 1920 && f.Disc.Mounted == null, "Display/disc not restored after force exit.");
            }
        }
        private static void GracefulExit()
        {
            using (var f = new Fixture())
            {
                f.Processes.CloseWorks = true;
                var task = f.Session.RunAsync(f.Game, CancellationToken.None);
                Complete(f.Session.RequestExitAsync()); Complete(task);
                Assert(f.Processes.Closed && !f.Processes.Terminated, "Graceful exit unnecessarily forced.");
            }
        }
        private static void LaunchFailureRestores()
        {
            using (var f = new Fixture())
            {
                f.Processes.FailStart = true;
                Throws(() => Complete(f.Session.RunAsync(f.Game, CancellationToken.None)));
                Assert(f.Display.Current.Width == 1920, "Launch failure did not restore exact prior display.");
                Assert(!File.Exists(f.RecoveryPath), "Completed recovery state not cleared.");
            }
        }
        private static void MountFailurePreventsLaunch()
        {
            using (var f = new Fixture())
            {
                f.EnableDisc(); f.Disc.FailReady = true;
                Throws(() => Complete(f.Session.RunAsync(f.Game, CancellationToken.None)));
                Assert(!f.Processes.Started, "Game started before disc was ready.");
                Assert(f.Disc.Mounted == "" && f.Display.Current.Width == 1920, "Mount failure not cleaned up.");
            }
        }
        private static void ExistingDiscIsPreserved()
        {
            using (var f = new Fixture())
            {
                f.EnableDisc(); f.Disc.Mounted = f.Game.Disc.ImagePath; f.Processes.Alive = false;
                Complete(f.Session.RunAsync(f.Game, CancellationToken.None));
                Assert(f.Disc.Mounted == f.Game.Disc.ImagePath && f.Disc.Mounts == 0, "Already mounted disc was unnecessarily replaced.");
            }
        }
        private static void PreviousDiscRestored()
        {
            using (var f = new Fixture())
            {
                f.EnableDisc(); f.Game.Disc.RestorePreviousImage = true; f.Disc.Mounted = "previous.iso"; f.Processes.Alive = false;
                Complete(f.Session.RunAsync(f.Game, CancellationToken.None));
                Assert(f.Disc.Mounted == "previous.iso" && f.Disc.Mounts == 2, "Previous disc identity not restored.");
            }
        }
        private static void RecoveryWaitsForGame()
        {
            using (var f = new Fixture())
            {
                f.EnableDisc();
                f.WriteRecovery(true);
                Throws(() => Complete(f.Session.RecoverAsync()));
                Assert(File.Exists(f.RecoveryPath) && f.Display.Applied == 0 && f.Disc.Mounts == 0, "Recovery changed resources under a live game.");
                f.Processes.Alive = false;
                Complete(f.Session.RecoverAsync());
                Assert(!File.Exists(f.RecoveryPath), "Recovery did not converge after game exited.");
            }
        }
        private static void FailedRestorationIsRetried()
        {
            using (var f = new Fixture())
            {
                f.Processes.Alive = false; f.Display.FailApply = true; f.WriteRecovery(false);
                Throws(() => Complete(f.Session.RecoverAsync()));
                Assert(File.Exists(f.RecoveryPath), "Failed restoration lost recovery state.");
                f.Display.FailApply = false; Complete(f.Session.RecoverAsync());
                Assert(!File.Exists(f.RecoveryPath) && f.Display.Current.Width == 1920, "Recovery retry failed.");
            }
        }

        private static void ConcurrentSessionsRejected()
        {
            using (var f = new Fixture())
            {
                var active = f.Session.RunAsync(f.Game, CancellationToken.None);
                var other = new GameSession(f.Store, new LauncherConfiguration(), f.Display, f.Processes, f.Disc, new QuietLog());
                Throws(() => Complete(other.RunAsync(f.Game, CancellationToken.None)));
                Throws(() => Complete(other.RecoverAsync()));
                Assert(f.Display.Current.Width == 800 && f.Session.IsRunning, "Another session changed active game resources.");
                Complete(f.Session.RequestExitAsync()); Complete(active);
            }
        }
        private static void DisabledForceExitKeepsResources()
        {
            using (var f = new Fixture())
            {
                f.EnableDisc(); f.Game.AutomaticForceExit = false;
                var active = f.Session.RunAsync(f.Game, CancellationToken.None);
                var exit = f.Session.RequestExitAsync();
                Assert(!active.Wait(400) && !exit.IsCompleted, "Disabled force exit returned while the game is alive.");
                Assert(f.Disc.Mounted == f.Game.Disc.ImagePath && f.Display.Current.Width == 800 && File.Exists(f.RecoveryPath), "Live game resources were cleaned up.");
                f.Processes.Alive = false;
                Complete(active); Complete(exit);
                Assert(f.Disc.Mounted == "" && f.Display.Current.Width == 1920, "Manual exit did not trigger cleanup.");
            }
        }
        private static void UnknownPriorImageDoesNotChangeDisc()
        {
            using (var f = new Fixture())
            {
                f.EnableDisc(); f.Game.Disc.RestorePreviousImage = true;
                Throws(() => Complete(f.Session.RunAsync(f.Game, CancellationToken.None)));
                Assert(f.Disc.Mounted == null && f.Disc.Mounts == 0 && !f.Processes.Started,
                    "Unknown prior-image identity was replaced despite requested restoration.");
            }
        }

        private static void WindowsProcessIdentity()
        {
            var service = new WindowsProcessService();
            var owned = service.Start(new Game { ExecutablePath = typeof(Program).Assembly.Location, Arguments = "--process-fixture" }, AppDomain.CurrentDomain.BaseDirectory);
            try
            {
                var reused = new TrackedProcess { Id = owned.Id, Name = owned.Name, StartTimeUtcTicks = owned.StartTimeUtcTicks + 1 };
                service.RequestClose(reused); service.Terminate(reused);
                Assert(service.IsAlive(owned) && !service.IsAlive(reused), "PID without matching creation time was acted on.");
                service.Terminate(owned);
                Assert(SpinWait.SpinUntil(() => !service.IsAlive(owned), 3000), "Owned process did not terminate.");
            }
            finally { if (service.IsAlive(owned)) service.Terminate(owned); }
        }

        private static void WindowsShortcutLaunch()
        {
            using (var f = new Fixture())
            {
                string path = Path.Combine(f.Root, "Game shortcut.lnk");
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                object link = null;
                try
                {
                    link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                    var type = link.GetType();
                    type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe") });
                    type.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { "-n 30 127.0.0.1" });
                    type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                }
                finally { if (link != null) Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
                Assert(WindowsShortcutService.Read(path).Arguments == "-n 30 127.0.0.1", "Shortcut arguments were not retained.");
                var service = new WindowsProcessService();
                var owned = service.Start(new Game { ExecutablePath = path }, f.Root);
                try
                {
                    Thread.Sleep(300);
                    Assert(service.IsAlive(owned), "Shortcut target did not stay alive with its embedded arguments.");
                }
                finally { if (service.IsAlive(owned)) service.Terminate(owned); }
            }
        }

        private static void FutureSchemaCannotBeOverwritten()
        {
            using (var f = new Fixture())
            {
                var c = new LauncherConfiguration(); f.Store.Save(c); f.Store.Save(c);
                const string future = "{\"SchemaVersion\":2}";
                File.WriteAllText(f.Store.ConfigurationPath, future);
                Throws(() => f.Store.Load()); Throws(() => f.Store.Save(c));
                Assert(File.ReadAllText(f.Store.ConfigurationPath) == future, "Future schema was silently downgraded using the backup.");
            }
        }
        private static void PreviousDiscRecoveryIsIdempotent()
        {
            using (var f = new Fixture())
            {
                f.EnableDisc(); f.Game.Disc.RestorePreviousImage = true;
                f.Processes.Alive = false; f.Disc.Mounted = "different.iso";
                File.WriteAllText(f.RecoveryPath, new JavaScriptSerializer().Serialize(new RecoverySession {
                    OriginalDisplay = f.Display.Capture(), Disc = f.Game.Disc, DiscChanged = true, PreviousImage = "previous.iso"
                }));
                f.Display.FailApply = true;
                Throws(() => Complete(f.Session.RecoverAsync()));
                f.Display.FailApply = false; Complete(f.Session.RecoverAsync());
                Assert(f.Disc.Mounted == "previous.iso" && f.Disc.Mounts == 1 && !File.Exists(f.RecoveryPath), "Recovery must replace the managed drive once, then leave the already-restored disc alone on retry.");
            }
        }
        private static void RecoveryFindsUnrecordedChild()
        {
            using (var f = new Fixture())
            {
                f.Processes.ChildHandoff = true; f.WriteRecovery(true);
                Throws(() => Complete(f.Session.RecoverAsync()));
                Assert(f.Display.Applied == 0 && File.ReadAllText(f.RecoveryPath).Contains("102"), "Recovery missed a surviving child of the persisted bootstrap.");
                f.Processes.Alive = false; Complete(f.Session.RecoverAsync());
            }
        }
        private static void ReportingFailureDoesNotLockSession()
        {
            using (var f = new Fixture())
            {
                var session = new GameSession(f.Store, new LauncherConfiguration(), f.Display, f.Processes, f.Disc, new ThrowingLog());
                session.StatusChanged += message => { throw new IOException("subscriber failed"); };
                f.Processes.Alive = false; f.Display.FailApply = true;
                Throws(() => Complete(session.RunAsync(f.Game, CancellationToken.None)));
                Assert(!session.IsRunning && session.RequestExitAsync().IsCompleted, "Failed reporting stranded exit waiters.");
                f.Display.FailApply = false; Complete(session.RecoverAsync());
                Assert(!File.Exists(f.RecoveryPath), "Failed reporting stranded the session ownership lock.");
            }
        }

        private static void UncertainStartRequiresAdultRecovery()
        {
            using (var f = new Fixture())
            {
                f.EnableDisc(); f.Processes.FailIdentity = true;
                Throws(() => Complete(f.Session.RunAsync(f.Game, CancellationToken.None)));
                var journal = new JavaScriptSerializer().Deserialize<RecoverySession>(File.ReadAllText(f.RecoveryPath));
                Assert(f.Processes.Started && journal.ProcessStartPending && journal.Processes.Count == 0,
                    "An uncertain process creation was treated as a known launch failure.");
                Throws(() => Complete(f.Session.RecoverAsync()));
                Assert(f.Display.Current.Width == 800 && f.Disc.Mounted == f.Game.Disc.ImagePath,
                    "Recovery changed resources while an unrecorded game might still be running.");
                f.Processes.Alive = false;
                Complete(f.Session.RecoverAsync(confirmNoUnrecordedGameIsRunning: true));
                Assert(f.Display.Current.Width == 1920 && f.Disc.Mounted == "" && !File.Exists(f.RecoveryPath),
                    "Adult-confirmed recovery did not restore the interrupted session.");
            }
        }

        private static void SelectedProcessNamesLeaveHelpersAlone()
        {
            using (var f = new Fixture())
            {
                f.Processes.ChildHandoff = true; f.Processes.HelperAlive = true;
                f.Game.ProcessNames.Add("GAME");
                var active = f.Session.RunAsync(f.Game, CancellationToken.None);
                Complete(f.Session.RequestExitAsync()); Complete(active);
                Assert(f.Processes.ChildTerminated && f.Processes.HelperAlive && !f.Processes.UnrelatedTerminated,
                    "Selected game process names must not force-close an excluded helper.");
                Assert(f.Session.ObservedProcesses.Count == 3 && !File.Exists(f.RecoveryPath),
                    "Excluded helpers must remain observable without holding the completed session open.");
                File.WriteAllText(f.RecoveryPath, new JavaScriptSerializer().Serialize(new RecoverySession {
                    OriginalDisplay = f.Display.Capture(), ProcessNames = new List<string> { "game" },
                    Processes = new List<TrackedProcess> { FakeProcesses.Initial }
                }));
                Complete(f.Session.RecoverAsync());
                Assert(f.Processes.HelperAlive && !File.Exists(f.RecoveryPath), "Startup recovery ignored the saved game-process selection.");
            }
        }

        private sealed class Fixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "KidLauncherTests-" + Guid.NewGuid().ToString("N"));
            public ConfigurationStore Store;
            public FakeDisplay Display = new FakeDisplay();
            public FakeProcesses Processes = new FakeProcesses();
            public FakeDisc Disc = new FakeDisc();
            public Game Game;
            public GameSession Session;
            public string RecoveryPath => Path.Combine(Store.DataDirectory, "display-session.json");
            public Fixture()
            {
                Directory.CreateDirectory(Root); Store = new ConfigurationStore(Root);
                Game = new Game { Title = "Asymmetric test", ExecutablePath = typeof(Program).Assembly.Location,
                    Resolution = new DisplayMode { Width = 800, Height = 600, BitsPerPixel = 16, Frequency = 75 },
                    StartupGraceMs = 0, GracefulExitTimeoutMs = 0 };
                Session = new GameSession(Store, new LauncherConfiguration(), Display, Processes, Disc, new QuietLog());
            }
            public void EnableDisc()
            {
                var image = Path.Combine(Root, "game.iso"); File.WriteAllText(image, "test");
                Game.Disc = new DiscSettings { Enabled = true, ImagePath = image, ExpectedDriveLetter = "V:", VirtualDriveIndex = 2 };
            }
            public void WriteRecovery(bool live)
            {
                File.WriteAllText(RecoveryPath, new JavaScriptSerializer().Serialize(new RecoverySession {
                    OriginalDisplay = Display.Capture(), Disc = Game.Disc, DiscChanged = Game.Disc.Enabled,
                    Processes = live ? new List<TrackedProcess> { FakeProcesses.Initial } : new List<TrackedProcess>()
                }));
            }
            public void Dispose() { Directory.Delete(Root, true); }
        }
        private sealed class QuietLog : ILog { public void Write(string message) { } }
        private sealed class ThrowingLog : ILog { public void Write(string message) { throw new IOException("log unavailable"); } }
        private sealed class FakeDisplay : IDisplayService
        {
            public DisplayMode Current = new DisplayMode { Width = 1920, Height = 1080, BitsPerPixel = 32, Frequency = 60 };
            public bool FailApply; public int Applied;
            public DisplayMode Capture() => Current;
            public IReadOnlyList<DisplayMode> Enumerate() => new[] { Current };
            public void Apply(DisplayMode mode) { if (FailApply) throw new IOException("driver rejected"); Current = mode; Applied++; }
        }
        private sealed class FakeProcesses : IProcessService
        {
            public static readonly TrackedProcess Initial = new TrackedProcess { Id = 101, StartTimeUtcTicks = 1000, Name = "bootstrap" };
            private readonly TrackedProcess child = new TrackedProcess { Id = 102, StartTimeUtcTicks = 2000, Name = "game" };
            private readonly TrackedProcess helper = new TrackedProcess { Id = 103, StartTimeUtcTicks = 3000, Name = "helper" };
            public bool Alive = true, ChildHandoff, ChildTerminated, UnrelatedTerminated, CloseWorks, Closed, Terminated, Started, FailStart, FailIdentity;
            public bool HelperAlive;
            public IReadOnlyList<TrackedProcess> Snapshot() => new[] { Initial };
            public TrackedProcess Start(Game game, string directory) { if (FailStart) throw new IOException("start failed"); Started = true; if (FailIdentity) throw new UncertainProcessStartException("identity unavailable"); return Initial; }
            public IReadOnlyList<TrackedProcess> FindChildren(IReadOnlyList<TrackedProcess> parents) => HelperAlive ? new[] { child, helper } : ChildHandoff ? new[] { child } : new TrackedProcess[0];
            public bool IsAlive(TrackedProcess p) => p.Id == helper.Id ? HelperAlive : Alive && (!ChildHandoff || p.Id == child.Id);
            public void RequestClose(TrackedProcess p) { Closed = true; if (CloseWorks) Alive = false; }
            public void Terminate(TrackedProcess p)
            {
                if (p.Id != Initial.Id && p.Id != child.Id) UnrelatedTerminated = true;
                if (p.Id == child.Id) ChildTerminated = true;
                if (p.Id == helper.Id) HelperAlive = false;
                Terminated = true; Alive = false;
            }
        }
        private sealed class FakeDisc : IDiscService
        {
            public string Mounted; public bool FailReady; public int Mounts;
            public string GetMountedImage(DiscSettings disc) { ValidateMapping(disc); return Mounted; }
            public void ValidateMapping(DiscSettings disc) { if (disc.VirtualDriveIndex != 2) throw new Exception("Wrong drive touched"); }
            public Task MountAsync(DiscSettings disc, string image, CancellationToken token) { ValidateMapping(disc); Mounted = image; Mounts++; return Task.CompletedTask; }
            public Task UnmountAsync(DiscSettings disc, CancellationToken token)
            {
                ValidateMapping(disc);
                if (!string.IsNullOrEmpty(Mounted) && Mounted != disc.ImagePath) throw new IOException("Refusing to eject unowned media.");
                Mounted = ""; return Task.CompletedTask;
            }
            public Task WaitReadyAsync(DiscSettings disc, CancellationToken token) { if (FailReady) throw new TimeoutException("disc not ready"); return Task.CompletedTask; }
        }
    }
}
