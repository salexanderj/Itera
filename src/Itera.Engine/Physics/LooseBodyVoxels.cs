using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// Loose bodies as level-0 voxels, for <see cref="VoxelCharacterMover"/>: characters are blocked by them and stand and
/// step on them by the ground's rules. A body's piece holds centimetres (a lift reads it a centimetre at a time), so each
/// world voxel is the piece's voxel under its centre. <see cref="Refresh"/> takes each body's pose after the physics step. When a query
/// reaches a world brick (8^3 voxels) that a body may cover, the brick is filled once for the step: each voxel whose
/// centre, turned back into the body's piece, lands on a blocking voxel of the piece is set: about the voxels a piece edit
/// would place, as the turn is in floats about the unrounded pivot, where the edit rounds. Masks are kept for the step in a fixed table. With no body near a query, it costs a bounds test per
/// body. It also holds the solid models that are not loose bodies (<see cref="Solids"/>), and every query reads both.
/// Main thread; allocates nothing.
/// </summary>
public sealed class LooseBodyVoxels
{
    private const int CacheBits = 13;
    private const int CacheSize = 1 << CacheBits;
    private const int Probes = 8;

    private readonly LooseBodies _bodies;
    private readonly MaterialSet _passable;
    private readonly LoosePose[] _poses;
    private readonly Vector3[] _axes;

    // The edge of the mover's cells (the world's level-0 voxels, or Engine:Physics:CollisionCentimetres without terrain)
    // in centimetres, the unit of a piece's voxels and of the poses' pivots.
    private readonly float _cell;
    private readonly long[] _keys = new long[CacheSize];
    private readonly int[] _stamps = new int[CacheSize];
    private readonly ulong[] _masks = new ulong[CacheSize * VoxelConstants.BrickMaskWords];
    private int _count;
    private int _stamp = 1;

    /// <summary>The bodies in the cells of a match's voxel world (its level-0 voxels).</summary>
    public LooseBodyVoxels(LooseBodies bodies, VoxelWorld voxels, IVoxelPassability passability)
        : this(bodies, passability, voxels.Grid.BaseCentimetres) { }

