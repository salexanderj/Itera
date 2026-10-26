using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Diagnostics;

/// <summary>
/// Accumulates loop numbers and logs one summary line per interval: frames built and their times (the mean, the
/// longest, and the median, 95th, and 99th percentile, so a frame rate that swings shows as a spread, not only an
/// average; the first <see cref="GpuTimings.MostFrameTimes"/> of an interval), fixed steps,
/// dropped steps, bytes allocated on the main thread and on the render thread, garbage collections, heap size, and the
/// GPU time per frame (<see cref="GpuTimings"/>, with its percentiles too), with a Debug line per GPU pass and a Debug line of the fixed steps' CPU
/// time by part (<see cref="RecordStepParts"/>): the fixed group of systems, the large edits applied in the step, the voxel
/// colliders, the physics step, the loose bodies as voxels, and the step's tail (the mods, navigation's finished fields,
/// the session, and the autosave's capture), with the most steps one loop iteration ran (a step longer
/// than the step length makes the loop run several in the next frame), a Debug line of the slowest fixed systems
/// (<see cref="SystemTimes"/>), and a Debug line of the frame's CPU times (<see cref="FrameCpuTimings"/>): the wait from
/// the render thread's request until the main thread takes it, the main thread's build, and the render thread's
/// recording, present, and fence wait with acquire.
/// Dropped steps also produce a warning, at most one per interval. Steps and frames are counted separately: while
/// presentation blocks, no frames are built but steps continue, and the summary still appears. Call it from the thread
/// that runs the game loop: the main-thread allocation count is per thread.
/// </summary>
public sealed partial class FrameStats
{
    private readonly ILogger<FrameStats> _logger;
    private readonly double _intervalSeconds;
    private readonly GpuTimings? _gpu;
    private readonly SystemTimes? _systems;
    private readonly FrameCpuTimings? _cpu;
    private double _intervalStart;
    private int _frames;
    private double _sumMs;
    private double _maxMs;
    private int _fixedSteps;
    private int _droppedSteps;
    private long _allocatedAtStart;
    private long _renderAllocatedAtStart;
    private int _gen0;
    private int _gen1;
    private int _gen2;
    private int _mostSteps;
    private long _longestStep;
    private readonly long[] _partSum = new long[StepParts];
    private readonly long[] _partMax = new long[StepParts];
    private readonly float[] _frameTimes = new float[GpuTimings.MostFrameTimes];
    private readonly float[] _gpuFrameTimes = new float[GpuTimings.MostFrameTimes];
    private int _frameTimeCount;

    /// <summary>Parts of a fixed step timed by <see cref="RecordStepParts"/>.</summary>
    private const int StepParts = 6;

    /// <summary>How many of the slowest fixed systems the Debug line names.</summary>
    public const int SlowestSystems = 8;

    public FrameStats(
        IOptions<EngineOptions> options,
        ILogger<FrameStats> logger,
        GpuTimings gpu,
        SystemTimes systems,
        FrameCpuTimings cpu
    )
        : this(options.Value.Logging.StatsIntervalSeconds, logger, gpu, systems, cpu) { }

    /// <param name="intervalSeconds">Seconds between summaries. Zero or less disables them.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="gpu">GPU pass times, or null to leave them out.</param>
    /// <param name="systems">Each fixed system's time, or null to leave them out.</param>
    /// <param name="cpu">The frame's CPU times, or null to leave them out.</param>
    public FrameStats(
        double intervalSeconds,
        ILogger<FrameStats> logger,
        GpuTimings? gpu = null,
        SystemTimes? systems = null,
        FrameCpuTimings? cpu = null
    )
    {
        _intervalSeconds = intervalSeconds;
        _logger = logger;
        _gpu = gpu;
        _systems = systems;
        _cpu = cpu;
    }

    /// <summary>True when summaries are produced.</summary>
    public bool Enabled => _intervalSeconds > 0;

    /// <summary>The most recent summary, or null before the first interval completes.</summary>
    public FrameStatsSummary? LastSummary { get; private set; }

    /// <summary>Marks the start of the first interval.</summary>
    /// <param name="totalSeconds">Clock time now.</param>
    /// <param name="renderAllocatedBytes">The render thread's allocation total now.</param>
    public void Start(double totalSeconds, long renderAllocatedBytes)
    {
        _intervalStart = totalSeconds;
        _gen0 = GC.CollectionCount(0);
        _gen1 = GC.CollectionCount(1);
        _gen2 = GC.CollectionCount(2);
        _allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        _renderAllocatedAtStart = renderAllocatedBytes;
        Reset();
    }

