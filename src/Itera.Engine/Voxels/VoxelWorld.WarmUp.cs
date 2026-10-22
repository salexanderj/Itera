using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using Itera.Engine.Matches;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Voxels;

// The edit warm-up: edits run on throwaway chunks at start until .NET compiles the edit code optimised.
public sealed partial class VoxelWorld
{
    /// <summary>
    /// Starts the edit warm-up on a thread of its own: a 4 m sphere set into and cleared out of throwaway chunks in the
    /// voxel memory, then freed, round after round until .NET runs optimised edit code (and the memory pools have grown)
    /// or 10 s have passed (<see cref="EditWarmUpStop"/>), so the first large edit in play does not run slowly (170 ms
    /// for a 4 m sphere against 35, Release, 2026-09-28). Once, after <see cref="Initialize(ulong)"/>; not at all once an
    /// earlier world of this run warmed the code up (<see cref="WarmUps.Edits"/>). Where .NET never compiles the code
    /// again (<see cref="s_codeRecompiled"/>), one round: it compiles the code, and more would only run out the time limit.
    /// </summary>
    public void StartEditWarmUp()
    {
        if (_warmUps is { Edits: true })
        {
            return;
        }

        var memory = Memory;
        var grid = Grid;
        _warmUp ??= Task.Factory.StartNew(
            () => WarmUpEdits(memory, grid),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
    }

    private void WarmUpEdits(VoxelMemory memory, VoxelGrid grid)
    {
        try
        {
            var start = Stopwatch.GetTimestamp();
            var changed = new int[VoxelConstants.BricksPerChunk];
            var stop = new EditWarmUpStop(s_codeRecompiled);
            // .NET compiles a method again, optimised, only after 30 calls counted once no new code has been compiled for
            // a moment, which the game's start-up puts off for seconds: 40 rounds at start left the first 4 m sphere in
            // play at 170 ms against 35 (Release, 2026-09-28). So the rounds go on, spaced out, until the code is fast.
            while (!_disposed)
            {
                var roundStart = Stopwatch.GetTimestamp();
                WarmUpRound(memory, grid, changed);
                var milliseconds = Stopwatch.GetElapsedTime(roundStart).TotalMilliseconds;
                var compiled = JitInfo.GetCompiledMethodCount();
                if (stop.Stop(milliseconds, compiled, Stopwatch.GetElapsedTime(start).TotalSeconds))
                {
                    break;
                }

                if (stop.Rounds >= WarmUpRounds)
                {
                    Thread.Sleep(WarmUpPauseMilliseconds);
                }
            }

            var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            LogWarmedUp(
                stop.Rounds,
                seconds,
                stop.FirstPassMilliseconds,
                stop.LastMilliseconds,
                stop.Reason ?? "the world ended"
            );
            // Done for the run, also at the time limit: a later world's warm-up of the same code would gain nothing.
            if (!_disposed && _warmUps is not null)
            {
                _warmUps.Edits = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Out of voxel memory, or shutting down: the first large edit compiles the code instead.
            LogWarmUpFailed(ex.Message);
        }
        catch (Exception ex)
        {
            // A bug: logged, and the task ends as the ones above do, so Dispose's wait never throws.
            LogWarmUpBroke(ex);
        }
    }

    /// <summary>
    /// A sphere set into air, holed, boxed, and bored through by a capsule (a projectile's jet; the first in play took 88 ms
    /// a step against 16 for the next, Release, 2026-10-02), filled and cleared of some materials (a tool's
    /// cubes; the first 5 m one in play took 60 ms against 14 to 27 for the next, counted as a removal counts it, Release,
    /// 2026-10-03), and stone holed and bored: mixed chunks of the world's <paramref name="grid"/> built, changed by the
    /// share each shape covers, counted, and freed. The sizes are 1 cm voxels' (a chunk is 1.28 m), so on a coarser grid
    /// the shapes cut a chunk of it as they would cut 1.28 m of it.
    /// </summary>
    private static void WarmUpRound(VoxelMemory memory, VoxelGrid grid, int[] changed)
    {
        Span<long> counts = stackalloc long[256];
        var key = new ChunkKey(0, 0, 0, 0);
        var scale = grid.BaseCentimetres;
        var centre = new VoxelCoord(64 * scale, 64 * scale, 64 * scale);
        var sphere = VoxelEdit.Sphere(centre, 200 * scale, VoxelEditOperation.Set, 1);
        var hole = VoxelEdit.Sphere(centre, 40 * scale, VoxelEditOperation.Clear);
        var box = VoxelEdit.Box(
            new VoxelCoord(10 * scale, 10 * scale, 10 * scale),
            new VoxelCoord(90 * scale, 90 * scale, 90 * scale),
            VoxelEditOperation.Set,
            2
        );
        // About a rocket's jet at 10 cm voxels: 30 cm round and 8.8 m long.
        var bore = VoxelEdit.Capsule(
            new VoxelCoord(30 * scale, 100 * scale, 40 * scale),
            new VoxelCoord(80 * scale, 40 * scale, 80 * scale),
            3 * scale,
            VoxelEditOperation.Clear
        );
        var piece = default(PieceView);
        var chunk = VoxelEditing.ApplyToWorldChunk(
            memory,
            grid,
            ChildState.Empty,
            key,
            in sphere,
            in piece,
            changed,
            out _
        );
        chunk = VoxelEditing.ApplyToWorldChunk(memory, grid, chunk, key, in hole, in piece, changed, out _);
        chunk = VoxelEditing.ApplyToWorldChunk(memory, grid, chunk, key, in box, in piece, changed, out _);
        chunk = VoxelEditing.ApplyToWorldChunk(memory, grid, chunk, key, in bore, in piece, changed, out _);
        var cube = VoxelEdit.Box(
            new VoxelCoord(0, 60 * scale, 0),
            new VoxelCoord(128 * scale, 68 * scale, 100 * scale),
            VoxelEditOperation.Fill,
            3
        );
        var removal = VoxelEdit.MaterialsBox(
            new VoxelCoord(-20 * scale, 0, -20 * scale),
            new VoxelCoord(140 * scale, 100 * scale, 90 * scale),
            s_warmSet
        );
        // A paint keeps to sample points, as a lift and a piece put back do.
        var paint = VoxelEdit.Sphere(centre, 30 * scale, VoxelEditOperation.Paint, 3);
        chunk = VoxelEditing.ApplyToWorldChunk(memory, grid, chunk, key, in paint, in piece, changed, out _);
        VoxelEditing.CountInWorldChunk(memory, grid, chunk, key, in removal, counts);
        chunk = VoxelEditing.ApplyToWorldChunk(memory, grid, chunk, key, in cube, in piece, changed, out _);
        chunk = VoxelEditing.ApplyToWorldChunk(memory, grid, chunk, key, in removal, in piece, changed, out _);
        ChunkTree.Free(memory, chunk, ChunkTree.RootDepth);
        chunk = VoxelEditing.ApplyToWorldChunk(
            memory,
            grid,
            ChildState.Uniform(1),
            key,
            in hole,
            in piece,
            changed,
            out _
        );
        chunk = VoxelEditing.ApplyToWorldChunk(memory, grid, chunk, key, in bore, in piece, changed, out _);
        ChunkTree.Free(memory, chunk, ChunkTree.RootDepth);
    }

    /// <summary>
    /// False where .NET never compiles the engine's code again, so the warm-up's later rounds gain nothing: a build whose
    /// assembly turns the JIT's optimiser off (<see cref="DebuggableAttribute"/>), and a run with tiered compilation off
    /// (the test projects), which optimises each method at its first call.
    /// </summary>
    private static readonly bool s_codeRecompiled =
        !(typeof(VoxelWorld).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled ?? false)
        && !(AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out var tiered) && !tiered);

    /// <summary>The materials the warm-up's clear of some materials takes.</summary>
    private static readonly byte[] s_warmSet = [1, 3];

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Voxels: edit warm-up ran {Rounds} round(s) over {Seconds:F1} s (first-pass round {FirstPassMilliseconds:F1} ms, last {LastMilliseconds:F1} ms); stopped: {Reason}"
    )]
    private partial void LogWarmedUp(
        int rounds,
        double seconds,
        double firstPassMilliseconds,
        double lastMilliseconds,
        string reason
    );

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voxels: the edit warm-up stopped: {Reason}")]
    private partial void LogWarmUpFailed(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Voxels: the edit warm-up failed")]
    private partial void LogWarmUpBroke(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Voxels: the edit warm-up did not stop within {Seconds} s; the voxel memory is left to the OS"
    )]
    private partial void LogWarmUpStuck(double seconds);
}
