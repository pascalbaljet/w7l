using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KidLauncher.Core
{
    public sealed class GameSession
    {
        private readonly ConfigurationStore store;
        private readonly LauncherConfiguration configuration;
        private readonly IDisplayService display;
        private readonly IProcessService processes;
        private readonly IDiscService discs;
        private readonly ILog log;
        private readonly RecoveryStore recovery;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly List<TrackedProcess> observed = new List<TrackedProcess>();
        private volatile bool exitRequested;
        private TaskCompletionSource<bool> finished;
        public bool IsRunning { get; private set; }
        public IReadOnlyList<TrackedProcess> ObservedProcesses => observed.ToArray();
        public event Action GameStarting;
        public event Action<string> StatusChanged;

        public GameSession(ConfigurationStore store, LauncherConfiguration config, IDisplayService display,
            IProcessService processes, IDiscService discs, ILog log)
        {
            this.store = store; configuration = config; this.display = display;
            this.processes = processes; this.discs = discs; this.log = log;
            recovery = new RecoveryStore(store.DataDirectory);
        }

        private void Report(string message)
        {
            try { log.Write(message); }
            catch (Exception error) { Trace.WriteLine("Session log failed: " + error); }
            try { StatusChanged?.Invoke(message); }
            catch (Exception error) { Trace.WriteLine("Session status subscriber failed: " + error); }
        }

        public async Task RecoverAsync(bool confirmNoUnrecordedGameIsRunning = false)
        {
            if (!await gate.WaitAsync(0)) throw new InvalidOperationException("A game session is already active.");
            FileStream ownership = null;
            try
            {
                ownership = AcquireOwnership();
                var session = recovery.Load();
                if (session != null && session.ProcessStartPending && confirmNoUnrecordedGameIsRunning)
                {
                    // Only the adult diagnostics route may provide this confirmation.
                    // Known identities are still checked by RestoreAsync below.
                    session.ProcessStartPending = false;
                    recovery.Save(session);
                    Report("Adult confirmed that no unrecorded game is running.");
                }
                if (session != null) await RestoreAsync(session);
            }
            finally { ownership?.Dispose(); gate.Release(); }
        }

        private FileStream AcquireOwnership() => new FileStream(Path.Combine(store.DataDirectory, "session.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        public Task RequestExitAsync()
        {
            if (!IsRunning) return Task.CompletedTask;
            exitRequested = true;
            return finished.Task;
        }

        public async Task RunAsync(Game game, CancellationToken cancellationToken)
        {
            if (!await gate.WaitAsync(0)) throw new InvalidOperationException("A game session is already active.");
            IsRunning = true;
            exitRequested = false;
            finished = new TaskCompletionSource<bool>();
            observed.Clear();
            RecoverySession session = null;
            Exception failure = null;
            FileStream ownership = null;
            try
            {
                ownership = AcquireOwnership();
                if (recovery.Load() != null) throw new InvalidOperationException("Recover the unfinished session before launching another game.");
                if (File.Exists(Path.Combine(store.DataDirectory, "configurator-display-test.json")) ||
                    File.Exists(Path.Combine(store.DataDirectory, "configurator-disc-test.json")))
                    throw new InvalidOperationException("An interrupted configurator test needs recovery. Open the configurator and retry recovery in Diagnostics before launching a game.");
                var errors = store.Validate(new LauncherConfiguration { Games = new List<Game> { game }, Appearance = configuration.Appearance, Controls = configuration.Controls }, true);
                if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
                cancellationToken.ThrowIfCancellationRequested();
                Report("Preparing game: " + game.Title);
                var disc = new DiscSettings {
                    Enabled = game.Disc.Enabled, ImagePath = store.ResolvePath(game.Disc.ImagePath),
                    VirtualDriveIndex = game.Disc.VirtualDriveIndex, ExpectedDriveLetter = game.Disc.ExpectedDriveLetter,
                    ExpectedVolumeLabel = game.Disc.ExpectedVolumeLabel, ReadinessFile = game.Disc.ReadinessFile,
                    MountTimeoutMs = game.Disc.MountTimeoutMs, UnmountAfterExit = game.Disc.UnmountAfterExit
                };
                session = new RecoverySession { OriginalDisplay = display.Capture(), Disc = disc, ProcessNames = new List<string>(game.ProcessNames) };
                recovery.Save(session);
                if (disc.Enabled)
                {
                    discs.ValidateMapping(disc);
                    var image = disc.ImagePath;
                    // The configured drive is dedicated to KidLauncher. Persist intent
                    // before replacing any existing disc, including a partial mount.
                    session.DiscChanged = true;
                    recovery.Save(session);
                    await discs.MountAsync(disc, image, cancellationToken);
                    await discs.WaitReadyAsync(disc, cancellationToken);
                    Report("Disc ready: " + image);
                }
                if (game.Resolution != null)
                {
                    Report("Applying resolution: " + game.Resolution);
                    display.Apply(game.Resolution);
                }
                Report("Display immediately before game launch: " + display.Capture());
                cancellationToken.ThrowIfCancellationRequested();
                GameStarting?.Invoke();
                session.ProcessStartPending = true;
                recovery.Save(session);
                TrackedProcess initial;
                try { initial = processes.Start(game, store.ApplicationDirectory); }
                catch (UncertainProcessStartException) { throw; }
                catch
                {
                    // A failed CreateProcess/UAC cancellation did not start a game.
                    // Uncertain post-start failures use the exception above instead.
                    session.ProcessStartPending = false;
                    recovery.Save(session);
                    throw;
                }
                observed.Add(initial);
                session.Processes.Add(initial);
                session.ProcessStartPending = false;
                recovery.Save(session);
                Report("Process started: " + initial.Name + " PID " + initial.Id);
                Report("Exit settings: key=" + configuration.Controls.ExitVirtualKey + "; automatic force exit=" + game.AutomaticForceExit +
                    "; force timeout=" + game.GracefulExitTimeoutMs + "ms; requested elevation=" + game.RunAsAdministrator);
                var lastDisplay = display.Capture();
                Report("Display after process start: " + lastDisplay);
                var startup = Stopwatch.StartNew();
                long nextDisplayCheck = 500;
                long nextDisplaySnapshot = 5000;
                Stopwatch exitTimer = null;
                var closeRequested = new HashSet<string>();
                while (true)
                {
                    foreach (var child in processes.FindChildren(observed.ToArray()))
                    {
                        if (observed.Any(p => p.Id == child.Id && p.StartTimeUtcTicks == child.StartTimeUtcTicks)) continue;
                        observed.Add(child);
                        Report("Detected child process: " + child.Name + " PID " + child.Id);
                        if (session.ProcessNames.Count > 0 && !session.ProcessNames.Contains(child.Name, StringComparer.OrdinalIgnoreCase)) continue;
                        session.Processes.Add(child);
                        recovery.Save(session);
                    }
                    var alive = session.Processes.Where(processes.IsAlive).ToList();
                    if (alive.Count == 0 && startup.ElapsedMilliseconds >= game.StartupGraceMs) break;
                    if (game.Resolution != null && startup.ElapsedMilliseconds >= nextDisplayCheck)
                    {
                        try
                        {
                            var currentDisplay = display.Capture();
                            if (currentDisplay.Width != lastDisplay.Width || currentDisplay.Height != lastDisplay.Height ||
                                currentDisplay.BitsPerPixel != lastDisplay.BitsPerPixel || currentDisplay.Frequency != lastDisplay.Frequency)
                            {
                                Report("Display changed during game at " + startup.ElapsedMilliseconds + "ms: " + lastDisplay +
                                    " -> " + currentDisplay + "; configured mode=" + game.Resolution);
                                lastDisplay = currentDisplay;
                            }
                            if (startup.ElapsedMilliseconds >= nextDisplaySnapshot)
                            {
                                Report("Display snapshot at " + startup.ElapsedMilliseconds + "ms (" +
                                    nextDisplaySnapshot / 1000 + "s checkpoint): " + currentDisplay +
                                    "; configured mode=" + game.Resolution);
                                nextDisplaySnapshot = nextDisplaySnapshot == 5000 ? 15000 : long.MaxValue;
                            }
                            nextDisplayCheck = startup.ElapsedMilliseconds + 500;
                        }
                        catch (Exception error)
                        {
                            // Diagnostics must not interrupt a running game or try
                            // to restore the desktop while it still owns the screen.
                            Report("Display monitoring unavailable: " + error.Message);
                            nextDisplayCheck = long.MaxValue;
                        }
                    }
                    if (exitRequested || cancellationToken.IsCancellationRequested)
                    {
                        if (exitTimer == null) { exitTimer = Stopwatch.StartNew(); Report("Exit requested; attempting graceful shutdown."); }
                        foreach (var process in alive)
                        {
                            var key = process.Id + ":" + process.StartTimeUtcTicks;
                            if (closeRequested.Add(key)) processes.RequestClose(process);
                        }
                        if (game.AutomaticForceExit && exitTimer.ElapsedMilliseconds >= game.GracefulExitTimeoutMs)
                        {
                            foreach (var process in alive)
                            {
                                if (!processes.IsAlive(process)) continue;
                                Report("Forcefully terminating tracked process: " + process.Name + " PID " + process.Id);
                                processes.Terminate(process);
                            }
                        }
                    }
                    await Task.Delay(150);
                }
                Report("All tracked game processes exited.");
            }
            catch (Exception e) { failure = e; Report("Game session failed: " + e); }
            finally
            {
                try
                {
                    try { if (session != null) await RestoreAsync(session); }
                    catch (Exception restore)
                    {
                        Report("Recovery incomplete: " + restore);
                        failure = failure == null ? restore : new AggregateException(failure, restore);
                    }
                }
                finally
                {
                    IsRunning = false;
                    ownership?.Dispose();
                    gate.Release();
                    finished.TrySetResult(true);
                }
            }
            if (failure != null) throw new InvalidOperationException("The game session could not finish safely. Check Diagnostics and the log before retrying.", failure);
        }

        private async Task RestoreAsync(RecoverySession session)
        {
            if (session.ProcessStartPending)
                throw new InvalidOperationException("The launcher stopped during process creation, before a game identity was recorded. Close any game manually, then use the configurator's adult confirmation recovery. Display and disc state have been preserved.");
            // A bootstrapper can spawn a game and exit between journal updates.
            // Reconstruct its surviving descendants before deciding cleanup is safe.
            bool discovered = false;
            foreach (var child in processes.FindChildren(session.Processes))
            {
                if (session.Processes.Any(p => p.Id == child.Id && p.StartTimeUtcTicks == child.StartTimeUtcTicks)) continue;
                if (session.ProcessNames.Count > 0 && !session.ProcessNames.Contains(child.Name, StringComparer.OrdinalIgnoreCase)) continue;
                session.Processes.Add(child); discovered = true;
            }
            if (discovered) recovery.Save(session);
            // An access-denied liveness check must throw, not be treated as a dead game.
            if (session.Processes.Any(processes.IsAlive))
                throw new InvalidOperationException("A tracked game is still running. Close it before restoring the display or disc. Recovery state has been preserved.");
            var failures = new List<Exception>();
            try { display.Apply(session.OriginalDisplay); Report("Original display mode restored."); }
            catch (Exception e) { failures.Add(e); }
            try
            {
                if (session.DiscChanged && session.Disc != null && session.Disc.Enabled && session.Disc.UnmountAfterExit)
                {
                    await discs.UnmountAsync(session.Disc, CancellationToken.None);
                    Report("Dedicated disc drive unmounted.");
                }
            }
            catch (Exception e) { failures.Add(e); }
            if (failures.Count > 0) throw new AggregateException("Recovery remains pending.", failures);
            recovery.Clear();
        }
    }
}
