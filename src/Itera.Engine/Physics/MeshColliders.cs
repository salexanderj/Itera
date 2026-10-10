using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Assets;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Physics;

/// <summary>
/// The match's static triangle colliders that a game registers (<see cref="Add(MeshAsset, in Matrix4x4, Func{string, uint}?)"/>):
/// a mesh asset or triangles built in code, placed by a transform, each triangle with the surface tags of its material
/// (game-defined bits; <c>IVoxelSurfaceTags</c> gives the voxels' theirs). Only registered colliders collide: drawing and
/// colliding stay apart, so one model may be decoration in one place and ground in another.
/// <list type="bullet">
/// <item>The triangles are kept in the world's space with a grid of 4 m buckets over x and z, which rays
/// (<see cref="Raycast"/>, two-sided) and the character mover's queries read. The buckets are made again at the first
/// query after a collider was added or removed (that query allocates); every other query allocates nothing.</item>
/// <item>Each collider is also a Bepu mesh static (its triangles wound as Bepu asks: clockwise seen from the front,
/// where glTF's are counter-clockwise), which every loose body touches whatever its voxel level
/// (<c>BodyKinds.SetGameStatic</c>); Bepu's meshes stop bodies from their front only.</item>
/// </list>
/// Scoped to the match; add colliders once its physics exists (from a system's <c>Initialize</c> on). Main thread.
/// </summary>
public sealed partial class MeshColliders(PhysicsWorld physics, ILogger<MeshColliders> logger) : IDisposable
{
    /// <summary>The side of a bucket of the triangle grid, in metres.</summary>
    public const float BucketMetres = 4;

    private readonly List<Collider?> _colliders = [];
    private readonly Dictionary<long, List<int>> _buckets = [];
    private Vector3[] _a = [];
    private Vector3[] _b = [];
    private Vector3[] _c = [];
    private uint[] _tags = [];
    private int[] _owner = [];
    private int[] _local = [];
    private int[] _stamps = [];
    private int _triangles;
    private int _stamp;
    private bool _dirty;
    private bool _disposed;

    /// <summary>The colliders registered and not removed.</summary>
    public int Count { get; private set; }

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
    /// to the world's, used as <c>v * M</c>), each tagged by its material's name through <paramref name="tagsOf"/> (none
    /// when null). A skinned asset collides as its file stores it. Returns the collider's number.
    /// </summary>
    public int Add(MeshAsset asset, in Matrix4x4 transform, Func<string, uint>? tagsOf = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var positions = new Vector3[asset.Vertices.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = asset.Vertices[i].Position;
        }

        var tags = new uint[asset.Indices.Length / 3];
        foreach (var primitive in asset.Primitives)
        {
            var tag = tagsOf?.Invoke(asset.Materials[primitive.Material].Name) ?? 0;
            for (var t = primitive.FirstIndex / 3; t < (primitive.FirstIndex + primitive.IndexCount) / 3; t++)
            {
                tags[t] = tag;
            }
        }

        return Add(positions, asset.Indices, in transform, tags);
    }

    /// <summary>
    /// Registers triangles built in code: <paramref name="indices"/> three a triangle into <paramref name="positions"/>,
    /// counter-clockwise seen from the front, placed by <paramref name="transform"/>, every one tagged
    /// <paramref name="tags"/>. Returns the collider's number.
    /// </summary>
    public int Add(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, in Matrix4x4 transform, uint tags = 0)
    {
        var all = new uint[indices.Length / 3];
        Array.Fill(all, tags);
        return Add(positions, indices, in transform, all);
    }

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
        _dirty = true;
    }

    /// <summary>
    /// The nearest triangle a ray from <paramref name="origin"/> along <paramref name="direction"/> (any length but
    /// zero) meets within <paramref name="maxMetres"/>, from either side. Allocates nothing once the buckets are made.
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

        // Walk the buckets the ray's path over x and z crosses, nearest first, until the nearest hit lies before the
        // next bucket.
        var bx = Bucket(origin.X);
        var bz = Bucket(origin.Z);
        var stepX = d.X > 0 ? 1 : -1;
        var stepZ = d.Z > 0 ? 1 : -1;
        var nextX =
            Math.Abs(d.X) > 1e-9f
                ? ((bx + (stepX > 0 ? 1 : 0)) * BucketMetres - origin.X) / d.X
                : float.PositiveInfinity;
        var nextZ =
            Math.Abs(d.Z) > 1e-9f
                ? ((bz + (stepZ > 0 ? 1 : 0)) * BucketMetres - origin.Z) / d.Z
                : float.PositiveInfinity;
        var deltaX = Math.Abs(d.X) > 1e-9f ? BucketMetres / Math.Abs(d.X) : float.PositiveInfinity;
        var deltaZ = Math.Abs(d.Z) > 1e-9f ? BucketMetres / Math.Abs(d.Z) : float.PositiveInfinity;
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
                    if (Intersect(origin, d, _a[triangle], _b[triangle], _c[triangle], out var t) && t < best)
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

    private int Add(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, in Matrix4x4 transform, uint[] tags)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException("A collider's indices come three a triangle.", nameof(indices));
        }

        var count = indices.Length / 3;
        var collider = new Collider(new Vector3[count], new Vector3[count], new Vector3[count], tags);
        for (var t = 0; t < count; t++)
        {
            collider.A[t] = Vector3.Transform(positions[(int)indices[t * 3]], transform);
            collider.B[t] = Vector3.Transform(positions[(int)indices[t * 3 + 1]], transform);
            collider.C[t] = Vector3.Transform(positions[(int)indices[t * 3 + 2]], transform);
        }

        AddStatic(collider);
        _colliders.Add(collider);
        Count++;
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
        collider.Static = simulation.Statics.Add(new StaticDescription(Vector3.Zero, Quaternion.Identity, shape));
        collider.HasStatic = true;
        physics.Kinds.SetGameStatic(collider.Static);
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

        physics.Kinds.ClearStatic(collider.Static);
        physics.Simulation.Statics.Remove(collider.Static);
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
                _owner[next] = i;
                _local[next] = t;
                var min = Vector3.Min(collider.A[t], Vector3.Min(collider.B[t], collider.C[t]));
                var max = Vector3.Max(collider.A[t], Vector3.Max(collider.B[t], collider.C[t]));
                for (var x = Bucket(min.X); x <= Bucket(max.X); x++)
                {
                    for (var z = Bucket(min.Z); z <= Bucket(max.Z); z++)
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

    private static int Bucket(float metres) => (int)MathF.Floor(metres / BucketMetres);

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

    /// <summary>One collider: its triangles in the world's space, their tags, and its Bepu static.</summary>
    private sealed class Collider(Vector3[] a, Vector3[] b, Vector3[] c, uint[] tags)
    {
        public Vector3[] A { get; } = a;

        public Vector3[] B { get; } = b;

        public Vector3[] C { get; } = c;

        public uint[] Tags { get; } = tags;

        public TypedIndex Shape { get; set; }

        public StaticHandle Static { get; set; }

        public bool HasStatic { get; set; }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Mesh colliders: collider {Collider} added, {Triangles} triangle(s)"
    )]
    private partial void LogAdded(int collider, int triangles);
}
