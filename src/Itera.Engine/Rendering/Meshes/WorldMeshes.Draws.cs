using System.Numerics;
using Itera.Engine.Meshing;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Voxels;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

public sealed unsafe partial class WorldMeshes
{
    /// <summary>The near shadow cascade covers view depths from 0 to this; the far one from here on.</summary>
    internal const float NearShadowMetres = 30;

    /// <summary>The far cascade ends at this view depth, or at the view's distance when that is less.</summary>
    internal const float FarShadowMetres = 200;

    /// <summary>
    /// The band before <see cref="NearShadowMetres"/> where the near cascade blends into the far one, which covers it too.
    /// </summary>
    internal const float NearBlendMetres = 5;

    /// <summary>The share of the far cascade's end over which its shadows fade out.</summary>
    internal const float FarFadeShare = 0.1f;

    /// <summary>How far towards the sun beyond a cascade's slice a block still casts into it.</summary>
    private const float CasterMetres = 100;

    /// <summary>
    /// The far cascade's blocks are chosen with the reaches times this, on top of <c>MeshReachShare</c>: one level
    /// coarser than the camera's at the same distance, as its texels are about a level-4 voxel (16 cm) wide.
    /// </summary>
    private const double FarShadowCutShare = 0.5;

    // Half a cube's diagonal over half its side: a block's bounding sphere.
    private static readonly float s_sphereOverHalfSide = MathF.Sqrt(3);

    // The planes of the view's frustum, through the camera, facing in: left, right, bottom, top.
    private readonly Vector3[] _planes = new Vector3[4];
    private int _drawSlot;
    private int _records;
    private int _drawn;
    private int _nearShadows;
    private int _farShadows;
    private long _triangles;
    private long _shadowTriangles;
    private bool _shadows;
    private bool _farPass;
    private bool _listFull;
    private bool _listFullReported;
    private int _stitchedDraws;
    private bool _stitching;
    private double _cutShare;
    private Vector3 _viewPosition;
    private Vector3 _viewForward;
    private float _viewDistance;
    private readonly CascadeBounds _nearCascade = new();
    private readonly CascadeBounds _farCascade = new();

    // The columns each pass draws, kept within one level of their neighbours for the seams' stitching.
    private readonly LevelBalance _balance = new();
    private Func<ColumnKey, bool>? _canSplit;

    // The camera's commands and their blocks' squared distances from the camera, gathered here and sorted nearest
    // first before they go into the slot's list, which is mapped memory and slow to read back.
    private readonly DrawIndexedIndirectCommand[] _cameraCommands = new DrawIndexedIndirectCommand[MeshPass.MaxDraws];
    private readonly float[] _cameraDistances = new float[MeshPass.MaxDraws];

