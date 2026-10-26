using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The sky field on the GPU (<see cref="SkyField"/>): a 3D texture that <see cref="MeshPass"/> owns. A build starts when
/// there is none, when the view is 32 m from its middle or 16 m above or below it, or 2 s after an edit or a coarse
/// column loaded inside its box; its voxels are
/// read on the main thread, at most <see cref="SkyReadMilliseconds"/> a frame over as many frames as that takes, its
/// flood runs on a worker, and once done it is staged whole in one frame (when the staging region has room), copied into
/// the texture before that frame's draws (the copy waits for the frames before to stop reading it), and drawn from that
/// frame. An edit made while the read goes on may show in some of its blocks and not others until the next build, 2 s
/// later. The read's time counts in the 2 ms a frame that the upload's block reads may take
/// (<see cref="ReadMillisecondsPerFrame"/>), so mesh jobs start in what is left of it, about 1 ms, while a read goes on.
/// <para>
/// A read going on when its match ends is dropped. A flood still running then finishes on its own and is dropped; none
/// starts before it is done. A world whose coarsest level is finer than <see cref="SkyLeastLevel"/> has no field (its
/// read would copy too many blocks): everything sees the whole sky. A failed flood is tried again after
/// <see cref="SkyChangeSeconds"/>; a failed read ends the match, as any exception in the upload does.
/// </para>
/// </summary>
public sealed unsafe partial class WorldMeshes
{
    /// <summary>A field is built again when the view is this far from its middle, in metres.</summary>
    private const float SkyMoveMetres = 32;

    /// <summary>
    /// A field is built again when the view is this far above or below its middle, in metres: the box is 80 m tall at the
    /// game's 80 cm cells (64 m at 64 cm), so its top stays at least about 11 m above the view (the box's middle is up to 4.5 m below the view when built), and
    /// a roof above a climbing view is not taken for open sky.
    /// </summary>
    private const float SkyClimbMetres = 16;

    /// <summary>The main thread reads the field's blocks for at most this long a frame, in milliseconds.</summary>
    private const int SkyReadMilliseconds = 1;

    private static readonly long SkyReadBudgetTicks = Stopwatch.Frequency * SkyReadMilliseconds / 1000;

    /// <summary>After a change to the world, a field is built again at most this often, in seconds.</summary>
    private const double SkyChangeSeconds = 2;

    /// <summary>
    /// The coarsest level a world needs for a sky field: its cells are then every other voxel at most (with a level
    /// four times finer the main thread's read would copy about 9,000 blocks).
    /// </summary>
    private int SkyLeastLevel => SkyField.LevelFor(_grid) - 1;

    private readonly SkyField _skyField = new();
    private Task? _skyBuild;
    private bool _skyBuildDropped;
    private bool _skyReading;
    private long _skyReadSpentTicks;
    private long _skyReadLongestTicks;
    private int _skyReadFrames;
    private bool _skyDrawn;
    private bool _skyUploaded;
    private ulong _skyUploadOffset;

    // The drawn field's corner and middle, absolute (written local to each frame's origin).
    private WorldPoint _skyCorner;
    private WorldPoint _skyCentre;
    private bool _skyChanged;

    // True once a read has started this match: until then any edit or coarse column loaded counts as a change.
    private bool _skyBoxKnown;
    private double _skyBuiltAt = double.NegativeInfinity;
    private bool _skyFaultReported;

    /// <summary>True once a field is drawn from. For tests.</summary>
    internal bool HasSkyField => _skyDrawn;

    /// <summary>True when a change to the world waits for the next build. For tests.</summary>
    internal bool SkyChangePending => _skyChanged;

    /// <summary>Starts a match with no field.</summary>
    private void BeginSky()
    {
        EndSky();
        _skyChanged = false;
        _skyBoxKnown = false;
        _skyBuiltAt = double.NegativeInfinity;
    }

    /// <summary>Stops drawing the field; a read going on is dropped, and a flood still running when it is done.</summary>
    private void EndSky()
    {
        _skyReading = false;
        _skyBuildDropped = _skyBuild is not null;
        _skyDrawn = false;
        _skyUploaded = false;
    }