    /// <summary>The bodies in cells of <paramref name="cellCentimetres"/>, the character mover's.</summary>
    public LooseBodyVoxels(LooseBodies bodies, IVoxelPassability passability, int cellCentimetres)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCentimetres, 1);
        _bodies = bodies;
        _passable = MaterialSet.Passable(passability);
        _poses = new LoosePose[bodies.MaxBodies];
        _axes = new Vector3[3 * bodies.MaxBodies];
        Solids = new SolidModelVoxels(cellCentimetres);
        _cell = cellCentimetres;
    }

    /// <summary>Loose bodies taken at the last <see cref="Refresh"/>, and the solid models (<see cref="Solids"/>).</summary>
    public int Count => _count + Solids.Count;

    /// <summary>Solid models that are not loose bodies, such as a head floating above the ground, placed by their owners each step.</summary>
    public SolidModelVoxels Solids { get; }

    /// <summary>
    /// Takes every loose body's pose and forgets the bricks filled before. Call after each physics step. A body made or
    /// settled since keeps its place until the next refresh; its piece's voxels do not change.
    /// </summary>
    public void Refresh()
    {
        _count = _bodies.CopyPoses(_poses);
        for (var i = 0; i < _count; i++)
        {
            // Where the world's axes point in the piece: the piece's turn undone.
            var undo = Quaternion.Conjugate(_poses[i].Orientation);
            _axes[3 * i] = Vector3.Transform(Vector3.UnitX, undo);
            _axes[3 * i + 1] = Vector3.Transform(Vector3.UnitY, undo);
            _axes[3 * i + 2] = Vector3.Transform(Vector3.UnitZ, undo);
        }

        _stamp++;
    }

    /// <summary>
    /// The top (one above the highest voxel) of the highest body voxel from y0 to y1 (inclusive) under
    /// <paramref name="footprint"/>, or <see cref="int.MinValue"/> when there is none.
    /// </summary>
    internal int HighestTop(in CircleFootprint footprint, int y0, int y1)
    {
        Span<int> table = stackalloc int[FootprintRows.TableLength(in footprint)];
        var visitor = new VoxelWorld.HighestVisitor(new FootprintRows(in footprint, table), y1);
        Walk(footprint.MinX, y0, footprint.MinZ, footprint.MaxX, y1, footprint.MaxZ, ref visitor);
        return Solids.Count == 0 ? visitor.Top : Math.Max(visitor.Top, Solids.HighestTop(in footprint, y0, y1));
    }

    /// <summary>
    /// The bottom of the lowest body voxel from y0 to y1 (inclusive) under <paramref name="footprint"/>, or
    /// <see cref="int.MaxValue"/> when there is none.
    /// </summary>
    internal int LowestBottom(in CircleFootprint footprint, int y0, int y1)
    {
        Span<int> table = stackalloc int[FootprintRows.TableLength(in footprint)];
        var visitor = new VoxelWorld.LowestVisitor(new FootprintRows(in footprint, table), y0);
        Walk(footprint.MinX, y0, footprint.MinZ, footprint.MaxX, y1, footprint.MaxZ, ref visitor);
        return Solids.Count == 0 ? visitor.Bottom : Math.Min(visitor.Bottom, Solids.LowestBottom(in footprint, y0, y1));
    }

    /// <summary>
    /// The cells of a window that hold a body voxel anywhere from y0 to y1, as <see cref="VoxelWorld.BlockedCells"/>
    /// gives them for the world. <paramref name="rows"/> is cleared first.
    /// </summary>
    internal void BlockedCells(int windowX, int windowZ, int width, int y0, int y1, Span<UInt128> rows)
    {
        rows.Clear();
        var visitor = new VoxelWorld.CellsVisitor(rows, windowX, windowZ);
        Walk(windowX, y0, windowZ, windowX + width - 1, y1, windowZ + rows.Length - 1, ref visitor);
        if (Solids.Count > 0)
        {
            Solids.BlockedCells(windowX, windowZ, width, y0, y1, rows);
        }
    }

    /// <summary>True when some body's (or solid's) bounds reach the box from (x0, y0, z0) to (x1, y1, z1).</summary>
    internal bool Reaches(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        if (Solids.Count > 0 && Solids.Reaches(x0, y0, z0, x1, y1, z1))
        {
            return true;
        }

        for (var i = 0; i < _count; i++)
        {
            if (Bounds(i, out var min, out var max) && Overlap(min, max, x0, y0, z0, x1, y1, z1))
            {
                return true;
            }
        }

        return false;
    }

    private void Walk<TVisitor>(int x0, int y0, int z0, int x1, int y1, int z1, ref TVisitor visitor)
        where TVisitor : struct, IBlockingVisitor, allows ref struct
    {
        if (_count == 0)
        {
            return;
        }

        Span<ulong> mask = stackalloc ulong[VoxelConstants.BrickMaskWords];
        for (var i = 0; i < _count; i++)
        {
            if (!Bounds(i, out var min, out var max) || !Overlap(min, max, x0, y0, z0, x1, y1, z1))
            {
                continue;
            }

            int fromX = Math.Max(x0, min.X) >> 3,
                fromY = Math.Max(y0, min.Y) >> 3,
                fromZ = Math.Max(z0, min.Z) >> 3,
                toX = Math.Min(x1, max.X) >> 3,
                toY = Math.Min(y1, max.Y) >> 3,
                toZ = Math.Min(z1, max.Z) >> 3;
            for (var bz = fromZ; bz <= toZ; bz++)
            {
                for (var by = fromY; by <= toY; by++)
                {
                    for (var bx = fromX; bx <= toX; bx++)
                    {
                        if (!Mask(i, bx, by, bz, mask))
                        {
                            continue;
                        }

                        int ox = bx << 3,
                            oy = by << 3,
                            oz = bz << 3;
                        if (
                            VoxelWorld.Clip(
                                mask,
                                Math.Max(x0 - ox, 0),
                                Math.Max(y0 - oy, 0),
                                Math.Max(z0 - oz, 0),
                                Math.Min(x1 - ox, 7),
                                Math.Min(y1 - oy, 7),
                                Math.Min(z1 - oz, 7)
                            )
                        )
                        {
                            visitor.Brick(ox, oy, oz, mask);
                            if (visitor.Finished)
                            {
                                return;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// The cells body <paramref name="index"/> may cover: its pivot plus and minus its radius and a centimetre (a piece's
    /// voxels are centimetres), in the world's level-0 voxels.
    /// </summary>
    private bool Bounds(int index, out VoxelCoord min, out VoxelCoord max)
    {
        var pose = _poses[index];
        var reach = pose.Piece.Radius + 1;
        min = new VoxelCoord(
            (int)MathF.Floor((MathF.Floor(pose.Pivot.X) - reach) / _cell),
            (int)MathF.Floor((MathF.Floor(pose.Pivot.Y) - reach) / _cell),
            (int)MathF.Floor((MathF.Floor(pose.Pivot.Z) - reach) / _cell)
        );
        max = new VoxelCoord(
            (int)MathF.Floor((MathF.Floor(pose.Pivot.X) + reach) / _cell),
            (int)MathF.Floor((MathF.Floor(pose.Pivot.Y) + reach) / _cell),
            (int)MathF.Floor((MathF.Floor(pose.Pivot.Z) + reach) / _cell)
        );
        return pose.Piece.SolidVoxels > 0;
    }

    private static bool Overlap(VoxelCoord min, VoxelCoord max, int x0, int y0, int z0, int x1, int y1, int z1) =>
        min.X <= x1 && max.X >= x0 && min.Y <= y1 && max.Y >= y0 && min.Z <= z1 && max.Z >= z0;

    /// <summary>Writes body <paramref name="index"/>'s voxels in world brick (bx, by, bz) to <paramref name="mask"/>; false when none.</summary>
    private bool Mask(int index, int bx, int by, int bz, Span<ulong> mask)
    {
        // Body 10 bits, brick x and z 14 bits each, brick y 11 bits.
        var key = ((long)index << 39) | ((long)(bx + 8192) << 25) | ((long)(by + 1024) << 14) | (long)(bz + 8192);
        var slot = (int)((ulong)key * 0x9E3779B97F4A7C15UL >> (64 - CacheBits));
        var free = -1;
        for (var probe = 0; probe < Probes; probe++)
        {
            var at = (slot + probe) & (CacheSize - 1);
            if (_stamps[at] != _stamp)
            {
                free = free < 0 ? at : free;
                break;
            }

            if (_keys[at] == key)
            {
                var cached = _masks.AsSpan(at * VoxelConstants.BrickMaskWords, VoxelConstants.BrickMaskWords);
                cached.CopyTo(mask);
                return HasAny(mask);
            }
        }

        Fill(index, bx, by, bz, mask);
        if (free >= 0)
        {
            _keys[free] = key;
            _stamps[free] = _stamp;
            mask.CopyTo(_masks.AsSpan(free * VoxelConstants.BrickMaskWords, VoxelConstants.BrickMaskWords));
        }

        return HasAny(mask);
    }

    /// <summary>Samples body <paramref name="index"/>'s piece at the centres of world brick (bx, by, bz).</summary>
    private void Fill(int index, int bx, int by, int bz, Span<ulong> mask)
    {
        mask.Clear();
        var pose = _poses[index];
        var piece = pose.Piece;
        // Far from the piece: nothing. The brick's centre to its corner is under 7 cells; all in centimetres.
        var centre = new Vector3((bx << 3) + 4, (by << 3) + 4, (bz << 3) + 4) * _cell;
        if (Vector3.Distance(centre, pose.Pivot) > piece.Radius + 7 * _cell)
        {
            return;
        }

        var memory = _bodies.Memory;
        var pivot = new Vector3(piece.Pivot.X, piece.Pivot.Y, piece.Pivot.Z);
        Vector3 axisX = _axes[3 * index],
            axisY = _axes[3 * index + 1],
            axisZ = _axes[3 * index + 2];
        for (var z = 0; z < 8; z++)
        {
            var word = 0UL;
            for (var y = 0; y < 8; y++)
            {
                // The row's first cell centre, in the piece's voxels; each cell along x adds the x axis a cell long.
                var offset =
                    new Vector3((bx << 3) + 0.5f, (by << 3) + y + 0.5f, (bz << 3) + z + 0.5f) * _cell - pose.Pivot;
                var local = axisX * offset.X + axisY * offset.Y + axisZ * offset.Z + pivot;
                var along = axisX * _cell;
                for (var x = 0; x < 8; x++, local += along)
                {
                    var material = piece.Read(
                        memory,
                        (int)MathF.Floor(local.X),
                        (int)MathF.Floor(local.Y),
                        (int)MathF.Floor(local.Z)
                    );
                    if (material != VoxelConstants.Air && !_passable.Contains(material))
                    {
                        word |= 1UL << (x + 8 * y);
                    }
                }
            }

            mask[z] = word;
        }
    }

    private static bool HasAny(ReadOnlySpan<ulong> mask)
    {
        var any = 0UL;
        foreach (var word in mask)
        {
            any |= word;
        }

        return any != 0;
    }
}