    /// <summary>
    /// Writes the frame's draws into <see cref="MeshPass"/>'s regions of <paramref name="slot"/> and returns what it
    /// wrote. Each column of the coarsest meshed level is drawn whole, or by its four columns of the level below where
    /// those are near enough to show (the column's nearest part within the finer level's reach) and all meshed; so the
    /// levels meet without gaps or overlaps, and a finer column still loading leaves its coarser one drawn. Distances are
    /// to the nearest part of a column (<see cref="NearestSquared"/>). A drawn column's blocks with a mesh go into the
    /// camera's list when their box meets the view's frustum, and, with the view's shadows on, into the near cascade's
    /// list when their bounding sphere meets the cascade's box and can shade its slice of the view
    /// (<see cref="CascadeBounds"/>). With shadows on, a second pass chooses the far cascade's blocks the same way with
    /// shorter reaches (<see cref="FarShadowCutShare"/>). Each pass gives a block it lists one <see cref="MeshDraw"/>.
    /// The camera's pass draws a block beside a column one level coarser from its stitched mesh where one is ready, and
    /// asks for it where not (WorldMeshes.Stitches.cs). The camera's list is written nearest first
    /// (<see cref="WriteCameraNearestFirst"/>).
    /// </summary>
    private MeshFrame WriteDraws(
        int slot,
        in ViewConstants view,
        ReadOnlySpan<ModelDraw> models,
        ReadOnlySpan<PointLight> lights
    )
    {
        _drawSlot = slot;
        _records = 0;
        _drawn = 0;
        _nearShadows = 0;
        _farShadows = 0;
        _triangles = 0;
        _shadowTriangles = 0;
        _viewPosition = view.Position;
        _viewForward = view.Forward;
        _viewDistance = view.MaxDistance;
        _shadows = view.Has(ViewSwitches.Shadows);
        _stitching = !view.Has(ViewSwitches.Plain);
        SetPlanes(view.Forward, view.Right, view.Up);
        WriteCascades(slot, in view);
        var lists = new DrawLists(
            _pass.DrawsOf(slot),
            _pass.CommandsOf(slot, 0),
            _pass.CommandsOf(slot, 1),
            _pass.CommandsOf(slot, 2)
        );
        _farPass = false;
        _listFull = false;
        _cutShare = _reachShare;
        _stitchedDraws = 0;
        SelectAll(in lists);
        StitchedDraws = _stitchedDraws;
        WriteCameraNearestFirst(lists.Camera);
        OfferEventLights(lights);
        _pass.LightingOf(slot).LightCount = WriteLights(slot);
        if (_shadows)
        {
            _farPass = true;
            _cutShare = _reachShare * FarShadowCutShare;
            SelectAll(in lists);
        }

        if (_listFull && !_listFullReported)
        {
            _listFullReported = true;
            LogListFull(MeshPass.MaxDraws);
        }

        _stats.LastTriangles = _triangles;
        _stats.LastShadowTriangles = _shadowTriangles;
        var (camera, near, far) = WriteModelDraws(slot, models);
        return new MeshFrame(
            _records,
            _drawn,
            _nearShadows,
            _farShadows,
            _shadows,
            camera,
            near,
            far,
            _skyUploaded,
            _skyUploadOffset
        );
    }

    /// <summary>
    /// Fits the two cascades to the view (<see cref="ShadowCascades.Fit"/>) and writes their record for
    /// <c>mesh.frag</c> and <c>mesh-shadow.vert</c>.
    /// </summary>
    private void WriteCascades(int slot, in ViewConstants view)
    {
        var farEnd = Math.Max(Math.Min(FarShadowMetres, view.MaxDistance), 2 * NearShadowMetres);
        var near = ShadowCascades.Fit(
            view.Position,
            view.Forward,
            view.Right,
            view.Up,
            0,
            NearShadowMetres,
            view.SunDirection,
            _shadowMapTexels,
            CasterMetres
        );
        var farStart = NearShadowMetres - NearBlendMetres;
        var far = ShadowCascades.Fit(
            view.Position,
            view.Forward,
            view.Right,
            view.Up,
            farStart,
            farEnd,
            view.SunDirection,
            _shadowMapTexels,
            CasterMetres
        );
        _nearCascade.Set(
            in near,
            _shadowMapTexels,
            view.Position,
            view.Forward,
            _planes,
            0,
            NearShadowMetres,
            view.SunDirection
        );
        _farCascade.Set(
            in far,
            _shadowMapTexels,
            view.Position,
            view.Forward,
            _planes,
            farStart,
            farEnd,
            view.SunDirection
        );
        _pass.LightingOf(slot) = new MeshLighting
        {
            Near = near.ViewProjection,
            Far = far.ViewProjection,
            NearEnd = NearShadowMetres,
            FarEnd = farEnd,
            NearTexelMetres = near.TexelMetres,
            FarTexelMetres = far.TexelMetres,
            Shadows = _shadows ? 1u : 0u,
            NearBlendStart = farStart,
            FarFadeStart = farEnd * (1 - FarFadeShare),
        };
        WriteSky(ref _pass.LightingOf(slot));
    }

