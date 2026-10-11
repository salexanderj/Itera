using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Assets;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Physics;

/// <summary>
/// The match's static triangle colliders that a game registers (<see cref="Add(MeshAsset, in Matrix4x4, Func{string, uint}?, in PhysicsBodySettings)"/>):
/// a mesh asset or triangles built in code, placed by a transform, each triangle with the surface tags of its material
/// (game-defined bits; <c>IVoxelSurfaceTags</c> gives the voxels' theirs). Only registered colliders collide: drawing and
/// colliding stay apart, so one model may be decoration in one place and ground in another.
/// <list type="bullet">
/// <item>Each collider has an anchor, an absolute point in whole metres near where it was placed, and keeps its
/// triangles in metres from it, so they stay precise anywhere on a large map whatever the match's origin
/// (<see cref="Origin"/>). A grid of 4 m buckets over the absolute x and z holds them, which rays
/// (<see cref="Raycast"/>, two-sided) and the character mover's queries read. The buckets are made again at the first
/// query after a collider was added or removed (that query allocates); every other query allocates nothing.</item>
/// <item>Each collider is also a Bepu mesh static posed at its anchor's local place (its triangles wound as Bepu asks:
/// clockwise seen from the front, where glTF's are counter-clockwise), which every loose body touches whatever its voxel
/// level (<c>PhysicsWorld.AddStatic</c>), of the physics material its settings name; Bepu's meshes stop bodies from
/// their front only.</item>
/// </list>
/// Scoped to the match; add colliders once its physics exists (from a system's <c>Initialize</c> on). Main thread.
/// </summary>
/// <param name="physics">The match's physics.</param>
/// <param name="logger">Logger.</param>
/// <param name="origin">The match's origin, which local positions are measured from; null for the map's middle.</param>
public sealed partial class MeshColliders(
    PhysicsWorld physics,
    ILogger<MeshColliders> logger,
    WorldOrigin? origin = null
) : IDisposable
{
    /// <summary>The side of a bucket of the triangle grid, in metres.</summary>
    public const float BucketMetres = 4;

    private readonly List<Collider?> _colliders = [];
    private readonly Dictionary<long, List<int>> _buckets = [];
    private Vector3[] _a = [];
    private Vector3[] _b = [];
    private Vector3[] _c = [];
    private uint[] _tags = [];
    private VoxelCoord[] _anchors = [];
    private int[] _owner = [];
    private int[] _local = [];
    private int[] _stamps = [];
    private int _triangles;
    private int _stamp;
    private bool _dirty;
    private bool _disposed;

    /// <summary>The colliders registered and not removed.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// The match's origin: a transform given to <see cref="Add(MeshAsset, in Matrix4x4, Func{string, uint}?, in PhysicsBodySettings)"/> and the
    /// points of rays are local to it.
    /// </summary>
    public WorldOrigin Origin { get; } = origin ?? new WorldOrigin();

    /// <summary>Counts the changes to the colliders, so what is built from their triangles knows when to build again.</summary>
    internal int Version { get; private set; }

    /// <summary>The triangles of every collider.</summary>
    public int TriangleCount
    {
        get
        {
            Rebuild();
            return _triangles;
        }
    }

    /// <summary>
    /// Registers the triangles of <paramref name="asset"/> placed by <paramref name="transform"/> (from the asset's space
    /// to the local space of <see cref="Origin"/>, used as <c>v * M</c>), each tagged by its material's name through
    /// <paramref name="tagsOf"/> (none when null), of the physics material <paramref name="settings"/> name. A skinned
    /// asset collides as its file stores it. Returns the collider's number.
    /// </summary>
    public int Add(
        MeshAsset asset,
        in Matrix4x4 transform,
        Func<string, uint>? tagsOf = null,
        in PhysicsBodySettings settings = default
    )
    {
        var positions = Positions(asset, tagsOf, out var tags);
        var (anchor, offset) = AnchorOfLocal(transform.Translation);
        return Add(positions, asset.Indices, in transform, tags, anchor, offset, settings);
    }

    /// <summary>
    /// Registers the triangles of <paramref name="asset"/> at the absolute point <paramref name="at"/>, placed from it by
    /// <paramref name="transform"/> (from the asset's space to metres from <paramref name="at"/>, used as <c>v * M</c>):
    /// precise anywhere on the map whatever the origin. Tags and settings as the other overload. Returns the collider's
    /// number.
    /// </summary>
    public int Add(
        MeshAsset asset,
        in WorldPoint at,
        in Matrix4x4 transform,
        Func<string, uint>? tagsOf = null,
        in PhysicsBodySettings settings = default
    )
    {
        var positions = Positions(asset, tagsOf, out var tags);
        var (anchor, offset) = AnchorOf(at);
        return Add(positions, asset.Indices, in transform, tags, anchor, offset, settings);
    }

    /// <summary>The asset's vertex positions, and each triangle's tags by its material's name.</summary>
    private static Vector3[] Positions(MeshAsset asset, Func<string, uint>? tagsOf, out uint[] tags)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var positions = new Vector3[asset.Vertices.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = asset.Vertices[i].Position;
        }

        tags = new uint[asset.Indices.Length / 3];
        foreach (var primitive in asset.Primitives)
        {
            var tag = tagsOf?.Invoke(asset.Materials[primitive.Material].Name) ?? 0;
            for (var t = primitive.FirstIndex / 3; t < (primitive.FirstIndex + primitive.IndexCount) / 3; t++)
            {
                tags[t] = tag;
            }
        }

        return positions;
    }

    /// <summary>
    /// Registers triangles built in code: <paramref name="indices"/> three a triangle into <paramref name="positions"/>,
    /// counter-clockwise seen from the front, placed by <paramref name="transform"/> (to the local space of
    /// <see cref="Origin"/>), every one tagged <paramref name="tags"/>, of the physics material <paramref name="settings"/>
    /// name. Returns the collider's number.
    /// </summary>
    public int Add(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        in Matrix4x4 transform,
        uint tags = 0,
        in PhysicsBodySettings settings = default
    )
    {
        var all = new uint[indices.Length / 3];
        Array.Fill(all, tags);
        var (anchor, offset) = AnchorOfLocal(transform.Translation);
        return Add(positions, indices, in transform, all, anchor, offset, settings);
    }

    /// <summary>
    /// Registers triangles built in code at the absolute point <paramref name="at"/>, placed from it by
    /// <paramref name="transform"/>, every one tagged <paramref name="tags"/>, of the physics material
    /// <paramref name="settings"/> name. Returns the collider's number.
    /// </summary>
    public int Add(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        in WorldPoint at,
        in Matrix4x4 transform,
        uint tags = 0,
        in PhysicsBodySettings settings = default
    )
    {
        var all = new uint[indices.Length / 3];
        Array.Fill(all, tags);
        var (anchor, offset) = AnchorOf(at);
        return Add(positions, indices, in transform, all, anchor, offset, settings);
    }

    /// <summary>
    /// Changes collider <paramref name="collider"/>'s settings (its physics material), from the next step on. Main thread,
    /// between steps.
    /// </summary>
    public void SetSettings(int collider, in PhysicsBodySettings settings)
    {
        if ((uint)collider >= (uint)_colliders.Count || _colliders[collider] is not { } found)
        {
            throw new ArgumentOutOfRangeException(nameof(collider), collider, "No such collider.");
        }

        physics.CheckMaterial(settings.Material);
        found.Settings = settings;
        if (found.HasStatic)
        {
            physics.SetMaterial(found.Static, settings.Material);
        }
    }

    /// <summary>Collider <paramref name="collider"/>'s settings.</summary>
    public PhysicsBodySettings SettingsOf(int collider) =>
        (uint)collider < (uint)_colliders.Count && _colliders[collider] is { } found ? found.Settings : default;

    /// <summary>
    /// The anchor of a collider placed at the local point <paramref name="local"/> (whole metres nearest it) and what to
    /// add to its local triangles to have them from the anchor.
    /// </summary>
    private (VoxelCoord Anchor, Vector3 Offset) AnchorOfLocal(Vector3 local)
    {
        var anchor = WholeMetres(Origin.ToWorld(local));
        return (anchor, -Origin.ToLocal(anchor));
    }

    /// <summary>
    /// The anchor of a collider placed at the absolute point <paramref name="at"/> (whole metres nearest it) and what to
    /// add to its triangles, given from <paramref name="at"/>, to have them from the anchor.
    /// </summary>
    private static (VoxelCoord Anchor, Vector3 Offset) AnchorOf(in WorldPoint at)
    {
        if (!at.IsFinite)
        {
            throw new ArgumentException($"A collider's point must be finite ({at}).", nameof(at));
        }

        var anchor = WholeMetres(at);
        var from = WorldPoint.FromCentimetres(anchor);
        return (anchor, new Vector3((float)(at.X - from.X), (float)(at.Y - from.Y), (float)(at.Z - from.Z)));
    }

    /// <summary>The whole metres nearest <paramref name="point"/>, in centimetres.</summary>
    private static VoxelCoord WholeMetres(in WorldPoint point) =>
        new(
            (int)Math.Round(point.X) * VoxelConstants.CentimetresPerMetre,
            (int)Math.Round(point.Y) * VoxelConstants.CentimetresPerMetre,
            (int)Math.Round(point.Z) * VoxelConstants.CentimetresPerMetre
        );

    /// <summary>Removes collider <paramref name="collider"/>; its number is not used again.</summary>
    public void Remove(int collider)
    {
        if ((uint)collider >= (uint)_colliders.Count || _colliders[collider] is not { } removed)
        {
            return;
        }

        RemoveStatic(removed);
        _colliders[collider] = null;
        Count--;
        Version++;
        _dirty = true;
    }

    /// <summary>
    /// The nearest triangle a ray from <paramref name="origin"/> (local to <see cref="Origin"/>) along
    /// <paramref name="direction"/> (any length but zero) meets within <paramref name="maxMetres"/>, from either side.
    /// Allocates nothing once the buckets are made.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxMetres, out SurfaceHit hit)
    {
        hit = default;
        var length = direction.Length();
        if (!(length > 0) || !float.IsFinite(length) || !(maxMetres > 0))
        {
            return false;
        }

        Rebuild();
        if (_triangles == 0)
        {
            return false;
        }

        var d = direction / length;
        var stamp = NextStamp();
        var best = maxMetres;
        var bestTriangle = -1;

        // Walk the buckets the ray's path over the absolute x and z crosses, nearest first, until the nearest hit lies
        // before the next bucket; each triangle is met from the ray's start measured from its collider's anchor.
        double ox = Origin.WorldX(origin.X),
            oy = Origin.WorldY(origin.Y),
            oz = Origin.WorldZ(origin.Z);
        var bx = Bucket(ox);
        var bz = Bucket(oz);
        var stepX = d.X > 0 ? 1 : -1;
        var stepZ = d.Z > 0 ? 1 : -1;
        var nextX =
            Math.Abs(d.X) > 1e-9f
                ? ((bx + (stepX > 0 ? 1 : 0)) * (double)BucketMetres - ox) / d.X
                : double.PositiveInfinity;
        var nextZ =
            Math.Abs(d.Z) > 1e-9f
                ? ((bz + (stepZ > 0 ? 1 : 0)) * (double)BucketMetres - oz) / d.Z
                : double.PositiveInfinity;
        var deltaX = Math.Abs(d.X) > 1e-9f ? BucketMetres / Math.Abs(d.X) : double.PositiveInfinity;
        var deltaZ = Math.Abs(d.Z) > 1e-9f ? BucketMetres / Math.Abs(d.Z) : double.PositiveInfinity;
        while (true)
        {
            if (_buckets.TryGetValue(Key(bx, bz), out var bucket))
            {
                foreach (var triangle in bucket)
                {
                    if (_stamps[triangle] == stamp)
                    {
                        continue;
                    }

                    _stamps[triangle] = stamp;
                    var anchor = _anchors[triangle];
                    var start = new Vector3(
                        (float)(ox - anchor.X / (double)VoxelConstants.CentimetresPerMetre),
                        (float)(oy - anchor.Y / (double)VoxelConstants.CentimetresPerMetre),
                        (float)(oz - anchor.Z / (double)VoxelConstants.CentimetresPerMetre)
                    );
                    if (Intersect(start, d, _a[triangle], _b[triangle], _c[triangle], out var t) && t < best)
                    {
                        best = t;
                        bestTriangle = triangle;
                    }
                }
            }

            var exit = Math.Min(nextX, nextZ);
            if (exit >= best)
            {
                break;
            }

            if (nextX < nextZ)
            {
                bx += stepX;
                nextX += deltaX;
            }
            else
            {
                bz += stepZ;
                nextZ += deltaZ;
            }
        }

        if (bestTriangle < 0)
        {
            return false;
        }

        var normal = Vector3.Normalize(
            Vector3.Cross(_b[bestTriangle] - _a[bestTriangle], _c[bestTriangle] - _a[bestTriangle])
        );
        if (Vector3.Dot(normal, d) > 0)
        {
            normal = -normal;
        }

        hit = new SurfaceHit(
            origin + d * best,
            normal,
            best,
            _tags[bestTriangle],
            SurfaceSource.Mesh,
            0,
            _owner[bestTriangle],
            _local[bestTriangle]
        );
        return true;
    }

    /// <summary>
    /// The triangles whose buckets meet the box from (<paramref name="minX"/>, <paramref name="minZ"/>) to
    /// (<paramref name="maxX"/>, <paramref name="maxZ"/>) over the absolute x and z, in metres, each once, into
    /// <paramref name="into"/> (cleared first). Allocates nothing once the buckets and the list have grown.
    /// </summary>
    internal void Triangles(double minX, double minZ, double maxX, double maxZ, List<int> into)
    {
        into.Clear();
        Rebuild();
        if (_triangles == 0)
        {
            return;
        }

        var stamp = NextStamp();
        for (var x = Bucket(minX); x <= Bucket(maxX); x++)
        {
            for (var z = Bucket(minZ); z <= Bucket(maxZ); z++)
            {
                if (!_buckets.TryGetValue(Key(x, z), out var bucket))
                {
                    continue;
                }

                foreach (var triangle in bucket)
                {
                    if (_stamps[triangle] != stamp)
                    {
                        _stamps[triangle] = stamp;
                        into.Add(triangle);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Triangle <paramref name="triangle"/> (from <see cref="Triangles"/>) in metres from its collider's anchor, the
    /// anchor (absolute centimetres, whole metres), and its tags.
    /// </summary>
    internal void Triangle(
        int triangle,
        out Vector3 a,
        out Vector3 b,
        out Vector3 c,
        out VoxelCoord anchor,
        out uint tags,
        out int collider
    )
    {
        a = _a[triangle];
        b = _b[triangle];
        c = _c[triangle];
        anchor = _anchors[triangle];
        tags = _tags[triangle];
        collider = _owner[triangle];
    }

    /// <summary>Removes every collider's static from the physics, while it still exists. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var collider in _colliders)
        {
            if (collider is not null)
            {
                RemoveStatic(collider);
            }
        }

        _colliders.Clear();
        _buckets.Clear();
        Count = 0;
    }

    private int Add(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        in Matrix4x4 transform,
        uint[] tags,
        VoxelCoord anchor,
        Vector3 offset,
        in PhysicsBodySettings settings
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException("A collider's indices come three a triangle.", nameof(indices));
        }

        var count = indices.Length / 3;
        var collider = new Collider(new Vector3[count], new Vector3[count], new Vector3[count], tags, anchor)
        {
            Settings = settings,
        };
        for (var t = 0; t < count; t++)
        {
            collider.A[t] = Vector3.Transform(positions[(int)indices[t * 3]], transform) + offset;
            collider.B[t] = Vector3.Transform(positions[(int)indices[t * 3 + 1]], transform) + offset;
            collider.C[t] = Vector3.Transform(positions[(int)indices[t * 3 + 2]], transform) + offset;
        }

        AddStatic(collider);
        _colliders.Add(collider);
        Count++;
        Version++;
        _dirty = true;
        LogAdded(_colliders.Count - 1, count);
        return _colliders.Count - 1;
    }

    /// <summary>The collider's Bepu mesh static, its triangles wound clockwise seen from the front, as Bepu asks.</summary>
    private void AddStatic(Collider collider)
    {
        if (collider.A.Length == 0)
        {
            return;
        }

        if (!physics.IsInitialized)
        {
            throw new InvalidOperationException(
                "Mesh colliders are added once the match's physics exists: from a system's Initialize on."
            );
        }

        var pool = physics.Pool;
        pool.Take<Triangle>(collider.A.Length, out var triangles);
        for (var t = 0; t < collider.A.Length; t++)
        {
            triangles[t] = new Triangle(collider.A[t], collider.C[t], collider.B[t]);
        }

        var simulation = physics.Simulation;
        var shape = simulation.Shapes.Add(new Mesh(triangles, Vector3.One, pool));
        collider.Shape = shape;
        collider.Static = physics.AddStatic(
            new StaticDescription(Origin.ToLocal(collider.Anchor), Quaternion.Identity, shape),
            collider.Settings
        );
        collider.HasStatic = true;
    }

    private void RemoveStatic(Collider collider)
    {
        if (!collider.HasStatic)
        {
            return;
        }

        collider.HasStatic = false;
        // After the match's physics went, its pool went with every shape in it.
        if (!physics.IsInitialized)
        {
            return;
        }

        physics.RemoveStatic(collider.Static);
        physics.Simulation.Shapes.RemoveAndDispose(collider.Shape, physics.Pool);
    }

    /// <summary>The flat triangle arrays and the buckets over them, made again after colliders changed.</summary>
    private void Rebuild()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        var total = 0;
        foreach (var collider in _colliders)
        {
            total += collider?.A.Length ?? 0;
        }

        _a = new Vector3[total];
        _b = new Vector3[total];
        _c = new Vector3[total];
        _tags = new uint[total];
        _anchors = new VoxelCoord[total];
        _owner = new int[total];
        _local = new int[total];
        _stamps = new int[total];
        _stamp = 0;
        _buckets.Clear();
        var next = 0;
        for (var i = 0; i < _colliders.Count; i++)
        {
            if (_colliders[i] is not { } collider)
            {
                continue;
            }

            for (var t = 0; t < collider.A.Length; t++, next++)
            {
                _a[next] = collider.A[t];
                _b[next] = collider.B[t];
                _c[next] = collider.C[t];
                _tags[next] = collider.Tags[t];
                _anchors[next] = collider.Anchor;
                _owner[next] = i;
                _local[next] = t;
                var min = Vector3.Min(collider.A[t], Vector3.Min(collider.B[t], collider.C[t]));
                var max = Vector3.Max(collider.A[t], Vector3.Max(collider.B[t], collider.C[t]));
                double anchorX = collider.Anchor.X / (double)VoxelConstants.CentimetresPerMetre,
                    anchorZ = collider.Anchor.Z / (double)VoxelConstants.CentimetresPerMetre;
                for (var x = Bucket(anchorX + min.X); x <= Bucket(anchorX + max.X); x++)
                {
                    for (var z = Bucket(anchorZ + min.Z); z <= Bucket(anchorZ + max.Z); z++)
                    {
                        var key = Key(x, z);
                        if (!_buckets.TryGetValue(key, out var bucket))
                        {
                            bucket = [];
                            _buckets[key] = bucket;
                        }

                        bucket.Add(next);
                    }
                }
            }
        }

        _triangles = total;
    }

    private int NextStamp()
    {
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_stamps);
            _stamp = 1;
        }

        return _stamp;
    }

    private static int Bucket(double metres) => (int)Math.Floor(metres / BucketMetres);

    private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

    /// <summary>
    /// Where a ray from <paramref name="origin"/> along the unit <paramref name="d"/> meets triangle a, b, c, from either
    /// side (Möller and Trumbore's test).
    /// </summary>
    private static bool Intersect(Vector3 origin, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f)
        {
            return false;
        }

        var inverse = 1 / det;
        var s = origin - a;
        var u = Vector3.Dot(s, p) * inverse;
        if (u < 0 || u > 1)
        {
            return false;
        }

        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(d, q) * inverse;
        if (v < 0 || u + v > 1)
        {
            return false;
        }

        t = Vector3.Dot(e2, q) * inverse;
        return t >= 0;
    }

    /// <summary>One collider: its triangles in metres from its anchor, their tags, the anchor, and its Bepu static.</summary>
    private sealed class Collider(Vector3[] a, Vector3[] b, Vector3[] c, uint[] tags, VoxelCoord anchor)
    {
        public VoxelCoord Anchor { get; } = anchor;

        public Vector3[] A { get; } = a;

        public Vector3[] B { get; } = b;

        public Vector3[] C { get; } = c;

        public uint[] Tags { get; } = tags;

        public TypedIndex Shape { get; set; }

        public StaticHandle Static { get; set; }

        public bool HasStatic { get; set; }

        /// <summary>A game's settings for it (its physics material).</summary>
        public PhysicsBodySettings Settings { get; set; }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Mesh colliders: collider {Collider} added, {Triangles} triangle(s)"
    )]
    private partial void LogAdded(int collider, int triangles);
}
