using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>The size of an upright character body, in metres, and the steepest ground it walks on.</summary>
/// <param name="HeightMetres">From the feet to the top of the head.</param>
/// <param name="RadiusMetres">Radius of the body seen from above.</param>
/// <param name="StepMetres">The highest ledge the body walks up onto without jumping.</param>
/// <param name="MaxSlopeDegrees">The steepest slope the body walks up, and stands on without sliding.</param>
/// <param name="StepsInAir">
/// True when the body also steps up onto a ledge within <see cref="StepMetres"/> while in the air, as it does walking: a
/// jump whose feet come that near a wall's top lands on it. False: only from the ground.
/// </param>
public readonly record struct CharacterShape(
    float HeightMetres,
    float RadiusMetres,
    float StepMetres,
    float MaxSlopeDegrees = 45,
    bool StepsInAir = false
);

/// <summary>What happened to a body in one <see cref="VoxelCharacterMover.Move"/>.</summary>
public enum CharacterMoveStatus : byte
{
    /// <summary>It moved (perhaps not as far as asked, against walls, floors, or ceilings).</summary>
    Moved = 0,

    /// <summary>Voxels around it are not loaded yet; it did not move and its velocity was set to zero.</summary>
    Waiting = 1,

    /// <summary>It overlapped voxels (placed into it, or loaded around it) and was lifted onto them before moving.</summary>
    Lifted = 2,

    /// <summary>It overlapped voxels and could not be lifted out within its height and a metre; it did not move.</summary>
    Stuck = 3,
}

/// <summary>The outcome of one <see cref="VoxelCharacterMover.Move"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Grounded">Standing on something: a blocking voxel lies at most 1 cm under the feet.</param>
/// <param name="Landed">A fall or a step down ended on the ground in this move.</param>
/// <param name="HitCeiling">Rising stopped against something above.</param>
/// <param name="HitWall">The horizontal move met a wall.</param>
/// <param name="SteppedMetres">
/// How far the feet rose (or, walking down, fell) by stepping onto a ledge or keeping to the ground, not counting
/// falls, jumps, and lifts. A camera eases the eye over this height.
/// </param>
/// <param name="Steep">
/// Grounded on a slope steeper than the limit: only a thin rim of the body rests on it and it rises steeply beside the
/// body. The body should slide towards <paramref name="Downhill"/> and not jump.
/// </param>
/// <param name="Downhill">On steep ground, the horizontal direction (x, z) away from what holds the body up.</param>
/// <param name="WallNormal">
/// When the horizontal move met a wall, the last one's normal across x and z (a unit length, pointing out of the wall
/// towards the body); zero when it met none.
/// </param>
/// <param name="GroundMaterial">
/// Grounded, the physics material of what the body stands on (a voxel's by its voxel material,
/// <see cref="PhysicsMaterials.OfVoxel"/>; a mesh collider's by its settings); the default on a loose body or in the air.
/// </param>
/// <param name="GroundTags">Grounded, the surface tags of what the body stands on; 0 on a loose body or in the air.</param>
public readonly record struct CharacterMoveResult(
    CharacterMoveStatus Status,
    bool Grounded,
    bool Landed,
    bool HitCeiling,
    bool HitWall,
    float SteppedMetres,
    bool Steep = false,
    Vector2 Downhill = default,
    Vector2 WallNormal = default,
    PhysicsMaterialId GroundMaterial = default,
    uint GroundTags = 0
);

/// <summary>
/// Moves upright bodies (vertical cylinders with flat ends) through the level-0 voxels and a game's mesh colliders
/// (<see cref="MeshColliders"/>, read as columns of the same cells, <c>MeshColumns</c>): slides them along walls, steps
/// them up onto ledges no higher than their step, keeps them on the ground when walking down steps and slopes, lands
/// falls, stops jumps at ceilings, and lifts bodies out of voxels placed into them. Solid voxels block unless their
/// material is passable (<see cref="IVoxelPassability"/>). A body never moves while a column its body and move cover is
/// not resident (the slope rules' look-ahead may read a column past those). Without voxel terrain it reads the mesh
/// colliders alone, in cells of <c>Engine:Physics:CollisionCentimetres</c>, always resident, the map's edge the play
/// area's world edge. Over terrain its cells may be finer than the voxels (<c>Engine:Physics:TerrainCellCentimetres</c>):
/// it then meets objects, solid models, and meshes at their own size, reading the voxels as fine cells of ground under
/// their surfaces (<c>FineGround</c>).
/// Seen from above, the body is a circle, so it slides around trunks and along curved walls that are built of 1 cm
/// steps; its flat bottom stands on the highest voxel under it. Works in its cells in double precision. Main
/// thread; allocates nothing once the mesh colliders' columns it reads are made (<c>MeshColumns</c>).
/// <para>
/// At 1 cm a slope is a staircase of small ledges, so two rules keep bodies off slopes steeper than
/// <see cref="CharacterShape.MaxSlopeDegrees"/> while they still step onto stones, logs, and stairs. A step up is kept
/// only when the ground stays level just beyond it (it rises no more than the limit over the next 8 cm, and no wall
/// stands there), as a ledge's top does and a hillside does not. And ground is steep when the body rests on a thin rim
/// (the voxels within 4 cm below the feet reach less than 4 cm / tan(limit) in from its edge) and the ground rises more
/// steeply than the limit within 8 cm of it in some direction (a near-vertical face included), as on a slope seen from
/// any side, so zig-zagging does not help. A stone under the rim or a ledge's edge holds a body on a rim too, but nothing
/// rises beside them. On steep
/// ground a body does not step up, and does not keep to the ground walking down unless the ground below is walkable;
/// the caller slides it and does not let it jump (<see cref="CharacterMoveResult.Steep"/>).
/// </para>
/// </summary>
public sealed class VoxelCharacterMover
{
    // Distances are given in centimetres and kept in the world's level-0 voxels (the mover's cells), so they mean the
    // same at any voxel size: on a grid of 1 cm each is exactly the number of voxels it was tuned as.

    /// <summary>The gap kept between a body and a wall, in centimetres.</summary>
    private const double SkinCentimetres = 0.02;

    /// <summary>How far a face may lie past a voxel boundary and still count as on it, in centimetres.</summary>
    private const double EdgeCentimetres = 0.01;

    /// <summary>Slides against walls within one part of a move.</summary>
    private const int SlideIterations = 4;

    /// <summary>The longest horizontal part of a move, in cells; longer moves are split.</summary>
    private const double MaxPart = 24;

    /// <summary>How much higher than its height a body may be lifted out of voxels, in centimetres.</summary>
    private const double LiftMarginCentimetres = 100;

    /// <summary>A stepped move is kept when it gets this much farther than the flat one, in centimetres.</summary>
    private const double StepGainCentimetres = 0.05;

    /// <summary>The cells of one strip of a window: one row of a strip is one <see cref="UInt128"/>.</summary>
    private const int StripCells = 128;

    /// <summary>The most strips side by side in a window.</summary>
    private const int MaxStrips = 4;

    /// <summary>The widest and deepest window of cells a slide reads: 512 cells, room for a body 2 m in radius.</summary>
    private const int WindowCells = StripCells * MaxStrips;

    /// <summary>
    /// Ground whose top lies this far below the feet, or less, holds the body up for the steepness rule, in centimetres.
    /// </summary>
    private const double SupportDepthCentimetres = 4;

    /// <summary>
    /// How far beyond a step up the ground must stay level, and how far around a body on a rim the ground is looked at
    /// for a steep rise, in centimetres.
    /// </summary>
    private const double LookAheadCentimetres = 8;

    /// <summary>
    /// What a rise is let off above the slope's limit in the steepness rules, the height of the steps a slope of 1 cm
    /// voxels makes; and how far under the feet ground may lie and still hold the body, in centimetres.
    /// </summary>
    private const double StepCentimetres = 1;

    /// <summary>
    /// How far a circle may lie past a face or inside a corner's reach and still meet it at once, in centimetres.
    /// </summary>
    private const double OverlapCentimetres = 0.1;

    /// <summary>The least <see cref="LookAheadCentimetres"/> in cells: what it is in 10 cm cells.</summary>
    private const double LeastLookAheadCells = 0.8;

    /// <summary>The widest circle at the centre of a body that the steepness rule reads first, in centimetres.</summary>
    private const double CentreCentimetres = 16;

    // The distances above in cells.
    private readonly double _skin;
    private readonly double _edge;
    private readonly double _liftMargin;
    private readonly double _stepGain;
    private readonly double _supportDepth;
    private readonly int _supportCells;
    private readonly double _lookAhead;
    private readonly double _oneStep;
    private readonly double _overlap;
    private readonly double _centreCells;

    private readonly VoxelWorld? _world;

    // The world's voxels read in the mover's cells where those are finer (Engine:Physics:TerrainCellCentimetres); null
    // where the cells are the world's level-0 voxels.
    private readonly FineGround? _fine;

    // The match's origin: feet in metres are local to it, cells absolute (whole metres, so a local float plus it is exact
    // in doubles and a move reads the same cells from any origin).
    private readonly WorldOrigin _origin;

    /// <summary>The match's origin, which the positions this mover is given and gives are local to.</summary>
    public WorldOrigin Origin => _origin;

    // The mesh colliders as columns of the mover's cells; null for none.
    private readonly MeshColumns? _meshes;

    // The map's edge: the first column across inside it and the first past it, on x and z.
    private readonly int _edgeFirst;
    private readonly int _edgeEnd;

    // The world's level-0 voxels (the mover's cells) per metre: 100 on a grid of 1 cm.
    private readonly double _cellsPerMetre;
    private readonly MaterialSet _passable;
    private readonly LooseBodyVoxels? _bodies;

    // The windows of cells a slide and the steepness rule read, and the loose bodies' cells added to them. A window is
    // strips of 128 cells side by side, strip after strip: row r of strip s is element s * depth + r.
    private readonly UInt128[] _window = new UInt128[MaxStrips * WindowCells];
    private readonly UInt128[] _steepWindow = new UInt128[MaxStrips * WindowCells];
    private readonly UInt128[] _bodyWindow = new UInt128[WindowCells];
    private readonly UInt128[] _ceilingWindow = new UInt128[MaxStrips * WindowCells];

    // Where the world's voxels are coarser than 1 cm the mover reads their densities (VoxelWorld.SurfaceOver): the
    // ground's surface lies inside a voxel, blended between neighbouring columns whose surfaces differ by less than the
    // ramp (the slope limit over one cell), so slopes are ramps and ledges and walls stay sharp. On a grid of 1 cm a
    // voxel is as fine as the mover reads, and its top is its surface.
    private readonly bool _density;

    // The columns' tops and surfaces of a window the ground and the walls read (density only), and the ramp of the move.
    private readonly int[] _columnTops;
    private readonly int[] _meshTops;
    private readonly int[] _objectTops;
    private readonly double[] _surfaces;

    // The voxels' and the objects' tops up to the ground's reference cell, where a ceiling over open space lies over it
    // (ReadSurfaces).
    private readonly int[] _columnTopsUnder;
    private readonly int[] _objectTopsUnder;

    // The columns' surfaces read within one move or query (VoxelWorld.SurfaceOver), by column and top: a move reads
    // the same columns several times (a large body's about 6,700 reads of 1,100 columns). Slotted by the column's place in a
    // square of 128 columns that repeats across the world: a move reads less than that across, so no two of its columns
    // share a slot (a hash of the key into 4,096 slots lost three reads in four to collisions, 2026-10-08).
    private const int SurfaceCacheSideBits = 7;
    private const int SurfaceCacheSide = 1 << SurfaceCacheSideBits;
    private readonly long[] _cachedKeys = new long[SurfaceCacheSide * SurfaceCacheSide];
    private readonly double[] _cachedSurfaces = new double[SurfaceCacheSide * SurfaceCacheSide];
    private readonly int[] _cachedStamps = new int[SurfaceCacheSide * SurfaceCacheSide];
    private int _cacheStamp = 1;
    private int _surfaceX;
    private int _surfaceZ;
    private int _surfaceWidth;
    private int _surfaceDepth;
    private double _ramp = 1;