    /// <summary>
    /// Chooses the pass's columns from each coarsest one (<see cref="Select"/>), keeps them within a level of their
    /// neighbours (<see cref="LevelBalance"/>), and draws them, in the camera's pass with the sides past which a column
    /// one level coarser is drawn.
    /// </summary>
    private void SelectAll(in DrawLists lists)
    {
        _balance.Begin(_finestLevel);
        foreach (var column in _byLevel[_coarsestLevel])
        {
            Select(column);
        }

        _balance.Balance(_canSplit ??= CanSplit);
        if (!_farPass)
        {
            _stats.LastUnbalanced = _balance.Unbalanced;
        }

        // By index: a foreach over the interface boxes its enumerator, an allocation each pass.
        var leaves = _balance.Leaves;
        var unmeshed = 0;
        for (var i = 0; i < leaves.Count; i++)
        {
            var key = leaves[i];
            var column = _columns[key];
            column.DrawnUpload = Uploads;
            // Columns past the view distance are never meshed and draw nothing there.
            unmeshed += column.Wanted && !column.MeshedOnce ? 1 : 0;
            Needed(column);
            Draw(column, _farPass ? TransitionSides.None : _balance.SidesOf(key), in lists);
        }

        if (!_farPass && unmeshed > 0)
        {
            _stats.UnmeshedFrames++;
            _stats.MostUnmeshed = Math.Max(_stats.MostUnmeshed, unmeshed);
        }
    }

    /// <summary>
    /// Draws <paramref name="column"/>, or its four finer columns where they are near enough and meshed; a column not
    /// meshed yet (loading, or evicted to make room, WorldMeshes.Evict) whose finer columns are meshed stays split until
    /// it is.
    /// </summary>
    private void Select(MeshColumn column)
    {
        var key = column.Key;
        if (key.Level > _finestLevel)
        {
            var near = IsNear(key);
            if (
                (near || !column.MeshedOnce)
                && TryChildren(key, out var a, out var b, out var c, out var d, orFiner: true)
            )
            {
                if (!near)
                {
                    Needed(column);
                }

                Select(a);
                Select(b);
                Select(c);
                Select(d);
                return;
            }

            if (near)
            {
                NeededChildren(key);
            }
        }

        _balance.Add(key);
    }

    /// <summary>
    /// True when the four columns of the level below that make up <paramref name="key"/> are meshed; where not, any of
    /// them evicted is meshed again.
    /// </summary>
    private bool CanSplit(ColumnKey key)
    {
        if (key.Level <= _finestLevel)
        {
            return false;
        }

        // The balance draws the four as leaves: each must be meshed itself.
        if (TryChildren(key, out _, out _, out _, out _, orFiner: false))
        {
            return true;
        }

        NeededChildren(key);
        return false;
    }

    /// <summary>The view would draw the four finer columns of <paramref name="key"/>: those evicted are meshed again.</summary>
    private void NeededChildren(ColumnKey key)
    {
        var level = key.Level - 1;
        int x = key.X << 1,
            z = key.Z << 1;
        for (var i = 0; i < 4; i++)
        {
            if (_columns.TryGetValue(new ColumnKey(level, x + (i & 1), z + (i >> 1)), out var child))
            {
                Needed(child);
            }
        }
    }

    /// <summary>True when the nearest part of the column is within the reach of the level below it, in this pass's cut.</summary>
    private bool IsNear(ColumnKey key)
    {
        var reach = _distances.ReachCentimetres(key.Level - 1) * _cutShare;
        return NearestSquared(key, _viewPosition) < reach * reach;
    }

    /// <summary>
    /// The squared horizontal distance from <paramref name="at"/> to the nearest point of the column, in centimetres:
    /// 0 inside it. A coarse column is tens of metres across, so its centre says little about how near it comes.
    /// </summary>
    private double NearestSquared(ColumnKey key, Vector3 at)
    {
        double half = _grid.ColumnCentimetres(key.Level) / 2;
        var dx = Math.Max(
            0,
            Math.Abs(_grid.ToCentimetres(key.CentreVoxelX) - at.X * VoxelConstants.CentimetresPerMetre) - half
        );
        var dz = Math.Max(
            0,
            Math.Abs(_grid.ToCentimetres(key.CentreVoxelZ) - at.Z * VoxelConstants.CentimetresPerMetre) - half
        );
        return dx * dx + dz * dz;
    }