    /// <summary>Fixed steps run since the game started. Main thread.</summary>
    public long TotalFixedSteps { get; private set; }

    /// <summary>Records one loop iteration's fixed steps.</summary>
    /// <param name="fixedSteps">Fixed steps run in the iteration.</param>
    /// <param name="droppedSteps">Fixed steps discarded in the iteration.</param>
    public void RecordSteps(int fixedSteps, int droppedSteps)
    {
        TotalFixedSteps += fixedSteps;
        _fixedSteps += fixedSteps;
        _droppedSteps += droppedSteps;
        _mostSteps = Math.Max(_mostSteps, fixedSteps);
    }

    /// <summary>
    /// Records one fixed step's CPU time by part, from <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> values
    /// taken before the fixed group of systems, before the large edits applied in the step, before the voxel colliders,
    /// before the physics step, before the loose bodies as voxels, before the step's tail (the mods, navigation, the
    /// session, and the saves), and after it.
    /// </summary>
    public void RecordStepParts(
        long start,
        long edits,
        long colliders,
        long physics,
        long bodyVoxels,
        long tail,
        long end
    )
    {
        Part(0, edits - start);
        Part(1, colliders - edits);
        Part(2, physics - colliders);
        Part(3, bodyVoxels - physics);
        Part(4, tail - bodyVoxels);
        Part(5, end - tail);
        _longestStep = Math.Max(_longestStep, end - start);
    }

    private void Part(int part, long ticks)
    {
        _partSum[part] += ticks;
        _partMax[part] = Math.Max(_partMax[part], ticks);
    }

    /// <summary>Records one built frame.</summary>
    /// <param name="frameSeconds">Time since the previous built frame, not clamped, so long gaps show at their real length.</param>
    public void RecordFrame(double frameSeconds)
    {
        var ms = frameSeconds * 1000.0;
        _frames++;
        _sumMs += ms;
        _maxMs = Math.Max(_maxMs, ms);
        if (_frameTimeCount < _frameTimes.Length)
        {
            _frameTimes[_frameTimeCount++] = (float)ms;
        }
    }

    /// <summary>
    /// The median, 95th, and 99th percentile of the first <paramref name="count"/> times in <paramref name="times"/>,
    /// which it sorts; zeros when there are none.
    /// </summary>
    internal static (double Median, double P95, double P99) Percentiles(float[] times, int count)
    {
        if (count == 0)
        {
            return default;
        }

        Array.Sort(times, 0, count);
        return (At(0.5), At(0.95), At(0.99));

        double At(double share) => times[Math.Clamp((int)Math.Ceiling(share * count) - 1, 0, count - 1)];
    }