    // Within one move: a loose body the body already overlaps above its step height does not block the cells under
    // where the body started (the body's stand-in pushes them apart), so it is never stuck inside a body that hit it.
    private bool _ignoreStart;
    private CircleFootprint _start;

    /// <param name="world">The voxels.</param>
    /// <param name="passability">Which materials bodies walk through.</param>
    /// <param name="bodies">Loose bodies, read as voxels too; null for none.</param>
    /// <param name="meshes">The match's mesh colliders, read in the world's cells too; null for none.</param>
    public VoxelCharacterMover(
        VoxelWorld world,
        IVoxelPassability passability,
        LooseBodyVoxels? bodies = null,
        MeshColliders? meshes = null
    )
        : this(
            world,
            world.Grid.BaseCentimetres,
            world.Grid.MinVoxelXZ,
            world.Grid.EndVoxelXZ,
            passability,
            bodies,
            meshes
        ) { }

    /// <summary>
    /// A mover whose cells are finer than the world's level-0 voxels (<c>Engine:Physics:TerrainCellCentimetres</c>), so
    /// it meets voxel objects, solid models, and mesh colliders at their own size over coarse ground: the world's voxels
    /// are read as cells of ground under their surfaces, blended between the voxels' columns as coarse cells are
    /// (<c>FineGround</c>). <paramref name="cellCentimetres"/> equal to the world's voxels is the mover of its voxels.
    /// </summary>
    /// <param name="world">The voxels.</param>
    /// <param name="cellCentimetres">The cells' edge: a whole divisor of the world's level-0 voxels.</param>
    /// <param name="passability">Which materials bodies walk through.</param>
    /// <param name="bodies">Loose bodies, read in the same cells (made with <paramref name="cellCentimetres"/>); null for none.</param>
    /// <param name="meshes">The match's mesh colliders, read in the same cells; null for none.</param>
    public VoxelCharacterMover(
        VoxelWorld world,
        int cellCentimetres,
        IVoxelPassability passability,
        LooseBodyVoxels? bodies = null,
        MeshColliders? meshes = null
    )
        : this(
            world,
            cellCentimetres,
            world.Grid.MinVoxelXZ * Factor(world, cellCentimetres),
            world.Grid.EndVoxelXZ * Factor(world, cellCentimetres),
            passability,
            bodies,
            meshes
        ) { }

