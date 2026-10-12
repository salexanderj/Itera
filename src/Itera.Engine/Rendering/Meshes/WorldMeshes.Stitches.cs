using System.Diagnostics;
using Itera.Engine.Meshing;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The seams' stitched meshes (docs/rendering-redesign.md, phase 3, seams). The camera's pass names the sides of each
/// drawn column past which a column one level coarser is drawn (<see cref="LevelBalance.SidesOf"/>); a block along such
/// a side is drawn, by the camera and the near cascade, from its stitched mesh (<see cref="MeshStitch"/>, meshed by
/// <see cref="SurfaceNets.MeshTransition"/> from its voxels and the coarser level's block that holds it) once one is
/// made for those sides from the voxels as they are, and from its plain mesh until then. A block without one asks for it
/// (<see cref="RequestStitch"/>); the next frame's jobs read the nearest asked for after the plain ones
/// (<see cref="StartStitchJobs"/>). Stitched meshes count neither as pending nor towards a column's
/// <see cref="MeshColumn.MeshedOnce"/>: the sides follow the levels drawn, which follow the plain meshes. The far cascade
/// draws the plain meshes, its columns chosen apart; so does every pass under the debug view
/// <see cref="GpuData.ViewSwitches.Plain"/>.
/// </summary>
public sealed partial class WorldMeshes
{
    /// <summary>Blocks a frame may ask stitched meshes for.</summary>
    private const int MostStitchRequests = 1024;

    private readonly MeshColumn?[] _stitchColumns = new MeshColumn?[MostStitchRequests];
    private readonly int[] _stitchIndices = new int[MostStitchRequests];
    private readonly TransitionSides[] _stitchSides = new TransitionSides[MostStitchRequests];
    private readonly float[] _stitchDistances = new float[MostStitchRequests];
    private readonly int[] _stitchOrder = new int[MostStitchRequests];
    private int _stitchRequests;

    /// <summary>Blocks the last frame's camera pass drew from their stitched meshes. For tests and the stats line.</summary>
    internal int StitchedDraws { get; private set; }

    /// <summary>True when the last frame asked for no stitched mesh and none is in flight. For tests.</summary>
    internal bool StitchesSettled => _stitchRequests == 0 && _freeStitchJobs.Count == MostStitchJobs;

    /// <summary>Stitched meshes started since the run began. For tests.</summary>
    internal long StitchesStarted { get; private set; }

    /// <summary>Asks for block <paramref name="index"/>'s stitched mesh for <paramref name="sides"/>, nearest first next frame.</summary>
    private void RequestStitch(MeshColumn column, int index, TransitionSides sides, float distanceSquared)
    {
        if (_stitchRequests >= MostStitchRequests)
        {
            return;
        }

        var i = _stitchRequests++;
        _stitchColumns[i] = column;
        _stitchIndices[i] = index;
        _stitchSides[i] = sides;
        _stitchDistances[i] = distanceSquared;
    }

    private void ClearStitchRequests()
    {
        Array.Clear(_stitchColumns);
        _stitchRequests = 0;
    }