    /// <summary>Logs a summary when the interval has elapsed. Call once per loop iteration.</summary>
    /// <param name="totalSeconds">Clock time now.</param>
    /// <param name="renderAllocatedBytes">The render thread's allocation total now.</param>
    public void Update(double totalSeconds, long renderAllocatedBytes)
    {
        if (!Enabled)
        {
            return;
        }

        var elapsed = totalSeconds - _intervalStart;
        if (elapsed < _intervalSeconds)
        {
            return;
        }

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _allocatedAtStart;
        var renderBytes = renderAllocatedBytes - _renderAllocatedAtStart;
        var gpuFrame = _gpu?.Take(GpuPass.Frame) ?? default;
        var gpuUpload = _gpu?.Take(GpuPass.Upload) ?? default;
        var gpuVoxels = _gpu?.Take(GpuPass.Voxels) ?? default;
        var gpuLines = _gpu?.Take(GpuPass.DebugLines) ?? default;
        var gpuModels = _gpu?.Take(GpuPass.Models) ?? default;
        var gpuMeshes = _gpu?.Take(GpuPass.Meshes) ?? default;
        var gpuSkinning = _gpu?.Take(GpuPass.Skinning) ?? default;
        var gpuUi = _gpu?.Take(GpuPass.Ui) ?? default;
        var gpuUpscale = _gpu?.Take(GpuPass.Upscale) ?? default;
        var gpuAntiAlias = _gpu?.Take(GpuPass.AntiAlias) ?? default;
        var gpuShadowMaps = _gpu?.Take(GpuPass.ShadowMaps) ?? default;
        var gpuParticleSimulate = _gpu?.Take(GpuPass.ParticleSimulate) ?? default;
        var gpuParticles = _gpu?.Take(GpuPass.Particles) ?? default;
        var gpuLoading = _gpu?.Take(GpuPass.Loading) ?? default;
        var gpuGame = _gpu?.Take(GpuPass.Game) ?? default;
        var gpuViews = _gpu?.Take(GpuPass.ImageViews) ?? default;
        var frame = Percentiles(_frameTimes, _frameTimeCount);
        var gpuCount = _gpu?.TakeFrameTimes(_gpuFrameTimes) ?? 0;
        var gpu = Percentiles(_gpuFrameTimes, gpuCount);

        var summary = new FrameStatsSummary(
            elapsed,
            _frames,
            _frames > 0 ? _sumMs / _frames : 0,
            _maxMs,
            _fixedSteps,
            _droppedSteps,
            allocatedBytes,
            PerFrame(allocatedBytes),
            renderBytes,
            PerFrame(renderBytes),
            gen0 - _gen0,
            gen1 - _gen1,
            gen2 - _gen2,
            heapMb,
            gpuFrame.AverageMs,
            gpuFrame.MaxMs,
            frame.Median,
            frame.P95,
            frame.P99,
            gpu.Median,
            gpu.P95,
            gpu.P99
        );

        LastSummary = summary;
        LogSummary(
            summary.Seconds,
            summary.Frames,
            summary.AverageMs,
            summary.MaxMs,
            summary.FixedSteps,
            summary.DroppedSteps,
            summary.AllocatedBytes / 1024.0,
            summary.BytesPerFrame,
            summary.RenderAllocatedBytes / 1024.0,
            summary.RenderBytesPerFrame,
            summary.Gen0,
            summary.Gen1,
            summary.Gen2,
            summary.HeapMb,
            summary.GpuAverageMs,
            summary.GpuMaxMs,
            summary.MedianMs,
            summary.P95Ms,
            summary.P99Ms,
            summary.GpuMedianMs,
            summary.GpuP95Ms,
            summary.GpuP99Ms
        );
        if (gpuFrame.Count > 0)
        {
            LogGpuPasses(
                summary.Seconds,
                gpuFrame.Count,
                gpuUpload.Count,
                gpuUpload.AverageMs,
                gpuUpload.MaxMs,
                gpuVoxels.AverageMs,
                gpuVoxels.MaxMs,
                gpuLines.Count,
                gpuLines.AverageMs,
                gpuLines.MaxMs,
                gpuModels.Count,
                gpuModels.AverageMs,
                gpuModels.MaxMs,
                gpuMeshes.Count,
                gpuMeshes.AverageMs,
                gpuMeshes.MaxMs,
                gpuSkinning.Count,
                gpuSkinning.AverageMs,
                gpuSkinning.MaxMs,
                gpuUi.Count,
                gpuUi.AverageMs,
                gpuUi.MaxMs,
                gpuAntiAlias.Count,
                gpuAntiAlias.AverageMs,
                gpuAntiAlias.MaxMs,
                gpuUpscale.Count,
                gpuUpscale.AverageMs,
                gpuUpscale.MaxMs,
                gpuParticleSimulate.Count,
                gpuParticleSimulate.AverageMs,
                gpuParticleSimulate.MaxMs,
                gpuParticles.AverageMs,
                gpuParticles.MaxMs,
                gpuLoading.Count,
                gpuLoading.AverageMs,
                gpuLoading.MaxMs,
                gpuShadowMaps.AverageMs,
                gpuGame.Count,
                gpuGame.AverageMs,
                gpuGame.MaxMs,
                gpuViews.Count,
                gpuViews.AverageMs,
                gpuViews.MaxMs
            );
        }

        // A game's passes one by one, those that ran.
        var gamePasses = _gpu?.GamePassNames ?? [];
        for (var i = 0; i < gamePasses.Count; i++)
        {
            var times = _gpu!.TakeGame(i);
            if (times.Count > 0)
            {
                LogGamePass(gamePasses[i], times.Count, times.AverageMs, times.MaxMs);
            }
        }

        if (_cpu is not null)
        {
            LogCpu(summary.Seconds, _cpu);
        }

        if (summary.FixedSteps > 0)
        {
            var steps = summary.FixedSteps;
            var longest = Ms(_longestStep);
            var systems = Ms(_partSum[0]) / steps;
            var systemsMax = Ms(_partMax[0]);
            var edits = Ms(_partSum[1]) / steps;
            var editsMax = Ms(_partMax[1]);
            var colliders = Ms(_partSum[2]) / steps;
            var collidersMax = Ms(_partMax[2]);
            var physics = Ms(_partSum[3]) / steps;
            var physicsMax = Ms(_partMax[3]);
            var bodyVoxels = Ms(_partSum[4]) / steps;
            var bodyVoxelsMax = Ms(_partMax[4]);
            var tail = Ms(_partSum[5]) / steps;
            var tailMax = Ms(_partMax[5]);
            LogStepParts(
                summary.Seconds,
                steps,
                longest,
                _mostSteps,
                systems,
                systemsMax,
                edits,
                editsMax,
                colliders,
                collidersMax,
                physics,
                physicsMax,
                bodyVoxels,
                bodyVoxelsMax,
                tail,
                tailMax
            );
            if (_systems is { Count: > 0 })
            {
                var slowest = _systems.TakeSlowest(SlowestSystems, steps);
                LogSlowestSystems(summary.Seconds, slowest);
            }
        }

        if (summary.DroppedSteps > 0)
        {
            LogDropped(summary.DroppedSteps, summary.Seconds);
        }

        _intervalStart = totalSeconds;
        _gen0 = gen0;
        _gen1 = gen1;
        _gen2 = gen2;
        // Read again after logging, so the summary's own allocations are not counted in the next interval.
        _allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        _renderAllocatedAtStart = renderAllocatedBytes;
        Reset();
    }