    /// <summary>
    /// The four columns of the level below that make up <paramref name="key"/>, when all can be drawn: each meshed, or
    /// with <paramref name="orFiner"/> standing for by its own four finer columns that can be (a column freed to make
    /// room, WorldMeshes.Evict, between the coarsest and the leaves the view draws; <see cref="Select"/> splits it in
    /// turn).
    /// </summary>
    private bool TryChildren(
        ColumnKey key,
        out MeshColumn a,
        out MeshColumn b,
        out MeshColumn c,
        out MeshColumn d,
        bool orFiner
    )
    {
        var level = key.Level - 1;
        var x = key.X << 1;
        var z = key.Z << 1;
        a = b = c = d = null!;
        return Drawable(new ColumnKey(level, x, z), orFiner, out a)
            && Drawable(new ColumnKey(level, x + 1, z), orFiner, out b)
            && Drawable(new ColumnKey(level, x, z + 1), orFiner, out c)
            && Drawable(new ColumnKey(level, x + 1, z + 1), orFiner, out d);
    }

    private bool Drawable(ColumnKey key, bool orFiner, out MeshColumn column) =>
        _columns.TryGetValue(key, out column!)
        && (
            column.MeshedOnce
            || (orFiner && key.Level > _finestLevel && TryChildren(key, out _, out _, out _, out _, orFiner: true))
        );

    /// <summary>
    /// Lists the column's blocks with meshes for the pass's lists; a block along <paramref name="sides"/> (the camera's
    /// pass) from its stitched mesh when one is ready for them, else from its plain mesh, asking for a stitched one.
    /// </summary>
    private void Draw(MeshColumn column, TransitionSides sides, in DrawLists lists)
    {
        var level = column.Key.Level;
        var voxelMetres = _grid.EdgeMetres(level);
        var blockMetres = SurfaceNets.BlockSize * voxelMetres;
        foreach (var index in column.Drawn)
        {
            ref readonly var block = ref column.Blocks[index];
            var (x, y, z) = column.BlockAt(index);
            var origin = new Vector3(x, y, z) * blockMetres;
            if (!_farPass)
            {
                OfferLight(in block, origin, voxelMetres);
            }

            // The vertices lie within half a voxel of the block's box, a stitched mesh's within a voxel.
            var centre = origin + new Vector3(0.5f * blockMetres);
            var extent = 0.5f * blockMetres + voxelMetres;
            var radius = extent * s_sphereOverHalfSide;
            // Room is asked last, so a full list is noted only when a block it should take goes without.
            var seen = !_farPass && InView(centre, extent) && Room(_drawn);
            var near = !_farPass && _shadows && _nearCascade.Holds(centre, radius) && Room(_nearShadows);
            var far = _farPass && _farCascade.Holds(centre, radius) && Room(_farShadows);
            if (!seen && !near && !far)
            {
                continue;
            }

            if (_records >= MeshPass.MaxRecords)
            {
                _listFull = true;
                return;
            }

            var vertexBase = block.VertexBase;
            var firstIndex = block.FirstIndex;
            var indexCount = block.IndexCount;
            var distanceSquared = Vector3.DistanceSquared(centre, _viewPosition);
            var stitched = false;
            // The far cascade, and the debug view of plain meshes, draw the plain meshes and keep the stitched ones.
            if (_stitching && !_farPass)
            {
                var blockSides = MeshColumn.BlockSides(sides, index);
                if (blockSides != TransitionSides.None)
                {
                    ref var stitch = ref column.StitchesMade()[index];
                    if (!stitch.IsCurrent(blockSides, block.Version))
                    {
                        if (!stitch.InFlight)
                        {
                            RequestStitch(column, index, blockSides, distanceSquared);
                        }
                    }
                    else if (!stitch.UsePlain)
                    {
                        vertexBase = stitch.VertexBase;
                        firstIndex = stitch.FirstIndex;
                        indexCount = stitch.IndexCount;
                        stitched = true;
                    }
                }
                else if (column.Stitches is { } stitches && stitches[index].RangeWords > 0)
                {
                    // The block no longer lies beside a coarser column.
                    FreeStitch(ref stitches[index]);
                }
            }

            if (indexCount == 0)
            {
                // Its stitched mesh is empty: the coarser column's mesh covers what it held.
                continue;
            }

            lists.Draws[_records] = new MeshDraw
            {
                Origin = origin,
                VoxelMetres = voxelMetres,
                VertexBase = vertexBase,
            };
            var command = new DrawIndexedIndirectCommand(
                (uint)indexCount,
                1,
                firstIndex,
                0,
                (uint)(_drawSlot * MeshPass.MaxRecords + _records)
            );
            _records++;
            if (seen)
            {
                _cameraDistances[_drawn] = distanceSquared;
                _cameraCommands[_drawn++] = command;
                _triangles += indexCount / 3;
                _stitchedDraws += stitched ? 1 : 0;
            }

            if (near)
            {
                lists.Near[_nearShadows++] = command;
                _shadowTriangles += indexCount / 3;
            }

            if (far)
            {
                lists.Far[_farShadows++] = command;
                _shadowTriangles += indexCount / 3;
            }
        }
    }