    /// <summary>
    /// Stages a finished build and draws from it, goes on with a read, or starts a build when one is due. Main thread, in
    /// the frame's upload.
    /// </summary>
    private void UpdateSky(VoxelWorld world, in WorldPoint viewPosition, double nowSeconds)
    {
        _skyUploaded = false;
        if (_skyBuild is { IsCompleted: true } build)
        {
            if (_skyBuildDropped)
            {
                _skyBuildDropped = false;
                _skyBuild = null;
            }
            else if (build.IsFaulted)
            {
                if (!_skyFaultReported)
                {
                    _skyFaultReported = true;
                    LogSkyFailed(build.Exception!.InnerException ?? build.Exception);
                }

                // Tried again after SkyChangeSeconds, not every frame.
                _skyBuild = null;
            }
            else if (_writer.HasRoom((ulong)SkyField.Cells, 0))
            {
                var target = _writer.Stage((ulong)SkyField.Cells, out _skyUploadOffset);
                _skyField.Light.AsSpan().CopyTo(new Span<byte>(target, SkyField.Cells));
                _skyUploaded = true;
                LogSkyBuilt(
                    _skyReadSpentTicks * 1000.0 / Stopwatch.Frequency,
                    _skyReadFrames,
                    _skyReadLongestTicks * 1000.0 / Stopwatch.Frequency,
                    _skyField.UniformBlocks,
                    _skyField.StoredBlocks,
                    _skyField.FloodTicks * 1000.0 / Stopwatch.Frequency
                );
                _skyDrawn = true;
                _skyCorner = _skyField.Corner;
                _skyCentre = _skyField.Centre;
                _skyBuild = null;
            }
        }

        if (_skyReading)
        {
            ReadSky(world, Stopwatch.GetTimestamp());
            return;
        }

        if (_skyBuild is not null || _coarsestLevel < SkyLeastLevel)
        {
            return;
        }

        var due =
            (!_skyDrawn && nowSeconds - _skyBuiltAt > SkyChangeSeconds)
            || viewPosition.DistanceTo(_skyCentre) > SkyMoveMetres
            || Math.Abs(viewPosition.Y - _skyCentre.Y) > SkyClimbMetres
            || (_skyChanged && nowSeconds - _skyBuiltAt > SkyChangeSeconds);
        if (!due)
        {
            return;
        }

        _skyChanged = false;
        _skyBuiltAt = nowSeconds;
        _skyReadSpentTicks = 0;
        _skyReadLongestTicks = 0;
        _skyReadFrames = 0;
        var start = Stopwatch.GetTimestamp();
        _skyField.BeginRead(world, viewPosition, _coarsestLevel);
        _skyBoxKnown = true;
        _skyReading = true;
        ReadSky(world, start);
    }

    /// <summary>
    /// Reads the field's blocks until <see cref="SkyReadMilliseconds"/> after <paramref name="start"/>, and starts its
    /// flood on a worker once all are read.
    /// </summary>
    private void ReadSky(VoxelWorld world, long start)
    {
        var done = _skyField.ContinueRead(world, start + SkyReadBudgetTicks);
        var spent = Stopwatch.GetTimestamp() - start;
        _skyReadSpentTicks += spent;
        _skyReadLongestTicks = Math.Max(_skyReadLongestTicks, spent);
        _skyReadFrames++;
        if (done)
        {
            _skyReading = false;
            _skyBuild = Task.Run(_skyField.Flood);
        }
    }

    /// <summary>
    /// True when <paramref name="change"/> can change what the sky field sees: an edit, or a column of the field's read
    /// level loaded, whose column, chunk, or brick meets the box of the last read begun. Before the match's first read
    /// begins, any of them counts. Allocates nothing.
    /// </summary>
    private bool ChangesSky(in VoxelChange change)
    {
        var column = change.Column;
        var loaded =
            change.Kind == VoxelChangeKind.ColumnLoaded
            && column.Level == Math.Min(SkyField.LevelFor(_grid), _coarsestLevel);
        if (!loaded && change.Kind is not (VoxelChangeKind.BrickChanged or VoxelChangeKind.ChunkChanged))
        {
            return false;
        }

        if (!_skyBoxKnown)
        {
            return true;
        }

        // The change's column, chunk, or brick, in absolute centimetres.
        var size = _grid.ColumnCentimetres(column.Level);
        var x = column.X * size;
        var z = column.Z * size;
        if (loaded)
        {
            return _skyField.Overlaps(x, long.MinValue, z, x + size, long.MaxValue, z + size);
        }

        var y = column.Chunk(change.Layer, _grid).Y * size;
        if (change.Kind == VoxelChangeKind.BrickChanged)
        {
            size /= VoxelConstants.BricksPerChunkEdge;
            x += (change.Brick & 15) * size;
            y += ((change.Brick >> 4) & 15) * size;
            z += (change.Brick >> 8) * size;
        }

        return _skyField.Overlaps(x, y, z, x + size, y + size, z + size);
    }

    /// <summary>The sky field's part of the frame's lighting record.</summary>
    private void WriteSky(ref GpuData.MeshLighting lighting)
    {
        if (!_skyDrawn)
        {
            return;
        }

        var origin = WorldPoint.FromCentimetres(_origin);
        lighting.SkyCorner = new Vector3(
            (float)(_skyCorner.X - origin.X),
            (float)(_skyCorner.Y - origin.Y),
            (float)(_skyCorner.Z - origin.Z)
        );
        lighting.SkyCellMetres = _skyField.CellMetres;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Sky field: built round the view, read in {ReadMs:F1} ms over {Frames} frame(s) on the main thread (at most {LongestMs:F2} ms a frame; {Uniform} blocks of one material or air, {Stored} read), flooded in {FloodMs:F1} ms on a worker"
    )]
    private partial void LogSkyBuilt(
        double readMs,
        int frames,
        double longestMs,
        int uniform,
        int stored,
        double floodMs
    );

    [LoggerMessage(Level = LogLevel.Error, Message = "Sky field: a build failed; the last one stays drawn")]
    private partial void LogSkyFailed(Exception exception);
}