    /// <summary>Takes the frame's CPU times and logs their line when any frame was timed.</summary>
    private void LogCpu(double seconds, FrameCpuTimings cpu)
    {
        var wait = cpu.Take(FrameCpuInterval.RequestWait);
        var build = cpu.Take(FrameCpuInterval.Build);
        var record = cpu.Take(FrameCpuInterval.Record);
        var present = cpu.Take(FrameCpuInterval.Present);
        var acquire = cpu.Take(FrameCpuInterval.FenceAndAcquire);
        if (wait.Count + build.Count + record.Count + present.Count + acquire.Count == 0)
        {
            return;
        }

        LogFrameCpu(
            seconds,
            build.Count,
            wait.AverageMs,
            wait.MaxMs,
            build.AverageMs,
            build.MaxMs,
            record.AverageMs,
            record.MaxMs,
            present.AverageMs,
            present.MaxMs,
            acquire.AverageMs,
            acquire.MaxMs
        );
    }

    private double PerFrame(long bytes)
    {
        return _frames > 0 ? (double)bytes / _frames : 0;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private void Reset()
    {
        _mostSteps = 0;
        _longestStep = 0;
        Array.Clear(_partSum);
        Array.Clear(_partMax);
        _frames = 0;
        _frameTimeCount = 0;
        _sumMs = 0;
        _maxMs = 0;
        _fixedSteps = 0;
        _droppedSteps = 0;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Frame stats over {Seconds:F1} s: {Frames} frames, avg {AverageMs:F2} ms, max {MaxMs:F1} ms, {FixedSteps} fixed steps, {DroppedSteps} dropped, allocated {AllocatedKb:F1} KB main ({BytesPerFrame:F1} B/frame) and {RenderAllocatedKb:F1} KB render ({RenderBytesPerFrame:F1} B/frame), GC {Gen0}/{Gen1}/{Gen2}, heap {HeapMb:F1} MB, GPU avg {GpuAverageMs:F2} ms max {GpuMaxMs:F2} ms; frame p50 {MedianMs:F2} p95 {P95Ms:F2} p99 {P99Ms:F2} ms, GPU p50 {GpuMedianMs:F2} p95 {GpuP95Ms:F2} p99 {GpuP99Ms:F2} ms"
    )]
    private partial void LogSummary(
        double seconds,
        int frames,
        double averageMs,
        double maxMs,
        int fixedSteps,
        int droppedSteps,
        double allocatedKb,
        double bytesPerFrame,
        double renderAllocatedKb,
        double renderBytesPerFrame,
        int gen0,
        int gen1,
        int gen2,
        double heapMb,
        double gpuAverageMs,
        double gpuMaxMs,
        double medianMs,
        double p95Ms,
        double p99Ms,
        double gpuMedianMs,
        double gpuP95Ms,
        double gpuP99Ms
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "GPU passes over {Seconds:F1} s ({Frames} frames measured): upload in {UploadFrames} frames avg {UploadAverageMs:F3} ms max {UploadMaxMs:F3} ms, voxels avg {VoxelsAverageMs:F3} ms max {VoxelsMaxMs:F3} ms, debug lines in {LineFrames} frames avg {LinesAverageMs:F3} ms max {LinesMaxMs:F3} ms, models in {ModelFrames} frames avg {ModelsAverageMs:F3} ms max {ModelsMaxMs:F3} ms, mesh assets in {MeshFrames} frames avg {MeshesAverageMs:F3} ms max {MeshesMaxMs:F3} ms, skinning in {SkinFrames} frames avg {SkinningAverageMs:F3} ms max {SkinningMaxMs:F3} ms, UI in {UiFrames} frames avg {UiAverageMs:F3} ms max {UiMaxMs:F3} ms, anti-aliasing in {AntiAliasFrames} frames avg {AntiAliasAverageMs:F3} ms max {AntiAliasMaxMs:F3} ms, upscale in {UpscaleFrames} frames avg {UpscaleAverageMs:F3} ms max {UpscaleMaxMs:F3} ms, particles moved in {ParticleFrames} frames avg {ParticleSimulateAverageMs:F3} ms max {ParticleSimulateMaxMs:F3} ms and drawn avg {ParticlesAverageMs:F3} ms max {ParticlesMaxMs:F3} ms, loading screen in {LoadingFrames} frames avg {LoadingAverageMs:F3} ms max {LoadingMaxMs:F3} ms; of the world, shadow maps avg {ShadowMapsAverageMs:F3} ms; game passes in {GameFrames} frames avg {GameAverageMs:F3} ms max {GameMaxMs:F3} ms; image views in {ViewFrames} frames avg {ViewsAverageMs:F3} ms max {ViewsMaxMs:F3} ms"
    )]
    private partial void LogGpuPasses(
        double seconds,
        long frames,
        long uploadFrames,
        double uploadAverageMs,
        double uploadMaxMs,
        double voxelsAverageMs,
        double voxelsMaxMs,
        long lineFrames,
        double linesAverageMs,
        double linesMaxMs,
        long modelFrames,
        double modelsAverageMs,
        double modelsMaxMs,
        long meshFrames,
        double meshesAverageMs,
        double meshesMaxMs,
        long skinFrames,
        double skinningAverageMs,
        double skinningMaxMs,
        long uiFrames,
        double uiAverageMs,
        double uiMaxMs,
        long antiAliasFrames,
        double antiAliasAverageMs,
        double antiAliasMaxMs,
        long upscaleFrames,
        double upscaleAverageMs,
        double upscaleMaxMs,
        long particleFrames,
        double particleSimulateAverageMs,
        double particleSimulateMaxMs,
        double particlesAverageMs,
        double particlesMaxMs,
        long loadingFrames,
        double loadingAverageMs,
        double loadingMaxMs,
        double shadowMapsAverageMs,
        long gameFrames,
        double gameAverageMs,
        double gameMaxMs,
        long viewFrames,
        double viewsAverageMs,
        double viewsMaxMs
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "GPU game pass {Name} in {Frames} frames avg {AverageMs:F3} ms max {MaxMs:F3} ms"
    )]
    private partial void LogGamePass(string name, long frames, double averageMs, double maxMs);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Fixed steps over {Seconds:F1} s ({Steps} steps): longest {LongestMs:F2} ms, at most {MostSteps} in one loop iteration; systems avg {SystemsAverageMs:F3} ms max {SystemsMaxMs:F2} ms, large edits avg {EditsAverageMs:F3} ms max {EditsMaxMs:F2} ms, voxel colliders avg {CollidersAverageMs:F3} ms max {CollidersMaxMs:F2} ms, physics avg {PhysicsAverageMs:F3} ms max {PhysicsMaxMs:F2} ms, bodies as voxels avg {BodyVoxelsAverageMs:F3} ms max {BodyVoxelsMaxMs:F2} ms, mods, navigation, session, and saves avg {TailAverageMs:F3} ms max {TailMaxMs:F2} ms"
    )]
    private partial void LogStepParts(
        double seconds,
        int steps,
        double longestMs,
        int mostSteps,
        double systemsAverageMs,
        double systemsMaxMs,
        double editsAverageMs,
        double editsMaxMs,
        double collidersAverageMs,
        double collidersMaxMs,
        double physicsAverageMs,
        double physicsMaxMs,
        double bodyVoxelsAverageMs,
        double bodyVoxelsMaxMs,
        double tailAverageMs,
        double tailMaxMs
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Frame CPU over {Seconds:F1} s ({Frames} frames built): request wait avg {RequestWaitAverageMs:F3} ms max {RequestWaitMaxMs:F2} ms, build avg {BuildAverageMs:F3} ms max {BuildMaxMs:F2} ms, record avg {RecordAverageMs:F3} ms max {RecordMaxMs:F2} ms, present avg {PresentAverageMs:F3} ms max {PresentMaxMs:F2} ms, fence and acquire avg {AcquireAverageMs:F3} ms max {AcquireMaxMs:F2} ms"
    )]
    private partial void LogFrameCpu(
        double seconds,
        long frames,
        double requestWaitAverageMs,
        double requestWaitMaxMs,
        double buildAverageMs,
        double buildMaxMs,
        double recordAverageMs,
        double recordMaxMs,
        double presentAverageMs,
        double presentMaxMs,
        double acquireAverageMs,
        double acquireMaxMs
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Fixed systems over {Seconds:F1} s, slowest first, avg and max ms a step: {Slowest}"
    )]
    private partial void LogSlowestSystems(double seconds, string slowest);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Simulation fell behind: dropped {DroppedSteps} fixed step(s) in the last {Seconds:F1} s"
    )]
    private partial void LogDropped(int droppedSteps, double seconds);
}