    /// <summary>The cells across one of the world's level-0 voxels: refuses a cell that does not divide it.</summary>
    private static int Factor(VoxelWorld world, int cellCentimetres)
    {
        var voxel = world.Grid.BaseCentimetres;
        if (cellCentimetres < 1 || cellCentimetres > voxel || voxel % cellCentimetres != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellCentimetres),
                cellCentimetres,
                $"The mover's cells are a whole divisor of the world's {voxel} cm voxels."
            );
        }

        return voxel / cellCentimetres;
    }

    /// <summary>The cells' edge, in centimetres: the world's level-0 voxels', or finer.</summary>
    public int CellCentimetres { get; }

    /// <summary>
    /// A mover for a match without voxel terrain: the mesh colliders alone, in cells of <paramref name="cellCentimetres"/>,
    /// with the map's edge <paramref name="edgeMetres"/> from the middle on x and z (the play area's world edge).
    /// </summary>
    /// <param name="passability">Which voxel materials bodies walk through (none are read without terrain).</param>
    /// <param name="meshes">The match's mesh colliders.</param>
    /// <param name="cellCentimetres">The cells' edge, in centimetres (<c>Engine:Physics:CollisionCentimetres</c>).</param>
    /// <param name="edgeMetres">How far the map reaches from the middle, in metres; infinity (the default) for no edge.</param>
    /// <param name="bodies">Loose bodies, read in the same cells (made with <paramref name="cellCentimetres"/>); null for none.</param>
    public VoxelCharacterMover(
        IVoxelPassability passability,
        MeshColliders meshes,
        int cellCentimetres,
        float edgeMetres = float.PositiveInfinity,
        LooseBodyVoxels? bodies = null
    )
        : this(
            null,
            cellCentimetres,
            -EdgeCells(edgeMetres, cellCentimetres),
            EdgeCells(edgeMetres, cellCentimetres),
            passability,
            bodies,
            meshes
        ) { }

    private VoxelCharacterMover(
        VoxelWorld? world,
        int cellCentimetres,
        int edgeFirst,
        int edgeEnd,
        IVoxelPassability passability,
        LooseBodyVoxels? bodies,
        MeshColliders? meshes
    )
    {
        if (cellCentimetres < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(cellCentimetres), cellCentimetres, "A cell is 1 cm or more.");
        }

        _world = world;
        CellCentimetres = cellCentimetres;
        _fine =
            world is not null && cellCentimetres < world.Grid.BaseCentimetres
                ? new FineGround(world, cellCentimetres)
                : null;
        _origin = world?.Origin ?? meshes?.Origin ?? new WorldOrigin();
        _edgeFirst = edgeFirst;
        _edgeEnd = edgeEnd;
        _cellsPerMetre = VoxelConstants.CentimetresPerMetre / (double)cellCentimetres;
        _meshes = meshes is null ? null : new MeshColumns(meshes, _cellsPerMetre);
        _passable = MaterialSet.Passable(passability);
        _bodies = bodies;
        double cell = cellCentimetres;
        _skin = SkinCentimetres / cell;
        _edge = EdgeCentimetres / cell;
        _liftMargin = LiftMarginCentimetres / cell;
        _stepGain = StepGainCentimetres / cell;
        _supportDepth = SupportDepthCentimetres / cell;
        _supportCells = Math.Max(1, (int)Math.Ceiling(_supportDepth - 1e-9));
        // At least 0.8 of a cell (8 cm at 10 cm), so on cells coarser than 10 cm it still reaches the next column.
        _lookAhead = Math.Max(LookAheadCentimetres / cell, LeastLookAheadCells);
        _oneStep = StepCentimetres / cell;
        _overlap = OverlapCentimetres / cell;
        _centreCells = CentreCentimetres / cell;
        _density = cellCentimetres > 1;
        // A slide's window: the widest body and the longest part on each side, and a margin.
        var side = _density ? (int)Math.Ceiling(2 * (MaxRadiusMetres * _cellsPerMetre + MaxPart + 2)) + 4 : 0;
        _columnTops = new int[side * side];
        _meshTops = new int[side * side];
        _objectTops = new int[side * side];
        _surfaces = new double[side * side];
        _columnTopsUnder = new int[side * side];
        _objectTopsUnder = new int[side * side];
    }

    /// <summary>The run's physics materials, for the ground's (<see cref="CharacterMoveResult.GroundMaterial"/>); none gives the default.</summary>
    public PhysicsMaterials? Materials { get; init; }

    /// <summary>The voxel materials' surface tags, for the ground's (<see cref="CharacterMoveResult.GroundTags"/>); none gives none.</summary>
    public IVoxelSurfaceTags? SurfaceTags { get; init; }

    /// <summary>
    /// The voxel objects the world places, read as the ground is (<see cref="ObjectColumns"/>: walls, ledges, and ground
    /// to stand on, with their material and tags); none without. A move waits where their areas are not filled yet.
    /// </summary>
    internal ObjectColumns? Objects { get; init; }

    /// <summary>
    /// The widest body the mover takes, in metres: the window of cells a slide reads (512 cells) limits it. A 10 m giant's
    /// radius is 1.5 m.
    /// </summary>
    public static float MaxRadiusMetres => 2f;

    /// <summary>The cells from the middle to an edge <paramref name="edgeMetres"/> away, at most <see cref="CircleFootprint.Far"/>.</summary>
    private static int EdgeCells(float edgeMetres, int cellCentimetres)
    {
        if (float.IsNaN(edgeMetres) || edgeMetres <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edgeMetres),
                edgeMetres,
                "The map's edge lies past the middle."
            );
        }

        var cells = Math.Floor(edgeMetres * (double)VoxelConstants.CentimetresPerMetre / cellCentimetres);
        return (int)Math.Min(cells, CircleFootprint.Far);
    }

    /// <summary>
    /// The farthest one call moves a body, in metres: 4 m, or 240 m/s at 60 Hz, well past the fastest fall
    /// (the game keeps a character's fastest fall at most 100 m/s). A larger move (a huge velocity from a bug or a peer)
    /// is scaled down to it, so the columns the move reads stay few and the walk cannot hang the main thread.
    /// </summary>
    public const float MaxMoveMetres = 4;

    /// <summary>
    /// Moves a body at <paramref name="feetMetres"/> (the centre of its bottom) by <paramref name="velocity"/> for
    /// <paramref name="seconds"/>. Walls take away the part of the horizontal velocity that points into them; landing
    /// and ceilings take away the vertical velocity. A move longer than <see cref="MaxMoveMetres"/> is scaled down to
    /// it, velocity included. Throws <see cref="ArgumentException"/> when the feet, the velocity, or the time is not a
    /// finite number: callers check what comes from outside (a peer's command, a physics push) before it gets here.
    /// </summary>
    public CharacterMoveResult Move(
        ref Vector3 feetMetres,
        ref Vector3 velocity,
        in CharacterShape shape,
        float seconds
    )
    {
        if (!IsFinite(feetMetres) || !IsFinite(velocity) || !float.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentException("The feet, the velocity, and the time must be finite numbers.");
        }

        // In doubles: a float's length of a huge velocity is infinite, which scaled the velocity to nothing.
        var length =
            Math.Sqrt(
                (double)velocity.X * velocity.X + (double)velocity.Y * velocity.Y + (double)velocity.Z * velocity.Z
            ) * seconds;
        if (length > MaxMoveMetres)
        {
            velocity = new Vector3(
                (float)(velocity.X * (MaxMoveMetres / length)),
                (float)(velocity.Y * (MaxMoveMetres / length)),
                (float)(velocity.Z * (MaxMoveMetres / length))
            );
        }

        var r = shape.RadiusMetres * _cellsPerMetre;
        var h = shape.HeightMetres * _cellsPerMetre;
        var step = shape.StepMetres * _cellsPerMetre;
        if (
            !(r > 0)
            || r > MaxRadiusMetres * _cellsPerMetre
            || !(h > 0)
            || !(step >= 0)
            || !(shape.MaxSlopeDegrees > 0 && shape.MaxSlopeDegrees < 90)
        )
        {
            throw new ArgumentOutOfRangeException(nameof(shape), shape, "The body's size is out of range.");
        }

        var slope = Math.Tan(shape.MaxSlopeDegrees * Math.PI / 180);
        _ramp = slope;
        ForgetSurfaces();

        double px = CellX(feetMetres.X),
            py = CellY(feetMetres.Y),
            pz = CellZ(feetMetres.Z);
        var scale = seconds * _cellsPerMetre;
        double dx = velocity.X * scale,
            dy = velocity.Y * scale,
            dz = velocity.Z * scale;
        var horizontal = Math.Sqrt(dx * dx + dz * dz);

        var reach = r + horizontal + 2;
        if (
            !Resident(
                (int)Math.Floor(px - reach),
                (int)Math.Floor(pz - reach),
                (int)Math.Floor(px + reach),
                (int)Math.Floor(pz + reach)
            )
        )
        {
            velocity = Vector3.Zero;
            return new CharacterMoveResult(CharacterMoveStatus.Waiting, false, false, false, false, 0);
        }

        var status = CharacterMoveStatus.Moved;
        var lifted = Lift(px, ref py, pz, r, h);
        if (lifted < 0)
        {
            velocity = Vector3.Zero;
            return new CharacterMoveResult(CharacterMoveStatus.Stuck, false, false, false, false, 0);
        }

        if (lifted > 0)
        {
            status = CharacterMoveStatus.Lifted;
            velocity.Y = Math.Max(velocity.Y, 0);
            dy = Math.Max(dy, 0);
        }

        if (StandOnBodies(px, ref py, pz, r, h, step))
        {
            status = CharacterMoveStatus.Lifted;
            velocity.Y = Math.Max(velocity.Y, 0);
            dy = Math.Max(dy, 0);
        }

        var grounded = IsGrounded(px, py, pz, r);
        // On steep ground a body does not step up; in the air it does only where its shape asks to.
        var canStep = step > 0 && (grounded ? !IsSteep(px, py, pz, r, step, slope, out _, out _) : shape.StepsInAir);
        var stepped = 0.0;
        double vx = velocity.X,
            vz = velocity.Z;
        var wallNormal = Vector2.Zero;

        // Horizontal, in parts short enough for one window of cells.
        var hitWall =
            horizontal > 0
            && MoveHorizontally(
                ref px,
                ref py,
                ref pz,
                dx,
                dz,
                horizontal,
                r,
                h,
                step,
                slope,
                canStep,
                ref vx,
                ref vz,
                ref stepped,
                ref wallNormal
            );

        // Vertical.
        var beforeVertical = py;
        var footprint = new CircleFootprint(px, pz, r);
        var (landed, hitCeiling) = MoveVertically(in footprint, ref py, dy, h);

        // Keep to the ground walking down steps and slopes.
        if (grounded && !landed && dy <= 0 && step > 0 && StepDown(in footprint, px, ref py, pz, r, step, slope))
        {
            // The whole drop in this move is the step down, including this move's share of gravity.
            stepped += py - beforeVertical;
            landed = true;
        }

        var groundedAfter = IsGrounded(px, py, pz, r);
        double downX = 0,
            downZ = 0;
        var steep = groundedAfter && IsSteep(px, py, pz, r, step, slope, out downX, out downZ);
        var groundMaterial = PhysicsMaterialId.Default;
        uint groundTags = 0;
        if (groundedAfter)
        {
            ReadGround(px, py, pz, r, out groundMaterial, out groundTags);
        }

        var vy = velocity.Y;
        if ((landed || groundedAfter) && vy < 0 || hitCeiling && vy > 0)
        {
            vy = 0;
        }

        feetMetres = Local(px, py, pz);
        velocity = new Vector3((float)vx, vy, (float)vz);
        return new CharacterMoveResult(
            status,
            groundedAfter,
            landed,
            hitCeiling,
            hitWall,
            (float)(stepped / _cellsPerMetre),
            steep,
            new Vector2((float)downX, (float)downZ),
            hitWall ? wallNormal : Vector2.Zero,
            groundMaterial,
            groundTags
        );
    }

    /// <summary>
    /// What a grounded body stands on: the highest voxel or mesh triangle within the grounded reach under the feet's
    /// centre, else the highest under four points half its radius out; a voxel's physics material and tags by its voxel
    /// material, a triangle's by its collider. On a loose body, or where none of the five finds ground, the default and
    /// no tags.
    /// </summary>
    private void ReadGround(double px, double py, double pz, double r, out PhysicsMaterialId material, out uint tags)
    {
        var y1 = (int)Math.Floor(py - 1e-6);
        var y0 = y1 - (int)Math.Ceiling(_oneStep + _edge) - 1;
        if (GroundAt((int)Math.Floor(px), (int)Math.Floor(pz), y0, y1, out _, out material, out tags))
        {
            return;
        }

        var best = int.MinValue;
        var half = r * 0.5;
        for (var i = 0; i < 4; i++)
        {
            var x = (int)
                Math.Floor(
                    px
                        + (
                            i == 0 ? half
                            : i == 1 ? -half
                            : 0
                        )
                );
            var z = (int)
                Math.Floor(
                    pz
                        + (
                            i == 2 ? half
                            : i == 3 ? -half
                            : 0
                        )
                );
            if (GroundAt(x, z, y0, y1, out var top, out var found, out var foundTags) && top > best)
            {
                best = top;
                material = found;
                tags = foundTags;
            }
        }
    }

    /// <summary>The highest blocking voxel or mesh triangle of column (x, z) from y0 to y1, with its material and tags.</summary>
    private bool GroundAt(int x, int z, int y0, int y1, out int top, out PhysicsMaterialId material, out uint tags)
    {
        top = int.MinValue;
        material = PhysicsMaterialId.Default;
        tags = 0;
        if (_fine is not null)
        {
            if (_fine.TopAt(x, z, y0, y1, out var cell, out var voxel))
            {
                top = cell;
                material = Materials?.OfVoxel(voxel) ?? PhysicsMaterialId.Default;
                tags = SurfaceTags?.TagsOf(voxel) ?? 0;
            }
        }
        else if (_world is { } world)
        {
            for (var y = y1; y >= y0; y--)
            {
                if (
                    world.TryGetVoxelMaterial(new VoxelCoord(x, y, z), out var voxel)
                    && voxel != VoxelConstants.Air
                    && !_passable.Contains(voxel)
                )
                {
                    top = y;
                    material = Materials?.OfVoxel(voxel) ?? PhysicsMaterialId.Default;
                    tags = SurfaceTags?.TagsOf(voxel) ?? 0;
                    break;
                }
            }
        }

        if (
            _meshes is { } meshes
            && meshes.TopAt(x, z, y0, y1, out var meshTop, out var meshTags, out var collider)
            && meshTop >= top
        )
        {
            top = meshTop;
            material = meshes.MaterialOf(collider);
            tags = meshTags;
        }

        if (
            Objects is { Empty: false } objects
            && objects.TopAt(x, z, y0, y1, out var objectTop, out var objectVoxel)
            && objectTop >= top
        )
        {
            top = objectTop;
            material = Materials?.OfVoxel(objectVoxel) ?? PhysicsMaterialId.Default;
            tags = SurfaceTags?.TagsOf(objectVoxel) ?? 0;
        }

        return top != int.MinValue;
    }

    /// <summary>
    /// The horizontal part of a move of (<paramref name="dx"/>, <paramref name="dz"/>) voxels, in parts short enough for
    /// one window of cells: each part slides along the walls, or, where <paramref name="canStep"/> and a ledge stops it,
    /// steps up onto the ledge (<see cref="TryStepUp"/>). The velocity loses what points into the walls met, and
    /// <paramref name="stepped"/> gains the steps' height; <paramref name="wallNormal"/> is the last wall's normal.
    /// Returns true when a wall was met.
    /// </summary>
    private bool MoveHorizontally(
        ref double px,
        ref double py,
        ref double pz,
        double dx,
        double dz,
        double horizontal,
        double r,
        double h,
        double step,
        double slope,
        bool canStep,
        ref double vx,
        ref double vz,
        ref double stepped,
        ref Vector2 wallNormal
    )
    {
        var hitWall = false;
        // A slide's window spans the radius and the part's length on each side, plus a cell: 2r + 2 part + 4.
        var maxPart = Math.Min(MaxPart, (WindowCells - 4) / 2 - Math.Ceiling(r) - 1);
        var parts = (int)Math.Ceiling(horizontal / maxPart);
        double partX = dx / parts,
            partZ = dz / parts;
        Span<Vector2> normals = stackalloc Vector2[SlideIterations];
        Span<Vector2> steppedNormals = stackalloc Vector2[SlideIterations];
        for (var part = 0; part < parts; part++)
        {
            double fx = px,
                fz = pz;
            var flatCount = Slide(ref fx, ref fz, partX, partZ, py, py, r, h, canStep, normals);
            var flatGain = Progress(fx - px, fz - pz, partX, partZ);
            var wanted = Math.Sqrt(partX * partX + partZ * partZ);
            if (
                canStep
                && flatGain < wanted - _stepGain
                && TryStepUp(
                    px,
                    py,
                    pz,
                    partX,
                    partZ,
                    flatGain,
                    r,
                    h,
                    step,
                    slope,
                    steppedNormals,
                    out var sx,
                    out var sz,
                    out var feet,
                    out var steppedCount
                )
            )
            {
                stepped += feet - py;
                px = sx;
                pz = sz;
                py = feet;
                hitWall |= steppedCount > 0;
                if (steppedCount > 0)
                {
                    wallNormal = steppedNormals[steppedCount - 1];
                }

                Clip(ref vx, ref vz, steppedNormals[..steppedCount]);
                continue;
            }

            if (_density)
            {
                // Up onto the ground the slide let the body over, the voxels' only (a loose body is stepped onto, not
                // walked up): a ramp it walks up, or for a body in the air the little the blend rises past the columns'
                // own surfaces, which the slide's walls read. Where there is no room over the head for that, the body
                // does not go there.
                var ground = Ground(
                    new CircleFootprint(fx, fz, r),
                    (int)Math.Floor(py - _edge),
                    py + _ramp,
                    bodies: false
                );
                if (ground > py + _edge)
                {
                    if (Headroom(fx, py, fz, r, h, ground - py) < ground - py - _edge)
                    {
                        hitWall = true;
                        wallNormal = new Vector2((float)(-partX / wanted), (float)(-partZ / wanted));
                        Clip(ref vx, ref vz, [wallNormal]);
                        continue;
                    }

                    if (canStep)
                    {
                        stepped += ground - py;
                    }

                    py = ground;
                }
            }

            px = fx;
            pz = fz;
            hitWall |= flatCount > 0;
            if (flatCount > 0)
            {
                wallNormal = normals[flatCount - 1];
            }

            Clip(ref vx, ref vz, normals[..flatCount]);
        }

        return hitWall;
    }

    /// <summary>
    /// Loose bodies the body overlaps where it starts (voxels): one under the feet, within the step, with room for the
    /// body over it, carries it up onto it, and <paramref name="py"/> rises to its top (returns true); one overlapping it
    /// higher up (it hit the body from the side), or one under the feet that goes on up past the step (a wall or a
    /// solid's face moved into it, which would carry it up a step a move), stops blocking where the body stands for this
    /// move.
    /// </summary>
    private bool StandOnBodies(double px, ref double py, double pz, double r, double h, double step)
    {
        _ignoreStart = false;
        _start = new CircleFootprint(px, pz, r);
        if (_bodies is null || _bodies.Count == 0)
        {
            return false;
        }

        var carried = _bodies.HighestTop(
            in _start,
            (int)Math.Floor(py + _edge),
            (int)Math.Floor(py + step + _edge) - 1
        );
        if (carried == int.MinValue || carried <= py)
        {
            carried = int.MinValue;
        }
        else if (_bodies.HighestTop(in _start, carried, (int)Math.Ceiling(carried + h - _edge) - 1) != int.MinValue)
        {
            // No room over that top: the body stays where it is, and what overlaps it there does not hold it.
            _ignoreStart = true;
            return false;
        }

        var lifted = carried != int.MinValue;
        if (lifted)
        {
            py = carried;
        }

        _ignoreStart =
            _bodies.HighestTop(in _start, (int)Math.Floor(py + step + _edge), (int)Math.Ceiling(py + h - _edge) - 1)
            != int.MinValue;
        return lifted;
    }

    /// <summary>
    /// A part of a horizontal move tried raised by up to the step, where the flat slide gained too little (a ledge): true
    /// when the raised slide gains more, and the body can stand where it ends without rising further (not a slope too
    /// steep), with where it ends (<paramref name="sx"/>, <paramref name="sz"/>), the feet's new height, and the walls it
    /// touched in <paramref name="normals"/>. All in voxels.
    /// </summary>
    private bool TryStepUp(
        double px,
        double py,
        double pz,
        double partX,
        double partZ,
        double flatGain,
        double r,
        double h,
        double step,
        double slope,
        Span<Vector2> normals,
        out double sx,
        out double sz,
        out double feet,
        out int count
    )
    {
        sx = px;
        sz = pz;
        feet = py;
        count = 0;
        var rise = Headroom(px, py, pz, r, h, step);
        if (rise < _oneStep)
        {
            return false;
        }

        // Loose bodies block the raised slide from the unraised feet: a body is pushed, not stepped onto.
        count = Slide(ref sx, ref sz, partX, partZ, py + rise, py, r, h, false, normals);
        if (Progress(sx - px, sz - pz, partX, partZ) <= flatGain + _stepGain)
        {
            return false;
        }

        var top = Ground(new CircleFootprint(sx, sz, r), (int)Math.Floor(py - step), py + rise + _edge);
        if (top > py + rise + _edge)
        {
            // The ground there lies over the raised slide (a blend with ground rising beyond): no room to stand.
            return false;
        }

        feet = top >= py - step ? top : py;
        return !KeepsRising(px, pz, sx, sz, feet, r, step, slope);
    }

    /// <summary>
    /// The vertical part of a move of <paramref name="dy"/> voxels: falling onto the highest ground in the way (landed), or
    /// rising into the lowest ceiling above the body's top (hit the ceiling), or all of it.
    /// </summary>
    private (bool Landed, bool HitCeiling) MoveVertically(
        in CircleFootprint footprint,
        ref double py,
        double dy,
        double h
    )
    {
        if (dy < 0)
        {
            var top = Ground(in footprint, (int)Math.Floor(py + dy), py + _edge);
            if (top > py + dy)
            {
                // A fall lands; on blended ground it lifts a body the ground blends over only as far as there is room
                // over the head.
                py =
                    _density && top > py
                        ? py
                            + Math.Min(
                                top - py,
                                Headroom(footprint.CentreX, py, footprint.CentreZ, footprint.Radius, h, top - py)
                            )
                        : Math.Max(py + dy, top);
                return (true, false);
            }

            py += dy;
        }
        else if (dy > 0)
        {
            var bodyTop = py + h;
            var bottom = Bottom(
                in footprint,
                (int)Math.Ceiling(bodyTop - _edge),
                (int)Math.Ceiling(bodyTop + dy - _edge) - 1
            );
            if (bottom != int.MaxValue && bottom - _skin < bodyTop + dy)
            {
                py = Math.Max(py, bottom - _skin - h);
                return (false, true);
            }

            py += dy;
        }

        return (false, false);
    }

    /// <summary>
    /// Keeps a grounded body on the ground walking down steps and slopes: true, with <paramref name="py"/> on the ground,
    /// when there is walkable ground within the step below. Down a steep slope, or off an edge onto one, the body falls
    /// instead.
    /// </summary>
    private bool StepDown(
        in CircleFootprint footprint,
        double px,
        ref double py,
        double pz,
        double r,
        double step,
        double slope
    )
    {
        var top = Ground(in footprint, (int)Math.Floor(py - step), py + _edge);
        if (top < py - step || IsSteep(px, top, pz, r, step, slope, out _, out _))
        {
            return false;
        }

        py = top;
        return true;
    }

    /// <summary>
    /// The top of the highest blocking voxel or mesh collider under an upright circle of <paramref name="radiusMetres"/>
    /// about <paramref name="atMetres"/>, from <paramref name="downMetres"/> below its height to <paramref name="upMetres"/>
    /// above, in metres: where a foot there would stand. Materials bodies walk through (foliage) and loose bodies are not
    /// ground. False when nothing blocks in that range (<paramref name="topMetres"/> 0), or when the level-0 voxels there
    /// are not loaded (<paramref name="topMetres"/> NaN: the ground is not known, as far from the camera, where only
    /// coarser levels are kept). Main thread; allocates nothing.
    /// </summary>
    public bool GroundTop(Vector3 atMetres, float radiusMetres, float upMetres, float downMetres, out float topMetres)
    {
        CheckRadius(radiusMetres);
        var perMetre = _cellsPerMetre;
        var footprint = new CircleFootprint(CellX(atMetres.X), CellZ(atMetres.Z), radiusMetres * perMetre);
        if (!Resident(footprint.MinX, footprint.MinZ, footprint.MaxX, footprint.MaxZ))
        {
            topMetres = float.NaN;
            return false;
        }

        var y = CellY(atMetres.Y);
        int y0 = (int)Math.Floor(y - downMetres * perMetre),
            y1 = (int)Math.Ceiling(y + upMetres * perMetre) - 1;
        if (_density)
        {
            // Blended as for a body of the default slope limit, and no higher than asked.
            _ramp = DefaultRamp;
            ForgetSurfaces();
            var surface = Math.Min(Surface(in footprint, y0, y1), y + upMetres * perMetre);
            topMetres = double.IsNegativeInfinity(surface) ? 0 : LocalY(surface);
            return !double.IsNegativeInfinity(surface);
        }

        var top = TerrainTop(in footprint, y0, y1);
        topMetres = top == int.MinValue ? 0 : LocalY(top);
        return top != int.MinValue;
    }

    /// <summary>
    /// True when the level-0 voxels within <paramref name="radiusMetres"/> of <paramref name="feetMetres"/> (across x and
    /// z) are loaded, so a body there can be moved. Callers that hand a body to <see cref="Move"/> ask with a margin
    /// over its radius, so it does not stop at once for want of the voxels a step further on. Main thread.
    /// </summary>
    public bool IsResident(Vector3 feetMetres, float radiusMetres)
    {
        double x = CellX(feetMetres.X),
            z = CellZ(feetMetres.Z),
            r = radiusMetres * _cellsPerMetre;
        return Resident((int)Math.Floor(x - r), (int)Math.Floor(z - r), (int)Math.Floor(x + r), (int)Math.Floor(z + r));
    }

    /// <summary>True when a body at <paramref name="feetMetres"/> overlaps a blocking voxel.</summary>
    public bool Overlaps(Vector3 feetMetres, in CharacterShape shape)
    {
        BeginQuery(in shape);
        double px = CellX(feetMetres.X),
            py = CellY(feetMetres.Y),
            pz = CellZ(feetMetres.Z);
        var footprint = new CircleFootprint(px, pz, shape.RadiusMetres * _cellsPerMetre);
        var h = shape.HeightMetres * _cellsPerMetre;
        return !double.IsNegativeInfinity(OverlapTop(in footprint, py, h, bodies: false));
    }

    /// <summary>
    /// True when a body at <paramref name="feetMetres"/> overlaps a blocking voxel or a loose body (with the solid models
    /// <see cref="LooseBodyVoxels"/> holds), as a move tests them: whether there is room for it there, as a crouched
    /// player asks before it stands up.
    /// </summary>
    public bool OverlapsAnything(Vector3 feetMetres, in CharacterShape shape)
    {
        BeginQuery(in shape);
        double px = CellX(feetMetres.X),
            py = CellY(feetMetres.Y),
            pz = CellZ(feetMetres.Z);
        var footprint = new CircleFootprint(px, pz, shape.RadiusMetres * _cellsPerMetre);
        var h = shape.HeightMetres * _cellsPerMetre;
        return !double.IsNegativeInfinity(OverlapTop(in footprint, py, h, bodies: true));
    }

    /// <summary>A local x in metres, in absolute cells: the local float plus the origin's whole metres, in doubles.</summary>
    private double CellX(float metres) => _origin.WorldX(metres) * _cellsPerMetre;

    /// <summary>A local y in metres, in absolute cells.</summary>
    private double CellY(float metres) => _origin.WorldY(metres) * _cellsPerMetre;

    /// <summary>A local z in metres, in absolute cells.</summary>
    private double CellZ(float metres) => _origin.WorldZ(metres) * _cellsPerMetre;

    /// <summary>A y in absolute cells, as a local height in metres.</summary>
    private float LocalY(double cells) => (float)(cells / _cellsPerMetre - _origin.WorldY(0));

    /// <summary>A point in absolute cells, as a local position in metres.</summary>
    private Vector3 Local(double x, double y, double z) =>
        new(
            (float)(x / _cellsPerMetre - _origin.WorldX(0)),
            (float)(y / _cellsPerMetre - _origin.WorldY(0)),
            (float)(z / _cellsPerMetre - _origin.WorldZ(0))
        );

    /// <summary>The ramp of a body of the default slope limit (45 degrees): one cell over one cell.</summary>
    private const double DefaultRamp = 1;

    /// <summary>Starts a query of a body's shape: its slope limit's ramp, and surfaces read afresh.</summary>
    private void BeginQuery(in CharacterShape shape)
    {
        CheckRadius(shape.RadiusMetres);
        _ramp =
            shape.MaxSlopeDegrees > 0 && shape.MaxSlopeDegrees < 90
                ? Math.Tan(shape.MaxSlopeDegrees * Math.PI / 180)
                : DefaultRamp;
        ForgetSurfaces();
    }

    /// <summary>Refuses a radius past <see cref="MaxRadiusMetres"/>, which the windows and their surfaces are sized for.</summary>
    private static void CheckRadius(float radiusMetres)
    {
        if (!(radiusMetres >= 0 && radiusMetres <= MaxRadiusMetres))
        {
            throw new ArgumentOutOfRangeException(
                nameof(radiusMetres),
                radiusMetres,
                "The radius is past what the mover reads."
            );
        }
    }

    /// <summary>
    /// Raises the feet onto whatever the body overlaps until it overlaps nothing. Returns how far it rose, or -1 when it
    /// is still inside something after its height and <c>_liftMargin</c> (then the feet are unchanged).
    /// </summary>
    private double Lift(double px, ref double py, double pz, double r, double h)
    {
        var footprint = new CircleFootprint(px, pz, r);
        var start = py;
        var feet = py;
        while (true)
        {
            var top = OverlapTop(in footprint, feet, h, bodies: false);
            if (double.IsNegativeInfinity(top))
            {
                py = feet;
                return feet - start;
            }

            feet = top;
            if (feet - start > h + _liftMargin)
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// The highest top of what a body at <paramref name="feet"/> overlaps, of the voxels (and with
    /// <paramref name="bodies"/> the loose bodies too), or <see cref="double.NegativeInfinity"/> where it overlaps nothing:
    /// on a grid of 1 cm any blocking voxel from the feet to the head; else the blended surface (<see cref="Surface"/>) of
    /// the ground within the body's height, where it lies over the feet.
    /// </summary>
    private double OverlapTop(in CircleFootprint footprint, double feet, double h, bool bodies)
    {
        int y0 = (int)Math.Floor(feet + _edge),
            y1 = (int)Math.Ceiling(feet + h - _edge) - 1;
        if (!_density)
        {
            var top = bodies ? Top(in footprint, y0, y1) : TerrainTop(in footprint, y0, y1);
            return top == int.MinValue ? double.NegativeInfinity : top;
        }

        // From a voxel lower, whose surface may reach over its top, and from where a neighbour joined to ground at the
        // feet can sink to, so the blend is the one the ground's reads make (Ground).
        var surface = Surface(in footprint, Math.Min(y0, (int)Math.Floor(feet - _ramp)) - 1, y1);
        var overlap = surface > feet + _edge ? surface : double.NegativeInfinity;
        if (bodies && _bodies is not null && _bodies.Count > 0)
        {
            var body = _bodies.HighestTop(in footprint, y0, y1);
            if (body != int.MinValue)
            {
                overlap = Math.Max(overlap, body);
            }
        }

        return overlap;
    }

    /// <summary>
    /// The ground under a footprint, of the voxels and the loose bodies, near <paramref name="limit"/> high (a body there
    /// overlaps nothing, so its ground lies no higher): the highest top of the ground from <paramref name="y0"/>, or
    /// <see cref="double.NegativeInfinity"/> where none lies there. On a grid of 1 cm a voxel's top, below the limit's
    /// cell; else the blended surface's highest point under the footprint (<see cref="Surface"/>), read up to the voxel
    /// over the limit's: a neighbour whose surface lies over the feet still shapes the blend under the footprint, which
    /// never reaches its centre. A column open at the limit's cell with something over it (the lintel over a door, which
    /// on coarse cells that read reaches over the head) has its ground under that cell, not on the ceiling.
    /// </summary>
    private double Ground(in CircleFootprint footprint, int y0, double limit, bool bodies = true)
    {
        if (!_density)
        {
            var top = Top(in footprint, y0, (int)Math.Floor(limit) - 1);
            return top == int.MinValue ? double.NegativeInfinity : top;
        }

        // From a voxel lower too: a surface lies up to half a voxel over its highest voxel's top. And up to where a
        // neighbour joined to ground under the limit can rise (the ramp more), so no such neighbour's surface is cut off
        // at the top of what is read; one cut off there rises the ramp or more past any of it, and joins none.
        // And from where a neighbour joined to ground near the limit can sink to, so every read blends the same.
        var y1 = (int)Math.Floor(limit + _ramp) + 1;
        var low = Math.Min(y0, (int)Math.Floor(limit - _ramp)) - 1;
        var ground = Surface(in footprint, low, y1, (int)Math.Floor(limit));
        if (bodies && _bodies is not null && _bodies.Count > 0)
        {
            var body = _bodies.HighestTop(in footprint, y0, (int)Math.Ceiling(limit) - 1);
            if (body != int.MinValue)
            {
                ground = Math.Max(ground, body);
            }
        }

        return ground;
    }

    /// <summary>
    /// Reads the surface of every column of the footprint's box and a ring of one round it (<see cref="ReadSurfaces"/>),
    /// and returns the highest point under the footprint of the surface blended between the columns' centres
    /// (<see cref="Blended"/>), or <see cref="double.NegativeInfinity"/>. A cell whose surface, and the joined
    /// neighbours', lie no higher than the best so far is skipped, so flat ground reads one cell; any other is measured at
    /// the corners, edges' middles, and middle of its square, each moved into the footprint, where the blend is highest
    /// (it is bilinear between them). With a <paramref name="reference"/> cell, a column open there under something
    /// higher reads its ground under it (<see cref="ReadSurfaces"/>).
    /// </summary>
    private double Surface(in CircleFootprint footprint, int y0, int y1, int reference = int.MaxValue)
    {
        ReadSurfaces(
            footprint.MinX - 1,
            footprint.MinZ - 1,
            footprint.MaxX - footprint.MinX + 3,
            footprint.MaxZ - footprint.MinZ + 3,
            y0,
            y1,
            double.PositiveInfinity,
            reference
        );
        var best = double.NegativeInfinity;
        double cx = footprint.CentreX,
            cz = footprint.CentreZ,
            r = footprint.Radius;
        for (var z = footprint.MinZ; z <= footprint.MaxZ; z++)
        {
            if (!footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                var own = SurfaceAt(x, z);
                if (double.IsNaN(own) || UpperBound(x, z, own) <= best)
                {
                    continue;
                }

                for (var j = 0; j <= 2; j++)
                {
                    for (var i = 0; i <= 2; i++)
                    {
                        double qx = x + 0.5 * i,
                            qz = z + 0.5 * j;
                        double ex = qx - cx,
                            ez = qz - cz;
                        var length = Math.Sqrt(ex * ex + ez * ez);
                        if (length > r)
                        {
                            // Onto the footprint's edge, a hair inside it.
                            var scale = (r - 1e-9) / length;
                            qx = cx + ex * scale;
                            qz = cz + ez * scale;
                        }

                        best = Math.Max(best, Blended(qx, qz));
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Reads the surface (<see cref="VoxelWorld.SurfaceOver"/>) of the highest ground from y0 to y1 of every column of the
    /// window, NaN where none lies there or it lies above <paramref name="limit"/>, into <see cref="_surfaces"/>. Where a
    /// column's voxels or objects have their highest top over the <paramref name="reference"/> cell and neither blocks
    /// that cell, what lies over it is a ceiling over open space, not ground (the lintel over a door, under which a body
    /// rises): their ground there is their highest top up to the reference cell, or none. A wall blocks the cell, so it
    /// reads its top as before. The mesh colliders' tops are read as they are: a mesh has no inside, so an empty cell
    /// under a triangle (a ramp's) is no sign of a ceiling.
    /// </summary>
    private void ReadSurfaces(
        int windowX,
        int windowZ,
        int width,
        int depth,
        int y0,
        int y1,
        double limit,
        int reference = int.MaxValue
    )
    {
        _surfaceX = windowX;
        _surfaceZ = windowZ;
        _surfaceWidth = width;
        _surfaceDepth = depth;
        var tops = _columnTops.AsSpan(0, width * depth);
        if (_world is null)
        {
            tops.Fill(int.MinValue);
        }
        else
        {
            WorldColumnTops(windowX, windowZ, width, depth, y0, y1, tops);
        }

        var meshTops = _meshTops.AsSpan(0, width * depth);
        var meshes = _meshes is not null && !_meshes.Empty;
        if (meshes)
        {
            _meshes!.ColumnTops(windowX, windowZ, width, depth, y0, y1, meshTops);
        }

        var objectTops = _objectTops.AsSpan(0, width * depth);
        var objects = Objects is { Empty: false };
        if (objects)
        {
            Objects!.ColumnTops(windowX, windowZ, width, depth, y0, y1, objectTops);
        }

        // The voxels' and the objects' tops up to the reference cell, read only where some column's lie over it.
        var under = false;
        if (reference >= y0 && reference < y1)
        {
            for (var i = 0; i < tops.Length && !under; i++)
            {
                under = tops[i] > reference || (objects && objectTops[i] > reference);
            }
        }

        var topsUnder = _columnTopsUnder.AsSpan(0, width * depth);
        var objectTopsUnder = _objectTopsUnder.AsSpan(0, width * depth);
        if (under)
        {
            if (_world is null)
            {
                topsUnder.Fill(int.MinValue);
            }
            else
            {
                WorldColumnTops(windowX, windowZ, width, depth, y0, reference, topsUnder);
            }

            if (objects)
            {
                Objects!.ColumnTops(windowX, windowZ, width, depth, y0, reference, objectTopsUnder);
            }
        }

        for (var i = 0; i < tops.Length; i++)
        {
            // The highest of the sources' tops, its surface read from whichever has it (all that meet there).
            var worldTop = tops[i];
            var meshTop = meshes ? meshTops[i] : int.MinValue;
            var objectTop = objects ? objectTops[i] : int.MinValue;
            if (under && Math.Max(worldTop, objectTop) > reference)
            {
                var worldUnder = topsUnder[i];
                var objectUnder = objects ? objectTopsUnder[i] : int.MinValue;
                if (Math.Max(worldUnder, objectUnder) < reference)
                {
                    // Open at the reference cell: a ceiling over it, and the ground (if any) under it.
                    worldTop = worldUnder;
                    objectTop = objectUnder;
                }
            }

            var top = Math.Max(worldTop, Math.Max(meshTop, objectTop));

            int x = windowX + i % width,
                z = windowZ + i / width;
            var surface =
                top == int.MinValue
                    ? double.NaN
                    : SurfaceOver(x, top, z, worldTop == top, meshTop == top, objectTop == top);
            // A mesh's surface may lie more than a cell under its top (a steep triangle); where it lies within half a
            // cell over the voxels' top or under it, the voxels' ground may be the higher (a mesh rock half buried).
            if (Math.Max(meshTop, objectTop) > worldTop && worldTop != int.MinValue && !(surface >= worldTop + 0.5))
            {
                var ground = WorldSurfaceOver(x, worldTop, z);
                surface = double.IsNaN(surface) ? ground : Math.Max(surface, ground);
            }

            _surfaces[i] = surface <= limit ? surface : double.NaN;
        }
    }

    /// <summary>
    /// The surface over column (x, z)'s ground whose top is <paramref name="top"/>: <see cref="VoxelWorld.SurfaceOver"/>
    /// where the voxels' top is it (<paramref name="voxels"/>), the mesh colliders' (<c>MeshColumns.SurfaceOver</c>)
    /// where theirs is (<paramref name="meshes"/>), the higher of the two where both are; read once for each column and
    /// top within a move or query (<see cref="ForgetSurfaces"/>).
    /// </summary>
    private double SurfaceOver(int x, int top, int z, bool voxels, bool meshes, bool objects = false)
    {
        // Column x and z 21 bits each, the top 19, and which sources have it. A move or query reads columns far fewer than
        // 2^21 cells apart, and the cache is forgotten between them, so the bits left out never tell two apart.
        var key =
            (((long)x & 0x1FFFFF) << 43)
            | (((long)z & 0x1FFFFF) << 22)
            | (((long)top & 0x7FFFF) << 3)
            | (voxels ? 1L : 0)
            | (meshes ? 2L : 0)
            | (objects ? 4L : 0);
        var slot = (x & (SurfaceCacheSide - 1)) | ((z & (SurfaceCacheSide - 1)) << SurfaceCacheSideBits);
        if (_cachedStamps[slot] == _cacheStamp && _cachedKeys[slot] == key)
        {
            return _cachedSurfaces[slot];
        }

        var surface = voxels && _world is not null ? WorldSurfaceOver(x, top, z) : double.NaN;
        if (meshes && _meshes is not null)
        {
            var mesh = _meshes.SurfaceOver(x, top, z);
            surface = double.IsNaN(surface) ? mesh : Math.Max(surface, mesh);
        }

        if (objects && Objects is not null)
        {
            var objectSurface = Objects.SurfaceOver(x, top, z);
            surface = double.IsNaN(surface) ? objectSurface : Math.Max(surface, objectSurface);
        }

        _cachedStamps[slot] = _cacheStamp;
        _cachedKeys[slot] = key;
        _cachedSurfaces[slot] = surface;
        return surface;
    }

    /// <summary>The voxels' column tops of a window, in the mover's cells (<see cref="VoxelWorld.ColumnTops"/>); with terrain.</summary>
    private void WorldColumnTops(int windowX, int windowZ, int width, int depth, int y0, int y1, Span<int> tops)
    {
        if (_fine is not null)
        {
            _fine.ColumnTops(windowX, windowZ, width, depth, y0, y1, tops);
        }
        else
        {
            _world!.ColumnTops(windowX, windowZ, width, depth, y0, y1, in _passable, tops);
        }
    }

    /// <summary>The voxels' surface over a column's top, in the mover's cells (<see cref="VoxelWorld.SurfaceOver"/>); with terrain.</summary>
    private double WorldSurfaceOver(int x, int top, int z) =>
        _fine?.SurfaceOver(x, top, z) ?? _world!.SurfaceOver(x, top, z, in _passable);

    /// <summary>Forgets the surfaces read: the voxels may have changed since (at the start of each move and query).</summary>
    private void ForgetSurfaces()
    {
        _fine?.Begin(_ramp, in _passable);
        _cacheStamp++;
        if (_cacheStamp == int.MaxValue)
        {
            Array.Clear(_cachedStamps);
            _cacheStamp = 1;
        }
    }

    /// <summary>The surface read for column (x, z), or NaN outside the window read.</summary>
    private double SurfaceAt(int x, int z)
    {
        x -= _surfaceX;
        z -= _surfaceZ;
        return (uint)x < (uint)_surfaceWidth && (uint)z < (uint)_surfaceDepth
            ? _surfaces[x + _surfaceWidth * z]
            : double.NaN;
    }

    /// <summary>The highest the blend can be in column (x, z): its own surface, or a joined neighbour's.</summary>
    private double UpperBound(int x, int z, double own)
    {
        var bound = own;
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var other = SurfaceAt(x + dx, z + dz);
                if (other > bound && other - own < _ramp)
                {
                    bound = other;
                }
            }
        }

        return bound;
    }

    /// <summary>
    /// The ground's surface at (x, z), in cells: the surfaces at the four columns' centres round it blended bilinearly,
    /// each that is missing, or differs from that of the column holding the point by the ramp or more (a ledge or a wall),
    /// taken as that column's own. NaN where that column has no ground.
    /// </summary>
    private double Blended(double x, double z)
    {
        var own = SurfaceAt((int)Math.Floor(x), (int)Math.Floor(z));
        if (double.IsNaN(own))
        {
            return double.NegativeInfinity;
        }

        var ax = (int)Math.Floor(x - 0.5);
        var az = (int)Math.Floor(z - 0.5);
        double fx = x - 0.5 - ax,
            fz = z - 0.5 - az;
        var a = Joined(ax, az, own);
        var b = Joined(ax + 1, az, own);
        var c = Joined(ax, az + 1, own);
        var d = Joined(ax + 1, az + 1, own);
        return (a * (1 - fx) + b * fx) * (1 - fz) + (c * (1 - fx) + d * fx) * fz;
    }

    /// <summary>Column (x, z)'s surface when it joins <paramref name="own"/> (they differ by less than the ramp), else own.</summary>
    private double Joined(int x, int z, double own)
    {
        var surface = SurfaceAt(x, z);
        return Math.Abs(surface - own) < _ramp ? surface : own;
    }

    /// <summary>The highest top from y0 to y1 under a footprint, of the voxels, the mesh colliders, and the loose bodies.</summary>
    private int Top(in CircleFootprint footprint, int y0, int y1)
    {
        var top = TerrainTop(in footprint, y0, y1);
        return _bodies is null || _bodies.Count == 0 ? top : Math.Max(top, _bodies.HighestTop(in footprint, y0, y1));
    }

    /// <summary>The lowest bottom from y0 to y1 over a footprint, of the voxels, the mesh colliders, and the loose bodies.</summary>
    private int Bottom(in CircleFootprint footprint, int y0, int y1)
    {
        var bottom =
            _fine?.LowestBottom(in footprint, y0, y1)
            ?? _world?.LowestBottom(in footprint, y0, y1, in _passable)
            ?? int.MaxValue;
        if (_meshes is not null)
        {
            bottom = Math.Min(bottom, _meshes.LowestBottom(in footprint, y0, y1));
        }

        if (Objects is not null)
        {
            bottom = Math.Min(bottom, Objects.LowestBottom(in footprint, y0, y1));
        }

        return _bodies is null || _bodies.Count == 0
            ? bottom
            : Math.Min(bottom, _bodies.LowestBottom(in footprint, y0, y1));
    }

    /// <summary>The highest top from y0 to y1 under a footprint, of the voxels, the mesh colliders, and the objects.</summary>
    private int TerrainTop(in CircleFootprint footprint, int y0, int y1)
    {
        var top =
            _fine?.HighestTop(in footprint, y0, y1)
            ?? _world?.HighestTop(in footprint, y0, y1, in _passable)
            ?? int.MinValue;
        top = _meshes is null ? top : Math.Max(top, _meshes.HighestTop(in footprint, y0, y1));
        return Objects is null ? top : Math.Max(top, Objects.HighestTop(in footprint, y0, y1));
    }

    /// <summary>
    /// True when the voxel columns of the box are loaded and the objects that may stand in it are known; always without
    /// voxel terrain.
    /// </summary>
    private bool Resident(int x0, int z0, int x1, int z1) =>
        (_fine?.ColumnsResident(x0, z0, x1, z1) ?? _world?.ColumnsResident(x0, z0, x1, z1) ?? true)
        && (Objects?.Known(x0, z0, x1, z1) ?? true);

    /// <summary>
    /// The cells of a strip of a window (at most 128 wide) that hold a blocking voxel or a mesh collider's cell from y0
    /// to y1, written into <paramref name="rows"/>.
    /// </summary>
    private void TerrainCells(int windowX, int windowZ, int width, int y0, int y1, Span<UInt128> rows)
    {
        if (_world is null)
        {
            rows.Clear();
        }
        else
        {
            if (_fine is not null)
            {
                _fine.BlockedCells(windowX, windowZ, width, y0, y1, rows);
            }
            else
            {
                _world.BlockedCells(windowX, windowZ, width, y0, y1, in _passable, rows);
            }
        }

        _meshes?.BlockedCells(windowX, windowZ, width, y0, y1, rows);
        Objects?.BlockedCells(windowX, windowZ, width, y0, y1, rows);
    }

    /// <summary>The strips of 128 cells a window <paramref name="width"/> cells wide needs.</summary>
    private static int Strips(int width) => (width + StripCells - 1) / StripCells;

    /// <summary>
    /// Fills a window (<paramref name="width"/> by <paramref name="depth"/> cells, as strips of 128) with the cells that
    /// hold a blocking voxel from y0 to y1, and a loose-body voxel from <paramref name="bodyY0"/> to y1, less the
    /// loose-body cells under where the body started the move when <paramref name="leaveStart"/> is true.
    /// </summary>
    private void FillCells(
        int windowX,
        int windowZ,
        int width,
        int depth,
        int y0,
        int y1,
        Span<UInt128> window,
        bool leaveStart,
        int bodyY0 = int.MinValue,
        double feet = double.NaN,
        double allowance = 0,
        double head = double.NaN
    )
    {
        if (width > WindowCells || depth > WindowCells)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A window is at most 512 cells each way.");
        }

        var strips = Strips(width);
        for (var strip = 0; strip < strips; strip++)
        {
            TerrainCells(
                windowX + strip * StripCells,
                windowZ,
                Math.Min(StripCells, width - strip * StripCells),
                y0,
                y1,
                window.Slice(strip * depth, depth)
            );
        }

        if (_density && !double.IsNaN(feet))
        {
            OpenLowColumns(windowX, windowZ, width, depth, y0, y1, feet, allowance, head, strips, window);
        }

        bodyY0 = bodyY0 == int.MinValue ? y0 : bodyY0;
        if (
            _bodies is null
            || _bodies.Count == 0
            || !_bodies.Reaches(windowX, bodyY0, windowZ, windowX + width - 1, y1, windowZ + depth - 1)
        )
        {
            return;
        }

        var bodies = _bodyWindow.AsSpan(0, depth);
        for (var strip = 0; strip < strips; strip++)
        {
            var stripX = windowX + strip * StripCells;
            var stripWidth = Math.Min(StripCells, width - strip * StripCells);
            _bodies.BlockedCells(stripX, windowZ, stripWidth, bodyY0, y1, bodies);
            for (var row = 0; row < depth; row++)
            {
                var cells = bodies[row];
                if (leaveStart && _start.Row(windowZ + row, out var x0, out var x1))
                {
                    var from = Math.Max(x0 - stripX, 0);
                    var to = Math.Min(x1 - stripX, stripWidth - 1);
                    if (from <= to)
                    {
                        cells &= ~RangeBits(from, to);
                    }
                }

                window[strip * depth + row] |= cells;
            }
        }
    }

    /// <summary>
    /// Opens the columns of a slide's window whose highest ground from y0 to y1 has its surface (the density's, inside
    /// the voxel) under the feet and the <paramref name="allowance"/> (the edge, or for a walking body the ramp), as the
    /// body stands on, or walks up onto, the blended surface over them, and a wall is only what rises past that. A column
    /// it would walk up onto stays shut where anything blocks within the rise over <paramref name="head"/>, so a body on
    /// a ramp under a ceiling stops below it.
    /// </summary>
    private void OpenLowColumns(
        int windowX,
        int windowZ,
        int width,
        int depth,
        int y0,
        int y1,
        double feet,
        double allowance,
        double head,
        int strips,
        Span<UInt128> window
    )
    {
        var below = feet + allowance;
        var ceilings = _ceilingWindow.AsSpan(0, strips * depth);
        var anyCeiling = false;
        if (allowance > _edge)
        {
            for (var strip = 0; strip < strips; strip++)
            {
                TerrainCells(
                    windowX + strip * StripCells,
                    windowZ,
                    Math.Min(StripCells, width - strip * StripCells),
                    (int)Math.Ceiling(head - _edge),
                    (int)Math.Ceiling(head + allowance - _edge) - 1,
                    ceilings.Slice(strip * depth, depth)
                );
            }

            anyCeiling = ceilings.IndexOfAnyExcept(UInt128.Zero) >= 0;
        }

        ReadSurfaces(windowX, windowZ, width, depth, y0, y1, double.PositiveInfinity);
        for (var row = 0; row < depth; row++)
        {
            for (var strip = 0; strip < strips; strip++)
            {
                ref var cells = ref window[strip * depth + row];
                if (cells == UInt128.Zero)
                {
                    continue;
                }

                var stripX = strip * StripCells;
                for (var half = 0; half < 2; half++)
                {
                    var bits = (ulong)(cells >> (64 * half));
                    while (bits != 0)
                    {
                        var index = BitOperations.TrailingZeroCount(bits) + 64 * half;
                        bits &= bits - 1;
                        var surface = _surfaces[stripX + index + width * row];
                        if (
                            !double.IsNaN(surface)
                            && surface < below
                            && (
                                surface <= feet + _edge
                                || !anyCeiling
                                || ((ceilings[strip * depth + row] >> index) & UInt128.One) == UInt128.Zero
                            )
                        )
                        {
                            cells &= ~(UInt128.One << index);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Blocks the cells of a slide's window (<paramref name="strips"/> strips of <paramref name="depth"/> rows) outside the
    /// world's columns at every height: the map's edge is a wall to every body the mover moves (a body that walked or dug off the map would fall without end). Only slides read it, so a body
    /// outside the map (put there by <c>tp</c>) is not stuck and walks back in.
    /// </summary>
    private static void AddMapEdge(
        int first,
        int end,
        int windowX,
        int windowZ,
        int width,
        int depth,
        int strips,
        Span<UInt128> window
    )
    {
        if (windowX >= first && windowZ >= first && (long)windowX + width <= end && (long)windowZ + depth <= end)
        {
            return;
        }

        for (var strip = 0; strip < strips; strip++)
        {
            var stripX = (long)windowX + strip * StripCells;
            var stripWidth = Math.Min(StripCells, width - strip * StripCells);
            // Cells before `low` and from `high` on lie outside on x.
            var low = first - stripX;
            var high = end - stripX;
            var outside = UInt128.Zero;
            if (low > 0)
            {
                outside |= RangeBits(0, (int)Math.Min(low, stripWidth) - 1);
            }

            if (high < stripWidth)
            {
                outside |= RangeBits((int)Math.Max(high, 0), stripWidth - 1);
            }

            var all = RangeBits(0, stripWidth - 1);
            for (var row = 0; row < depth; row++)
            {
                var z = (long)windowZ + row;
                window[strip * depth + row] |= z < first || z >= end ? all : outside;
            }
        }
    }

    /// <summary>
    /// True when the body at (px, py, pz) stands on a slope steeper than the limit. First, it must rest on a thin rim: the
    /// voxels whose tops lie within <c>_supportDepth</c> below the feet reach less than
    /// <c>_supportDepth</c> / <paramref name="slope"/> in from the body's edge (on a slope of angle a they form a
    /// band <c>_supportDepth</c> / tan a wide along the high side, whichever way the body faces). Then the ground
    /// must rise more steeply than the limit within <c>_lookAhead</c> in one of eight directions; a face that rises
    /// by the whole step there counts too, so a body on the rim of a near-vertical crag cannot jump up it. A stone under
    /// the rim or a ledge's edge has nothing rising beside it. (A body on a ledge narrower than about 30 cm against a wall
    /// is steep too, and slides off.) Writes the horizontal direction from the rim's middle to the centre, the way the
    /// body would slide.
    /// </summary>
    private bool IsSteep(
        double px,
        double py,
        double pz,
        double r,
        double step,
        double slope,
        out double downX,
        out double downZ
    )
    {
        downX = 0;
        downZ = 0;
        if (_density)
        {
            return IsSteepOnSurfaces(px, py, pz, r, step, slope, out downX, out downZ);
        }

        var feet = (int)Math.Floor(py + _edge);
        // On open ground the cells near the centre hold the body up: a circle of up to 16 voxels there, half a voxel
        // inside the reach the rim test asks for, settles it without reading every cell under a wide body.
        var centre = Math.Min(r - _supportDepth / slope - 0.5, _centreCells);
        if (centre > 0 && Top(new CircleFootprint(px, pz, centre), feet - _supportCells, feet - 1) != int.MinValue)
        {
            return false;
        }

        var footprint = new CircleFootprint(px, pz, r);
        var width = footprint.MaxX - footprint.MinX + 1;
        var depth = footprint.MaxZ - footprint.MinZ + 1;
        var strips = Strips(width);
        var window = _steepWindow.AsSpan(0, strips * depth);
        FillCells(footprint.MinX, footprint.MinZ, width, depth, feet - _supportCells, feet - 1, window, false);
        if (HasInnerSupport(window, strips, depth, in footprint, r, slope, _supportDepth))
        {
            return false;
        }

        var nearest = double.MaxValue;
        double sumX = 0,
            sumZ = 0;
        var count = 0;
        for (var row = 0; row < depth; row++)
        {
            var any = UInt128.Zero;
            for (var strip = 0; strip < strips; strip++)
            {
                any |= window[strip * depth + row];
            }

            var z = footprint.MinZ + row;
            if (any == UInt128.Zero || !footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            // The nearest distance from the centre to each supporting cell's square.
            var dz =
                pz < z ? z - pz
                : pz > z + 1 ? pz - (z + 1)
                : 0;
            // The supporting cells from x0 to x1, in order of x.
            for (var strip = (x0 - footprint.MinX) / StripCells; strip <= (x1 - footprint.MinX) / StripCells; strip++)
            {
                var stripX = footprint.MinX + strip * StripCells;
                var cells =
                    window[strip * depth + row]
                    & RangeBits(Math.Max(x0 - stripX, 0), Math.Min(x1 - stripX, StripCells - 1));
                for (var half = 0; half < 2; half++)
                {
                    var bits = (ulong)(cells >> (64 * half));
                    while (bits != 0)
                    {
                        var x = stripX + BitOperations.TrailingZeroCount(bits) + 64 * half;
                        bits &= bits - 1;
                        var dx =
                            px < x ? x - px
                            : px > x + 1 ? px - (x + 1)
                            : 0;
                        nearest = Math.Min(nearest, Math.Sqrt(dx * dx + dz * dz));
                        sumX += x + 0.5;
                        sumZ += z + 0.5;
                        count++;
                    }
                }
            }
        }

        if (count == 0)
        {
            return false;
        }

        if (r - nearest >= _supportDepth / slope)
        {
            return false;
        }

        // A thin rim: steep only if the ground keeps rising beside the body, as a slope does.
        var limit = _lookAhead * slope + _oneStep;
        var wall = (int)Math.Floor(feet + step);
        var rises = false;
        for (var direction = 0; direction < 8 && !rises; direction++)
        {
            var top = Top(
                new CircleFootprint(
                    px + DirectionX[direction] * _lookAhead,
                    pz + DirectionZ[direction] * _lookAhead,
                    r
                ),
                feet,
                wall - 1
            );
            rises = top != int.MinValue && top - feet > limit;
        }

        if (!rises)
        {
            return false;
        }

        double ax = px - sumX / count,
            az = pz - sumZ / count;
        var length = Math.Sqrt(ax * ax + az * az);
        if (length > 1e-6)
        {
            downX = ax / length;
            downZ = az / length;
        }

        return true;
    }

    /// <summary>
    /// <see cref="IsSteep"/> on the columns' surfaces (density): the ground that holds the body up is the columns under it
    /// whose surface lies no more than <c>_supportDepth</c> under the feet, and the rise beside it the blended ground
    /// <c>_lookAhead</c> away.
    /// </summary>
    private bool IsSteepOnSurfaces(
        double px,
        double py,
        double pz,
        double r,
        double step,
        double slope,
        out double downX,
        out double downZ
    )
    {
        downX = 0;
        downZ = 0;
        var low = py - _supportDepth - _edge;
        var centre = Math.Min(r - _supportDepth / slope - 0.5, _centreCells);
        if (centre > 0 && Ground(new CircleFootprint(px, pz, centre), (int)Math.Floor(low), py + _edge) >= low)
        {
            return false;
        }

        var footprint = new CircleFootprint(px, pz, r);
        ReadSurfaces(
            footprint.MinX,
            footprint.MinZ,
            footprint.MaxX - footprint.MinX + 1,
            footprint.MaxZ - footprint.MinZ + 1,
            (int)Math.Floor(low) - 1,
            (int)Math.Floor(py + _edge),
            py + _edge
        );
        var nearest = double.MaxValue;
        double sumX = 0,
            sumZ = 0;
        var count = 0;
        for (var z = footprint.MinZ; z <= footprint.MaxZ; z++)
        {
            if (!footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            var dz =
                pz < z ? z - pz
                : pz > z + 1 ? pz - (z + 1)
                : 0;
            for (var x = x0; x <= x1; x++)
            {
                if (!(SurfaceAt(x, z) >= low))
                {
                    continue;
                }

                var dx =
                    px < x ? x - px
                    : px > x + 1 ? px - (x + 1)
                    : 0;
                nearest = Math.Min(nearest, Math.Sqrt(dx * dx + dz * dz));
                sumX += x + 0.5;
                sumZ += z + 0.5;
                count++;
            }
        }

        if (count == 0 || r - nearest >= _supportDepth / slope)
        {
            return false;
        }

        // A thin rim: steep only if the ground keeps rising beside the body, as a slope does.
        var limit = _lookAhead * slope + _oneStep;
        var rises = false;
        for (var direction = 0; direction < 8 && !rises; direction++)
        {
            var top = Ground(
                new CircleFootprint(
                    px + DirectionX[direction] * _lookAhead,
                    pz + DirectionZ[direction] * _lookAhead,
                    r
                ),
                (int)Math.Floor(py),
                py + step
            );
            rises = top - py > limit;
        }

        if (!rises)
        {
            return false;
        }

        double ax = px - sumX / count,
            az = pz - sumZ / count;
        var length = Math.Sqrt(ax * ax + az * az);
        if (length > 1e-6)
        {
            downX = ax / length;
            downZ = az / length;
        }

        return true;
    }

    /// <summary>
    /// True when a supporting cell of the window lies far enough inside the footprint's edge that the body does not rest
    /// on a thin rim (<see cref="IsSteep"/>'s first test, the same comparison): on open ground one is found in the first
    /// rows looked at, so a wide body does not measure every cell under it.
    /// </summary>
    private static bool HasInnerSupport(
        ReadOnlySpan<UInt128> window,
        int strips,
        int depth,
        in CircleFootprint footprint,
        double r,
        double slope,
        double supportDepth
    )
    {
        var inner = r - supportDepth / slope;
        if (inner < 0)
        {
            return false;
        }

        double px = footprint.CentreX,
            pz = footprint.CentreZ;
        // A voxel wider, so no cell whose square lies within the inner reach is left out.
        var reach = new CircleFootprint(px, pz, inner + 1);
        var lastX = footprint.MinX + strips * StripCells - 1;
        for (var z = Math.Max(reach.MinZ, footprint.MinZ); z <= Math.Min(reach.MaxZ, footprint.MaxZ); z++)
        {
            if (!reach.Row(z, out var x0, out var x1))
            {
                continue;
            }

            x0 = Math.Max(x0, footprint.MinX);
            x1 = Math.Min(x1, lastX);
            var row = z - footprint.MinZ;
            var dz =
                pz < z ? z - pz
                : pz > z + 1 ? pz - (z + 1)
                : 0;
            for (var strip = (x0 - footprint.MinX) / StripCells; strip <= (x1 - footprint.MinX) / StripCells; strip++)
            {
                var stripX = footprint.MinX + strip * StripCells;
                var cells =
                    window[strip * depth + row]
                    & RangeBits(Math.Max(x0 - stripX, 0), Math.Min(x1 - stripX, StripCells - 1));
                for (var half = 0; half < 2; half++)
                {
                    var bits = (ulong)(cells >> (64 * half));
                    while (bits != 0)
                    {
                        var x = stripX + BitOperations.TrailingZeroCount(bits) + 64 * half;
                        bits &= bits - 1;
                        var dx =
                            px < x ? x - px
                            : px > x + 1 ? px - (x + 1)
                            : 0;
                        if (r - Math.Sqrt(dx * dx + dz * dz) >= supportDepth / slope)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>The x of eight directions around a body, one every 45 degrees.</summary>
    private static readonly double[] DirectionX =
    [
        1,
        0.70710678118654752,
        0,
        -0.70710678118654752,
        -1,
        -0.70710678118654752,
        0,
        0.70710678118654752,
    ];

    /// <summary>The z of the same eight directions.</summary>
    private static readonly double[] DirectionZ =
    [
        0,
        0.70710678118654752,
        1,
        0.70710678118654752,
        0,
        -0.70710678118654752,
        -1,
        -0.70710678118654752,
    ];

    /// <summary>
    /// True when the ground keeps rising beyond a step up from (px, pz) to (sx, sz) onto <paramref name="feet"/>: a body
    /// <c>_lookAhead</c> further along the move would stand more than the slope limit higher (and a voxel more, for
    /// the 1 cm steps), or meet a wall. A ledge's top stays level; a hillside does not, and neither does a near-vertical
    /// face a body edges along.
    /// </summary>
    private bool KeepsRising(
        double px,
        double pz,
        double sx,
        double sz,
        double feet,
        double r,
        double step,
        double slope
    )
    {
        double mx = sx - px,
            mz = sz - pz;
        var moved = Math.Sqrt(mx * mx + mz * mz);
        if (moved < 1e-9)
        {
            return false;
        }

        var ahead = Ground(
            new CircleFootprint(sx + mx / moved * _lookAhead, sz + mz / moved * _lookAhead, r),
            (int)Math.Floor(feet - step),
            Math.Floor(feet + step + _edge)
        );
        return ahead - feet > _lookAhead * slope + _oneStep;
    }

    /// <summary>True when a blocking voxel lies at most one voxel under the feet.</summary>
    private bool IsGrounded(double px, double py, double pz, double r)
    {
        var top = Ground(new CircleFootprint(px, pz, r), (int)Math.Floor(py - _oneStep - _edge), py + _edge);
        return py - top <= _oneStep + _edge;
    }

    /// <summary>How far the body can rise, up to <paramref name="step"/>, before its top meets something.</summary>
    private double Headroom(double px, double py, double pz, double r, double h, double step)
    {
        var bodyTop = py + h;
        var bottom = Bottom(
            new CircleFootprint(px, pz, r),
            (int)Math.Ceiling(bodyTop - _edge),
            (int)Math.Ceiling(bodyTop + step - _edge) - 1
        );
        return bottom == int.MaxValue ? step : Math.Min(step, bottom - _skin - bodyTop);
    }

    /// <summary>
    /// Slides the circle at (px, pz) by (dx, dz) through the cells that block anywhere from <paramref name="feet"/> to
    /// the body's top (loose bodies from <paramref name="bodyFeet"/>, which a step up leaves where the feet were, so
    /// bodies are pushed, not stepped onto): moves to each wall less the skin, then along it with the part of the move
    /// that does not point into it. Writes each wall's normal to <paramref name="normals"/> and returns how many it met.
    /// </summary>
    private int Slide(
        ref double px,
        ref double pz,
        double dx,
        double dz,
        double feet,
        double bodyFeet,
        double r,
        double h,
        bool ramps,
        Span<Vector2> normals
    )
    {
        var length = Math.Sqrt(dx * dx + dz * dz);
        var margin = r + length + 1;
        var windowX = (int)Math.Floor(px - margin);
        var windowZ = (int)Math.Floor(pz - margin);
        var width = (int)Math.Floor(px + margin) - windowX + 1;
        var depth = (int)Math.Floor(pz + margin) - windowZ + 1;
        var strips = Strips(width);
        var window = _window.AsSpan(0, strips * depth);
        FillCells(
            windowX,
            windowZ,
            width,
            depth,
            (int)Math.Floor(feet + _edge),
            (int)Math.Ceiling(feet + h - _edge) - 1,
            window,
            _ignoreStart,
            (int)Math.Floor(bodyFeet + _edge),
            feet,
            ramps ? _ramp : _edge,
            feet + h
        );
        AddMapEdge(_edgeFirst, _edgeEnd, windowX, windowZ, width, depth, strips, window);

        var count = 0;
        for (var iteration = 0; iteration < SlideIterations; iteration++)
        {
            var remaining = Math.Sqrt(dx * dx + dz * dz);
            if (remaining < 1e-9)
            {
                break;
            }

            if (
                !EarliestHit(
                    window,
                    strips,
                    depth,
                    windowX,
                    windowZ,
                    px,
                    pz,
                    dx,
                    dz,
                    r,
                    _overlap,
                    out var t,
                    out var nx,
                    out var nz
                )
            )
            {
                px += dx;
                pz += dz;
                break;
            }

            // Stop short of the wall by the skin, measured along the normal.
            var approach = -(dx * nx + dz * nz) / remaining;
            var stop = approach > 0 ? Math.Max(0, t - _skin / (approach * remaining)) : t;
            px += dx * stop;
            pz += dz * stop;
            dx *= 1 - stop;
            dz *= 1 - stop;
            var into = dx * nx + dz * nz;
            if (into < 0)
            {
                dx -= nx * into;
                dz -= nz * into;
            }

            normals[count++] = new Vector2((float)nx, (float)nz);
        }

        return count;
    }

    /// <summary>
    /// The earliest point, as a fraction <paramref name="t"/> of (dx, dz), where the circle meets a blocked cell of the
    /// window (<paramref name="strips"/> strips of 128 cells, each <paramref name="depth"/> rows), and the wall's normal
    /// there. Only faces that border an empty cell and corners between two empty cells can be met first, so the rest are
    /// skipped. A circle that starts on a wall and moves into it meets it at once.
    /// </summary>
    private static bool EarliestHit(
        ReadOnlySpan<UInt128> window,
        int strips,
        int depth,
        int windowX,
        int windowZ,
        double px,
        double pz,
        double dx,
        double dz,
        double r,
        double overlap,
        out double t,
        out double nx,
        out double nz
    )
    {
        t = double.PositiveInfinity;
        nx = 0;
        nz = 0;
        var lengthSquared = dx * dx + dz * dz;
        int firstRow = Math.Max(0, (int)Math.Floor(Math.Min(pz, pz + dz) - r) - 1 - windowZ),
            lastRow = Math.Min(depth - 1, (int)Math.Floor(Math.Max(pz, pz + dz) + r) + 1 - windowZ),
            firstCell = Math.Max(0, (int)Math.Floor(Math.Min(px, px + dx) - r) - 1 - windowX),
            lastCell = Math.Min(strips * StripCells - 1, (int)Math.Floor(Math.Max(px, px + dx) + r) + 1 - windowX);
        if (firstCell > lastCell)
        {
            return false;
        }

        for (var strip = firstCell / StripCells; strip <= lastCell / StripCells; strip++)
        {
            var stripStart = strip * StripCells;
            var span = RangeBits(Math.Max(firstCell - stripStart, 0), Math.Min(lastCell - stripStart, StripCells - 1));
            var rows = window.Slice(strip * depth, depth);
            for (var row = firstRow; row <= lastRow; row++)
            {
                var occupied = rows[row];
                var near = occupied & span;
                if (near == UInt128.Zero)
                {
                    continue;
                }

                // The cells either side of the strip, for its first and last cell.
                var leftStrip = strip > 0 ? window[(strip - 1) * depth + row] : UInt128.Zero;
                var rightStrip = strip + 1 < strips ? window[(strip + 1) * depth + row] : UInt128.Zero;
                var below = row > 0 ? rows[row - 1] : UInt128.Zero;
                var above = row + 1 < depth ? rows[row + 1] : UInt128.Zero;
                var interior = occupied & (occupied << 1) & (occupied >> 1) & below & above;
                var boundary = near & ~interior;
                double cz = windowZ + row;
                for (var half = 0; half < 2; half++)
                {
                    var bits = (ulong)(boundary >> (64 * half));
                    while (bits != 0)
                    {
                        var index = BitOperations.TrailingZeroCount(bits) + 64 * half;
                        bits &= bits - 1;
                        double cx = windowX + stripStart + index;
                        var leftEmpty =
                            index == 0
                                ? (leftStrip >> (StripCells - 1)) == UInt128.Zero
                                : ((occupied >> (index - 1)) & UInt128.One) == UInt128.Zero;
                        var rightEmpty =
                            index == StripCells - 1
                                ? (rightStrip & UInt128.One) == UInt128.Zero
                                : ((occupied >> (index + 1)) & UInt128.One) == UInt128.Zero;
                        var belowEmpty = ((below >> index) & UInt128.One) == UInt128.Zero;
                        var aboveEmpty = ((above >> index) & UInt128.One) == UInt128.Zero;

                        if (dx > 0 && leftEmpty)
                        {
                            Face((cx - r - px) / dx, pz, dz, cz, dx, -1, 0, overlap, ref t, ref nx, ref nz);
                        }
                        else if (dx < 0 && rightEmpty)
                        {
                            Face((cx + 1 + r - px) / dx, pz, dz, cz, -dx, 1, 0, overlap, ref t, ref nx, ref nz);
                        }

                        if (dz > 0 && belowEmpty)
                        {
                            Face((cz - r - pz) / dz, px, dx, cx, dz, 0, -1, overlap, ref t, ref nx, ref nz);
                        }
                        else if (dz < 0 && aboveEmpty)
                        {
                            Face((cz + 1 + r - pz) / dz, px, dx, cx, -dz, 0, 1, overlap, ref t, ref nx, ref nz);
                        }

                        if (leftEmpty && belowEmpty)
                        {
                            Corner(cx, cz, px, pz, dx, dz, lengthSquared, r, overlap, ref t, ref nx, ref nz);
                        }

                        if (rightEmpty && belowEmpty)
                        {
                            Corner(cx + 1, cz, px, pz, dx, dz, lengthSquared, r, overlap, ref t, ref nx, ref nz);
                        }

                        if (leftEmpty && aboveEmpty)
                        {
                            Corner(cx, cz + 1, px, pz, dx, dz, lengthSquared, r, overlap, ref t, ref nx, ref nz);
                        }

                        if (rightEmpty && aboveEmpty)
                        {
                            Corner(cx + 1, cz + 1, px, pz, dx, dz, lengthSquared, r, overlap, ref t, ref nx, ref nz);
                        }
                    }
                }
            }
        }

        return t <= 1;
    }

    /// <summary>
    /// A face of a cell, moved out by the radius: the circle meets it at <paramref name="hit"/> when the other
    /// coordinate is then within the cell's side. <paramref name="speed"/> is the move's length towards the face.
    /// </summary>
    private static void Face(
        double hit,
        double start,
        double move,
        double low,
        double speed,
        double faceX,
        double faceZ,
        double overlap,
        ref double t,
        ref double nx,
        ref double nz
    )
    {
        // A circle already up to the overlap past the face (a tenth of a voxel at 1 cm) meets it at once.
        if (hit * speed < -overlap || hit >= t)
        {
            return;
        }

        hit = Math.Max(hit, 0);
        var along = start + move * hit;
        if (along < low || along > low + 1)
        {
            return;
        }

        t = hit;
        nx = faceX;
        nz = faceZ;
    }

    /// <summary>A cell corner: the circle meets it when the corner comes within the radius of the centre.</summary>
    private static void Corner(
        double kx,
        double kz,
        double px,
        double pz,
        double dx,
        double dz,
        double lengthSquared,
        double r,
        double overlap,
        ref double t,
        ref double nx,
        ref double nz
    )
    {
        double ex = px - kx,
            ez = pz - kz;
        var b = ex * dx + ez * dz;
        if (b >= 0)
        {
            return;
        }

        var c = ex * ex + ez * ez - r * r;
        double hit;
        if (c <= 0)
        {
            // Already on or up to the overlap inside the corner's reach.
            if (Math.Sqrt(ex * ex + ez * ez) < r - overlap)
            {
                return;
            }

            hit = 0;
        }
        else
        {
            var discriminant = b * b - lengthSquared * c;
            if (discriminant < 0)
            {
                return;
            }

            hit = (-b - Math.Sqrt(discriminant)) / lengthSquared;
        }

        if (hit >= t)
        {
            return;
        }

        var cx = px + dx * hit - kx;
        var cz = pz + dz * hit - kz;
        var length = Math.Sqrt(cx * cx + cz * cz);
        if (length <= 0)
        {
            return;
        }

        t = hit;
        nx = cx / length;
        nz = cz / length;
    }

    /// <summary>How far a move of (mx, mz) went along the wanted (wx, wz), in voxels.</summary>
    private static double Progress(double mx, double mz, double wx, double wz)
    {
        var length = Math.Sqrt(wx * wx + wz * wz);
        return length > 0 ? (mx * wx + mz * wz) / length : 0;
    }

    /// <summary>Takes away the part of the velocity that points into each wall.</summary>
    private static void Clip(ref double vx, ref double vz, ReadOnlySpan<Vector2> normals)
    {
        foreach (var normal in normals)
        {
            var into = vx * normal.X + vz * normal.Y;
            if (into < 0)
            {
                vx -= normal.X * into;
                vz -= normal.Y * into;
            }
        }
    }

    /// <summary>Bits <paramref name="from"/> to <paramref name="to"/> inclusive, both within 0 to 127.</summary>
    private static UInt128 RangeBits(int from, int to) =>
        (to == 127 ? UInt128.MaxValue : (UInt128.One << (to + 1)) - 1) & ~((UInt128.One << from) - 1);
}