    /// <summary>
    /// Writes the camera's commands into <paramref name="camera"/> nearest first, by the distance to each block's
    /// centre. They are gathered in the order the columns loaded and their blocks' meshes arrived, which changes from
    /// run to run; drawn in that order, the leaves behind were often shaded before the leaves in front covered them, a
    /// third of the integrated GPU's time in the leafy views and different in each run (docs/rendering-redesign.md,
    /// stage 1.7). Nearest first, the depth test rejects most hidden surfaces before they are shaded.
    /// </summary>
    private void WriteCameraNearestFirst(Span<DrawIndexedIndirectCommand> camera)
    {
        Array.Sort(_cameraDistances, _cameraCommands, 0, _drawn);
        _cameraCommands.AsSpan(0, _drawn).CopyTo(camera);
    }

    /// <summary>True when a list of <paramref name="count"/> commands has room for one more; else notes that a block went without.</summary>
    private bool Room(int count)
    {
        if (count < MeshPass.MaxDraws)
        {
            return true;
        }

        _listFull = true;
        return false;
    }

    /// <summary>The frustum's side planes from the view's axes (right and up reach the image's edges at unit forward).</summary>
    private void SetPlanes(Vector3 forward, Vector3 right, Vector3 up)
    {
        _planes[0] = InwardNormal(Vector3.Cross(forward - right, up), forward);
        _planes[1] = InwardNormal(Vector3.Cross(forward + right, up), forward);
        _planes[2] = InwardNormal(Vector3.Cross(forward - up, right), forward);
        _planes[3] = InwardNormal(Vector3.Cross(forward + up, right), forward);
    }

    private static Vector3 InwardNormal(Vector3 normal, Vector3 forward)
    {
        normal = Vector3.Normalize(normal);
        return Vector3.Dot(normal, forward) < 0 ? -normal : normal;
    }