/// <summary>One interval of loop health numbers.</summary>
/// <param name="Seconds">Interval length.</param>
/// <param name="Frames">Frames built.</param>
/// <param name="AverageMs">Mean time between built frames in milliseconds.</param>
/// <param name="MaxMs">Longest time between built frames in milliseconds.</param>
/// <param name="FixedSteps">Fixed steps run.</param>
/// <param name="DroppedSteps">Fixed steps discarded.</param>
/// <param name="AllocatedBytes">Managed bytes allocated on the main (loop) thread during the interval.</param>
/// <param name="BytesPerFrame"><paramref name="AllocatedBytes"/> divided by <paramref name="Frames"/>, or 0 without frames.</param>
/// <param name="RenderAllocatedBytes">Managed bytes allocated on the render thread during the interval.</param>
/// <param name="RenderBytesPerFrame"><paramref name="RenderAllocatedBytes"/> divided by <paramref name="Frames"/>, or 0 without frames.</param>
/// <param name="Gen0">Generation 0 collections.</param>
/// <param name="Gen1">Generation 1 collections.</param>
/// <param name="Gen2">Generation 2 collections.</param>
/// <param name="HeapMb">Managed heap size at the end of the interval, in megabytes.</param>
/// <param name="GpuAverageMs">Mean GPU time per frame in milliseconds, or 0 when not measured.</param>
/// <param name="GpuMaxMs">Longest GPU time of a frame in milliseconds.</param>
/// <param name="MedianMs">The median time between built frames in milliseconds (of the first
/// <see cref="GpuTimings.MostFrameTimes"/>).</param>
/// <param name="P95Ms">The 95th percentile time between built frames in milliseconds.</param>
/// <param name="P99Ms">The 99th percentile time between built frames in milliseconds.</param>
/// <param name="GpuMedianMs">The median GPU time of a frame in milliseconds, or 0 when not measured.</param>
/// <param name="GpuP95Ms">The 95th percentile GPU time of a frame in milliseconds.</param>
/// <param name="GpuP99Ms">The 99th percentile GPU time of a frame in milliseconds.</param>
public readonly record struct FrameStatsSummary(
    double Seconds,
    int Frames,
    double AverageMs,
    double MaxMs,
    int FixedSteps,
    int DroppedSteps,
    long AllocatedBytes,
    double BytesPerFrame,
    long RenderAllocatedBytes,
    double RenderBytesPerFrame,
    int Gen0,
    int Gen1,
    int Gen2,
    double HeapMb,
    double GpuAverageMs,
    double GpuMaxMs,
    double MedianMs = 0,
    double P95Ms = 0,
    double P99Ms = 0,
    double GpuMedianMs = 0,
    double GpuP95Ms = 0,
    double GpuP99Ms = 0
);
