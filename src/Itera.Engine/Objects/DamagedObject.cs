using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>
/// A placed object whose voxels edits changed (<see cref="ObjectDamage"/>): its kind's level-0 voxels with the 8-voxel
/// bricks the edits changed over them, each copied whole from the kind at its first change, and which of its parts those
/// changes reach (a part's own voxels or its border of its neighbours', which its model meshes with), for their models to
/// be made again. Main thread.
/// </summary>
internal sealed class DamagedObject : IObjectVoxelSource
{
    private const int Brick = VoxelConstants.BrickSize;
    private const int BrickVoxels = Brick * Brick * Brick;
    private const int BrickBytes = 2 * BrickVoxels;

    // A changed brick's place in its payload by its packed place in the kind's bricks (x, y, z of 8 bits each).
    private readonly Dictionary<int, int> _brickAt = [];
    private readonly List<int> _bricks = [];
    private readonly List<bool> _brickChanged = [];
    private readonly List<int> _changedSlots = [];
    private byte[] _payload = [];

    public DamagedObject(in ObjectPlacement placement, VoxelObjectKind kind)
    {
        Placement = placement;
        Kind = kind;
        PartDirty = new bool[kind.Parts.Length];
        Models = new int[kind.Parts.Length];
        Array.Fill(Models, -1);
    }

    public ObjectPlacement Placement { get; }

    public VoxelObjectKind Kind { get; }

    /// <summary>How many times its voxels changed: readers that keep what they read of it compare it.</summary>
    public int Revision { get; private set; }

    /// <summary>The bricks the edits changed.</summary>
    public int BrickCount => _bricks.Count;

    /// <summary>The changed bricks' packed places (x, y, z of 8 bits each, in the kind's bricks), in the order changed.</summary>
    public ReadOnlySpan<int> Bricks => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bricks);

    /// <summary>The parts whose models the changes since they were last made reach.</summary>
    public bool[] PartDirty { get; }

    /// <summary>Each part's own model, made from its changed voxels, or -1 while it draws its kind's.</summary>
    public int[] Models { get; }

    /// <summary>True while it waits in <c>ObjectDamage</c>'s queue of objects whose parts' models are to be made again.</summary>
    public bool Queued { get; set; }

    /// <summary>The part models drawn for it this frame (<see cref="ObjectDamage.PartsOf"/>), made at the first draw.</summary>
    public ObjectPartModel[]? Drawn { get; set; }

    /// <summary>The memory its changed bricks hold, in bytes.</summary>
    public long Bytes => (long)_bricks.Count * BrickBytes;

    /// <summary>The packed place of the brick holding kind voxel (x, y, z).</summary>
    public static int BrickKey(int x, int y, int z) => (x >> 3) | ((y >> 3) << 8) | ((z >> 3) << 16);

    public void Voxel(int x, int y, int z, out byte material, out byte density)
    {
        var size = Kind.Size;
        if ((uint)x >= (uint)size.X || (uint)y >= (uint)size.Y || (uint)z >= (uint)size.Z)
        {
            material = VoxelConstants.Air;
            density = VoxelDensity.Empty;
            return;
        }

        if (_brickAt.TryGetValue(BrickKey(x, y, z), out var at))
        {
            var j = (x & 7) + Brick * ((y & 7) + Brick * (z & 7));
            material = _payload[at + j];
            density = _payload[at + BrickVoxels + j];
            return;
        }

        Kind.Voxel(x, y, z, out material, out density);
    }

    /// <summary>
    /// Sets kind voxel (x, y, z), inside the kind's box, copying its brick from the kind at its first change; the brick
    /// counts as changed until <see cref="MarkChangedParts"/>.
    /// </summary>
    public void Set(int x, int y, int z, byte material, byte density)
    {
        var key = BrickKey(x, y, z);
        if (!_brickAt.TryGetValue(key, out var at))
        {
            at = CopyBrick(x & ~7, y & ~7, z & ~7);
            _brickAt.Add(key, at);
            _bricks.Add(key);
            _brickChanged.Add(false);
        }

        var slot = at / BrickBytes;
        if (!_brickChanged[slot])
        {
            _brickChanged[slot] = true;
            _changedSlots.Add(slot);
        }

        var j = (x & 7) + Brick * ((y & 7) + Brick * (z & 7));
        _payload[at + j] = material;
        _payload[at + BrickVoxels + j] = density;
    }

    /// <summary>Notes that an edit changed its voxels: readers that keep what they read of it read it again.</summary>
    public void Touched() => Revision++;

    /// <summary>Marks the parts every brick changed since the last call reaches (<see cref="PartDirty"/>).</summary>
    public void MarkChangedParts()
    {
        foreach (var slot in _changedSlots)
        {
            _brickChanged[slot] = false;
            var key = _bricks[slot];
            MarkParts(key & 255, (key >> 8) & 255, key >> 16);
        }

        _changedSlots.Clear();
    }

    /// <summary>The voxels of changed brick <paramref name="key"/> (materials, then densities), or empty for one unchanged.</summary>
    public ReadOnlySpan<byte> BrickVoxelsOf(int key) =>
        _brickAt.TryGetValue(key, out var at) ? _payload.AsSpan(at, BrickBytes) : default;

    private int CopyBrick(int x0, int y0, int z0)
    {
        var at = _bricks.Count * BrickBytes;
        if (_payload.Length < at + BrickBytes)
        {
            Array.Resize(ref _payload, Math.Max(at + BrickBytes, 2 * _payload.Length));
        }

        for (var j = 0; j < BrickVoxels; j++)
        {
            Kind.Voxel(x0 + (j & 7), y0 + ((j >> 3) & 7), z0 + (j >> 6), out var material, out var density);
            _payload[at + j] = material;
            _payload[at + BrickVoxels + j] = density;
        }

        return at;
    }

    /// <summary>
    /// Marks the parts whose boxes hold brick (bx, by, bz): its owner's, and on each side where it is the owner's first or
    /// last own brick, the neighbour's beyond, whose border it is (the border is one brick).
    /// </summary>
    private void MarkParts(int bx, int by, int bz)
    {
        const int own = ObjectPart.OwnVoxels / Brick;
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!Reaches(bx, dx, own) || !Reaches(by, dy, own) || !Reaches(bz, dz, own))
                    {
                        continue;
                    }

                    var index = Kind.PartIndexAt(bx / own + dx, by / own + dy, bz / own + dz);
                    if (index >= 0)
                    {
                        PartDirty[index] = true;
                    }
                }
            }
        }

        // A neighbour's border holds the owner's first and last own bricks along each side.
        static bool Reaches(int brick, int side, int own) =>
            side == 0 || (side < 0 ? brick % own == 0 : brick % own == own - 1);
    }
}