    /// <summary>True when the cube of half-size <paramref name="extent"/> at <paramref name="centre"/> may be seen.</summary>
    private bool InView(Vector3 centre, float extent)
    {
        var offset = centre - _viewPosition;
        var ahead = Vector3.Dot(offset, _viewForward);
        var reach = extent * (MathF.Abs(_viewForward.X) + MathF.Abs(_viewForward.Y) + MathF.Abs(_viewForward.Z));
        if (ahead + reach < 0 || ahead - reach > _viewDistance)
        {
            return false;
        }

        foreach (var plane in _planes)
        {
            var spread = extent * (MathF.Abs(plane.X) + MathF.Abs(plane.Y) + MathF.Abs(plane.Z));
            if (Vector3.Dot(plane, offset) + spread < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A frame slot's draws and its three command lists, written by <see cref="Draw"/> (the camera's by
    /// <see cref="WriteCameraNearestFirst"/>).
    /// </summary>
    private readonly ref struct DrawLists(
        Span<MeshDraw> draws,
        Span<DrawIndexedIndirectCommand> camera,
        Span<DrawIndexedIndirectCommand> near,
        Span<DrawIndexedIndirectCommand> far
    )
    {
        public readonly Span<MeshDraw> Draws = draws;
        public readonly Span<DrawIndexedIndirectCommand> Camera = camera;
        public readonly Span<DrawIndexedIndirectCommand> Near = near;
        public readonly Span<DrawIndexedIndirectCommand> Far = far;
    }

    /// <summary>
    /// Which blocks cast into a cascade: those whose bounding sphere meets the cascade's box, and lies on the inner side
    /// of each plane of the cascade's slice of the view (its side planes and its near and far depths) that faces the sun.
    /// A block shades a point of the slice only if it lies towards the sun from it, and moving towards the sun never
    /// leaves the inner side of a plane that faces the sun; the planes that face away are dropped, so the test keeps a
    /// little more than it must, never less.
    /// </summary>
    private sealed class CascadeBounds
    {
        private const int MostPlanes = 6;

        // Inward normals (xyz) and the dot product a point on the plane has with them (w), relative to the camera.
        private readonly Vector4[] _planes = new Vector4[MostPlanes];
        private Matrix4x4 _viewProjection;
        private Vector3 _origin;
        private float _acrossPerMetre;
        private float _alongPerMetre;
        private int _planeCount;

        /// <summary>
        /// Takes the cascade's fit, <paramref name="mapTexels"/> wide, and its slice of the view, between
        /// <paramref name="nearMetres"/> and <paramref name="farMetres"/> of view depth, bounded by the frustum's
        /// <paramref name="sidePlanes"/> through <paramref name="position"/>.
        /// </summary>
        public void Set(
            in CascadeFit fit,
            int mapTexels,
            Vector3 position,
            Vector3 forward,
            ReadOnlySpan<Vector3> sidePlanes,
            float nearMetres,
            float farMetres,
            Vector3 sun
        )
        {
            _viewProjection = fit.ViewProjection;
            _origin = position;
            // The square is mapTexels texels wide, 2 in clip units; z's scale is the length of its column.
            _acrossPerMetre = 2 / (fit.TexelMetres * mapTexels);
            var m = fit.ViewProjection;
            _alongPerMetre = new Vector3(m.M13, m.M23, m.M33).Length();
            _planeCount = 0;
            foreach (var plane in sidePlanes)
            {
                Keep(plane, 0, sun);
            }

            Keep(forward, nearMetres, sun);
            Keep(-forward, -farMetres, sun);
        }

        /// <summary>True when a block's sphere of <paramref name="radius"/> at <paramref name="centre"/> may cast into the cascade.</summary>
        public bool Holds(Vector3 centre, float radius)
        {
            var clip = Vector3.Transform(centre, _viewProjection);
            var across = 1 + radius * _acrossPerMetre;
            var along = radius * _alongPerMetre;
            if (MathF.Abs(clip.X) > across || MathF.Abs(clip.Y) > across || clip.Z < -along || clip.Z > 1 + along)
            {
                return false;
            }

            var offset = centre - _origin;
            for (var i = 0; i < _planeCount; i++)
            {
                var plane = _planes[i];
                if (Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), offset) - plane.W < -radius)
                {
                    return false;
                }
            }

            return true;
        }

        private void Keep(Vector3 normal, float distance, Vector3 sun)
        {
            if (Vector3.Dot(normal, sun) >= 0)
            {
                _planes[_planeCount++] = new Vector4(normal, distance);
            }
        }
    }
}
