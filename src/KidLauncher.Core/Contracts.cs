using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KidLauncher.Core
{
    public interface IDisplayService
    {
        DisplayMode Capture();
        IReadOnlyList<DisplayMode> Enumerate();
        void Apply(DisplayMode mode);
    }

    public interface IProcessService
    {
        IReadOnlyList<TrackedProcess> Snapshot();
        TrackedProcess Start(Game game, string applicationDirectory);
        IReadOnlyList<TrackedProcess> FindChildren(IReadOnlyList<TrackedProcess> parents);
        bool IsAlive(TrackedProcess process);
        void RequestClose(TrackedProcess process);
        void Terminate(TrackedProcess process);
    }

    public interface IDiscService
    {
        string GetMountedImage(DiscSettings disc);
        void ValidateMapping(DiscSettings disc);
        Task MountAsync(DiscSettings disc, string imagePath, CancellationToken cancellationToken);
        Task UnmountAsync(DiscSettings disc, CancellationToken cancellationToken);
        Task WaitReadyAsync(DiscSettings disc, CancellationToken cancellationToken);
    }

    public interface ILog
    {
        void Write(string message);
    }
}