    /// <summary>
    /// Reads the blocks the last frame asked stitched meshes for, nearest first, each with the coarser level's block that
    /// holds it, and queues their jobs, until the time budget, the stitch jobs, or the requests run out.
    /// </summary>
    private void StartStitchJobs(VoxelWorld world, long deadline)
    {
        var count = _stitchRequests;
        _stitchRequests = 0;
        for (var i = 0; i < count; i++)
        {
            _stitchOrder[i] = i;
        }

        Array.Sort(_stitchDistances, _stitchOrder, 0, count);
        for (var k = 0; k < count; k++)
        {
            if (_freeStitchJobs.Count == 0 || Stopwatch.GetTimestamp() > deadline)
            {
                return;
            }

            var r = _stitchOrder[k];
            var column = _stitchColumns[r]!;
            var index = _stitchIndices[r];
            var sides = _stitchSides[r];
            if (
                !_columns.TryGetValue(column.Key, out var current)
                || !ReferenceEquals(current, column)
                || !column.Wanted
            )
            {
                continue;
            }

            ref var block = ref column.Blocks[index];
            ref var stitch = ref column.StitchesMade()[index];
            if (block.DrawnSlot < 0 || stitch.InFlight || stitch.IsCurrent(sides, block.Version))
            {
                continue;
            }

            var (x, y, z) = column.BlockAt(index);
            var level = column.Key.Level;
            var job = _freeStitchJobs.Pop();
            // Blocks 2c and 2c + 1 of a level lie in block c of the next coarser one, at 0 and 16 of its voxels.
            if (
                !world.TryReadBlock(level, x, y, z, job.Samples, job.Densities)
                || !world.TryReadBlock(level + 1, x >> 1, y >> 1, z >> 1, job.CoarseSamples, job.CoarseDensities)
            )
            {
                _freeStitchJobs.Push(job);
                continue;
            }

            const int Half = SurfaceNets.BlockSize / 2;
            job.Model = null;
            job.HasDensities = true;
            job.Windowed = false;
            job.Column = column.Key;
            job.Owner = column;
            job.Index = index;
            job.Version = block.Version;
            job.Sides = sides;
            job.CoarseOffset = new VoxelCoord((x & 1) * Half, (y & 1) * Half, (z & 1) * Half);
            job.CoarseVersion = stitch.CoarseChanges;
            stitch.InFlight = true;
            _workers!.Enqueue(job);
            _stats.StitchesStarted++;
            StitchesStarted++;
        }
    }

    /// <summary>
    /// Uploads a finished stitched mesh, or drops it when its block or the coarser block changed since. False only when
    /// the staging region has no room left this frame; the job then waits. A full heap or a failed mesher leaves the
    /// plain mesh drawn in its place.
    /// </summary>
    private bool TryFinishStitch(MeshJob job)
    {
        if (!_columns.TryGetValue(job.Column, out var column) || !ReferenceEquals(column, job.Owner))
        {
            job.Owner = null;
            _freeStitchJobs.Push(job);
            return true;
        }

        ref var block = ref column.Blocks[job.Index];
        ref var stitch = ref column.StitchesMade()[job.Index];
        if (job.Version == block.Version && job.CoarseVersion == stitch.CoarseChanges)
        {
            var usePlain = job.Triangles < 0;
            if (job.Triangles > 0)
            {
                var mesh = job.Mesh;
                var staged = TryStage(
                    mesh,
                    column,
                    out var offset,
                    out var words,
                    out var vertexBase,
                    out var firstIndex
                );
                if (staged == Staged.NoStagingRoom)
                {
                    return false;
                }

                FreeStitch(ref stitch);
                if (staged == Staged.Done)
                {
                    column.StagedUpload = Uploads;
                    stitch.RangeOffset = offset;
                    stitch.RangeWords = words;
                    stitch.VertexBase = vertexBase;
                    stitch.FirstIndex = firstIndex;
                    stitch.IndexCount = mesh.IndexCount;
                    _stats.StitchesUploaded++;
                    _stats.UploadedBytes += (ulong)words * sizeof(uint);
                }
                else
                {
                    // Tried again once ranges have been freed (RetryStarved).
                    usePlain = true;
                    _starved.Add((column, job.Index, true));
                }
            }
            else
            {
                FreeStitch(ref stitch);
            }

            stitch.Sides = job.Sides;
            stitch.BlockVersion = job.Version;
            stitch.CoarseVersion = job.CoarseVersion;
            stitch.UsePlain = usePlain;
        }

        stitch.InFlight = false;
        _freeStitchJobs.Push(job);
        return true;
    }

    /// <summary>Frees a stitched mesh's range; the block has none after (its plain mesh is drawn until another is made).</summary>
    private void FreeStitch(ref MeshStitch stitch)
    {
        if (stitch.RangeWords > 0)
        {
            FreeRange(stitch.RangeOffset, stitch.RangeWords);
        }

        stitch.RangeWords = 0;
        stitch.IndexCount = 0;
        stitch.Sides = TransitionSides.None;
        stitch.UsePlain = false;
    }

    /// <summary>Gives a job back to its pool.</summary>
    private void Release(MeshJob job) => (job.CoarseSamples is null ? _freeJobs : _freeStitchJobs).Push(job);

    /// <summary>What <see cref="TryStage"/> did with a mesh.</summary>
    private enum Staged
    {
        Done,
        NoStagingRoom,
        HeapFull,
    }
}
