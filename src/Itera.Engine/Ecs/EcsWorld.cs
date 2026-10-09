using System.Diagnostics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Diagnostics;
using Itera.Modding;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Ecs;

/// <summary>
/// Owns the Arch world and the two system groups that drive it. The fixed systems are run here phase by phase, as
/// <see cref="Group{T}"/> runs them, so each one's update is timed (<see cref="SystemTimes"/>).
/// </summary>
public sealed partial class EcsWorld : IDisposable
{
    private readonly ILogger<EcsWorld> _logger;
    private readonly SystemTimes _times;
    private readonly Group<FixedStepTime> _fixedSystems = new("Fixed");
    private readonly Group<FrameTime> _frameSystems = new("Frame");
    private readonly List<(IFixedUpdateSystem System, int Timing)> _fixed = [];
    private bool _disposed;

    public EcsWorld(ILogger<EcsWorld> logger, SystemTimes times)
    {
        _logger = logger;
        _times = times;
        World = World.Create();
    }

    /// <summary>The entity world. Systems query it; everything else goes through systems.</summary>
    public World World { get; }

    /// <summary>Adds systems and initialises them.</summary>
    public void Initialize(IEnumerable<IFixedUpdateSystem> fixedSystems, IEnumerable<IFrameUpdateSystem> frameSystems)
    {
        var fixedCount = 0;
        foreach (var system in fixedSystems)
        {
            _fixedSystems.Add(system);
            _fixed.Add((system, _times.Add(system.GetType().Name)));
            fixedCount++;
        }

        var frameCount = 0;
        foreach (var system in frameSystems)
        {
            _frameSystems.Add(system);
            frameCount++;
        }

        _fixedSystems.Initialize();
        _frameSystems.Initialize();
        LogInitialized(fixedCount, frameCount);
    }

    /// <summary>Runs the fixed group once: every system's before-update, update (each timed), and after-update, in order.</summary>
    public void FixedUpdate(in FixedStepTime step)
    {
        foreach (var (system, _) in _fixed)
        {
            system.BeforeUpdate(in step);
        }

        foreach (var (system, timing) in _fixed)
        {
            var start = Stopwatch.GetTimestamp();
            system.Update(in step);
            _times.Record(timing, Stopwatch.GetTimestamp() - start);
        }

        foreach (var (system, _) in _fixed)
        {
            system.AfterUpdate(in step);
        }
    }

    /// <summary>Runs the frame group once.</summary>
    public void Update(in FrameTime frame)
    {
        _frameSystems.BeforeUpdate(in frame);
        _frameSystems.Update(in frame);
        _frameSystems.AfterUpdate(in frame);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fixedSystems.Dispose();
        _frameSystems.Dispose();
        World.Destroy(World);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ECS world created: {FixedSystems} fixed system(s), {FrameSystems} frame system(s)"
    )]
    private partial void LogInitialized(int fixedSystems, int frameSystems);
}
