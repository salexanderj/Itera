using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Meshing;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Voxels;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

public sealed unsafe partial class WorldMeshes
{
    /// <summary>
    /// The far cascade's blocks are chosen with the reaches times this, on top of <c>MeshReachShare</c>: one level
    /// coarser than the camera's at the same distance, as its texels are about 20 cm wide. Its pass is not balanced
    /// (<see cref="SelectAll"/>), so the coarser cut holds.
    /// </summary>
    private const double FarShadowCutShare = 0.5;

    // Half a cube's diagonal over half its side: a block's bounding sphere.
    private static readonly float s_sphereOverHalfSide = MathF.Sqrt(3);

    private int _drawSlot;
    private int _records;
    private int _drawn;
    private int _nearShadows;
    private int _farShadows;
    private int _distantShadows;
    private long _triangles;
    private long _shadowTriangles;
    private bool _farPass;
    private bool _listFull;
    private bool _listFullReported;
    private int _stitchedDraws;
    private bool _stitching;
    private double _cutShare;

    // The columns each pass draws, in the camera's pass kept within one level of their neighbours for the seams'
    // stitching.
    private readonly LevelBalance _balance = new();

    /// <summary>The columns the camera's pass chose in the last frame.</summary>
    internal int LastCameraColumns { get; private set; }

    /// <summary>The columns the far cascade's pass chose in the last frame (0 without shadows).</summary>
    internal int LastFarColumns { get; private set; }
    private Func<ColumnKey, bool>? _canSplit;

    // The camera's commands and their blocks' keys (squared distances from the camera, MeshView.NearFirstKey), sorted nearest
    // first before they go into the slot's list, which is mapped memory and slow to read back; kept for CameraCommands.
    private readonly DrawIndexedIndirectCommand[] _cameraCommands = new DrawIndexedIndirectCommand[MeshPass.MaxDraws];
    private readonly float[] _cameraDistances = new float[MeshPass.MaxDraws];

    // The image views drawn this frame (RenderViewPass), and each one's commands and keys nearest first, gathered in the
    // camera's pass as the camera's are; each command has a draw of its own in the view's records.
    private readonly IRenderViewLists[] _views = new IRenderViewLists[RenderViews.MostViews];
    private readonly DrawIndexedIndirectCommand[][] _viewCommands = NewViewArrays<DrawIndexedIndirectCommand>();
    private readonly float[][] _viewKeys = NewViewArrays<float>();
    private readonly int[] _viewDrawn = new int[RenderViews.MostViews];
    private int _viewCount;
    private bool _viewListFull;
    private bool _viewListFullReported;

    /// <summary>
    /// Writes the frame's draws into <see cref="MeshPass"/>'s regions of <paramref name="slot"/> and returns what it
    /// wrote. Each column of the coarsest meshed level is drawn whole, or by its four columns of the level below where
    /// those are near enough to show (the column's nearest part within the finer level's reach) and all meshed; so the
    /// levels meet without gaps or overlaps, and a finer column still loading leaves its coarser one drawn. Distances are
    /// to the nearest part of a column (<see cref="NearestSquared"/>). A drawn column's blocks with a mesh go into the
    /// camera's list when their box meets the view's frustum, and, with the view's shadows on, into the near cascade's
    /// list when their bounding sphere meets the cascade's box and can shade its slice of the view
    /// (<see cref="CascadeBounds"/>), and into the distant cascade's list the same way when it is drawn
    /// (<see cref="MeshView.DistantShadows"/>): from the camera's columns, one level finer than the far pass's at the
    /// same distance (12.8 m voxels at 1.5 km against 25.6 m, on the starter's nine levels), so its shadows fall from the
    /// surfaces the camera draws there, for one more test a block. With shadows on, a second pass chooses the far
    /// cascade's blocks the same way with shorter reaches (<see cref="FarShadowCutShare"/>). Each pass gives a block it
    /// lists one <see cref="MeshDraw"/>.
    /// The camera's pass draws a block beside a column one level coarser from its stitched mesh where one is ready, and
    /// asks for it where not (WorldMeshes.Stitches.cs). The camera's list is written nearest first
    /// (<see cref="WriteCameraNearestFirst"/>).
    /// </summary>
    private MeshFrame WriteDraws(int slot, in ViewConstants view, ReadOnlySpan<ModelDraw> models)
    {
        _drawSlot = slot;
        _records = 0;
        _drawn = 0;
        _nearShadows = 0;
        _farShadows = 0;
        _distantShadows = 0;
        _triangles = 0;
        _shadowTriangles = 0;
        _stitching = !view.Has(ViewSwitches.Plain);
        WriteSky(ref _pass.LightingOf(slot));
        for (var v = 0; v < _viewCount; v++)
        {
            _viewDrawn[v] = 0;
            WriteSky(ref _views[v].LightingOf(slot));
        }

        var lists = new DrawLists(
            _pass.DrawsOf(slot),
            _pass.CommandsOf(slot, 0),
            _pass.CommandsOf(slot, 1),
            _pass.CommandsOf(slot, 2),
            _pass.CommandsOf(slot, 3)
        );
        _farPass = false;
        _listFull = false;
        _viewListFull = false;
        _cutShare = _reachShare;
        _stitchedDraws = 0;
        SelectAll(in lists);
        StitchedDraws = _stitchedDraws;
        WriteCameraNearestFirst(lists.Camera);
        WriteViewsNearestFirst(slot);
        LastFarColumns = 0;
        // Without shadows the far pass chooses nothing, and splits nothing.
        _farSplit.Clear();
        if (_view.Shadows)
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

        if (_viewListFull && !_viewListFullReported)
        {
            _viewListFullReported = true;
            LogViewListFull(RenderViewTarget.MaxDraws);
        }

        _stats.LastTriangles = _triangles;
        _stats.LastShadowTriangles = _shadowTriangles;
        var (camera, near, far) = WriteModelDraws(slot, models);
        return new MeshFrame(
            _records,
            _drawn,
            _nearShadows,
            _farShadows,
            _view.Shadows,
            camera,
            near,
            far,
            _skyUploaded,
            _skyUploadOffset,
            _view.DistantShadows,
            _distantShadows
        );
    }

    /// <summary>
    /// Chooses the pass's columns from each coarsest one (<see cref="Select"/>) and draws them. The camera's pass keeps
    /// them within a level of their neighbours (<see cref="LevelBalance"/>) and draws them with the sides past which a
    /// column one level coarser is drawn, for the stitched seams. The far cascade's pass draws plain meshes and is not
    /// balanced: balancing split its columns back to the camera's (on the starter's map the two passes chose the same
    /// columns in every view), so its coarser cut (<see cref="FarShadowCutShare"/>) saved nothing.
    /// </summary>
    private void SelectAll(in DrawLists lists)
    {
        _balance.Begin(_finestLevel);
        BeginSplits();
        foreach (var column in _byLevel[_coarsestLevel])
        {
            Select(column.Key);
        }

        if (!_farPass)
        {
            _balance.Balance(_canSplit ??= CanSplit);
            _stats.LastUnbalanced = _balance.Unbalanced;
        }

        // By index: a foreach over the interface boxes its enumerator, an allocation each pass.
        var leaves = _balance.Leaves;
        if (_farPass)
        {
            LastFarColumns = leaves.Count;
        }
        else
        {
            LastCameraColumns = leaves.Count;
        }

        var unmeshed = 0;
        for (var i = 0; i < leaves.Count; i++)
        {
            // A leaf the balance split off past the map's edge has no column and draws nothing.
            var key = leaves[i];
            if (!_columns.TryGetValue(key, out var column))
            {
                continue;
            }

            RecordSplit(key);
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
    /// Draws the column <paramref name="key"/>, or its four finer columns where they are near enough and meshed (and,
    /// at the objects' hand-over level, its objects are ready: <see cref="ObjectsReady"/>); a column not meshed yet
    /// (loading, or evicted to make room, WorldMeshes.Evict) whose finer columns are meshed stays split until it is. Past
    /// the map's edge or the view's end (<see cref="DrawsNothing"/>) nothing is drawn.
    /// </summary>
    private void Select(ColumnKey key)
    {
        if (!_columns.TryGetValue(key, out var column) || DrawsNothing(key, column))
        {
            return;
        }

        if (key.Level > _finestLevel)
        {
            var near = IsNear(column);
            if (((near && ObjectsReady(key)) || !column.MeshedOnce) && TryChildren(key, orFiner: true))
            {
                if (!near)
                {
                    Needed(column);
                }

                var level = key.Level - 1;
                int x = key.X << 1,
                    z = key.Z << 1;
                Select(new ColumnKey(level, x, z));
                Select(new ColumnKey(level, x + 1, z));
                Select(new ColumnKey(level, x, z + 1));
                Select(new ColumnKey(level, x + 1, z + 1));
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
    /// True when the four columns of the level below that make up <paramref name="key"/> are meshed (and its objects are
    /// ready, <see cref="ObjectsReady"/>); where not, any of them evicted is meshed again.
    /// </summary>
    private bool CanSplit(ColumnKey key)
    {
        if (key.Level <= _finestLevel || !ObjectsReady(key))
        {
            return false;
        }

        // The balance draws the four as leaves: each must be meshed itself.
        if (TryChildren(key, orFiner: false))
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
    private bool IsNear(MeshColumn column)
    {
        var reach = _distances.ReachCentimetres(column.Key.Level - 1) * _cutShare;
        return NearestSquared(column, in _viewAt) < reach * reach;
    }

    /// <summary>
    /// The squared distance from <paramref name="at"/> to the nearest point of the column's box, in centimetres: its
    /// square, and its height from the bottom of its first layer with blocks to the top of its last, so a column far
    /// below the view (a camera high over the ground) is far, as one far across is. 0 inside it. A coarse column is tens of
    /// metres across, so its centre says little about how near it comes.
    /// </summary>
    private double NearestSquared(MeshColumn column, in WorldPoint at)
    {
        var key = column.Key;
        double half = _grid.ColumnCentimetres(key.Level) / 2;
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        var dx = Math.Max(0, Math.Abs(_grid.ToCentimetres(key.CentreVoxelX) - at.X * perMetre) - half);
        var dz = Math.Max(0, Math.Abs(_grid.ToCentimetres(key.CentreVoxelZ) - at.Z * perMetre) - half);
        var shift = VoxelConstants.ChunkShift + key.Level;
        var firstChunkY = _grid.MinChunkYAt(key.Level) + column.FirstLayer;
        double bottom = _grid.ToCentimetres(firstChunkY << shift);
        double top = _grid.ToCentimetres((_grid.MinChunkYAt(key.Level) + column.EndLayer) << shift);
        var y = at.Y * perMetre;
        var dy = Math.Max(0, Math.Max(bottom - y, y - top));
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>
    /// The corner of block (x, y, z), blocks <paramref name="blockCentimetres"/> across, local to the frame's origin in
    /// metres: worked out in doubles from whole centimetres, so a block far from the map's middle is drawn where it is.
    /// </summary>
    private Vector3 LocalCorner(int x, int y, int z, long blockCentimetres)
    {
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        return new Vector3(
            (float)((x * blockCentimetres - _origin.X) / perMetre),
            (float)((y * blockCentimetres - _origin.Y) / perMetre),
            (float)((z * blockCentimetres - _origin.Z) / perMetre)
        );
    }

    /// <summary>
    /// True when the four columns of the level below that make up <paramref name="key"/> can all be drawn: each meshed,
    /// drawing nothing (<see cref="DrawsNothing"/>), or with <paramref name="orFiner"/> standing for by its own four
    /// finer columns that can be (a column freed to make room, WorldMeshes.Evict, between the coarsest and the leaves the
    /// view draws; <see cref="Select"/> splits it in turn).
    /// </summary>
    private bool TryChildren(ColumnKey key, bool orFiner)
    {
        var level = key.Level - 1;
        var x = key.X << 1;
        var z = key.Z << 1;
        return Drawable(new ColumnKey(level, x, z), orFiner)
            && Drawable(new ColumnKey(level, x + 1, z), orFiner)
            && Drawable(new ColumnKey(level, x, z + 1), orFiner)
            && Drawable(new ColumnKey(level, x + 1, z + 1), orFiner);
    }

    private bool Drawable(ColumnKey key, bool orFiner) =>
        _columns.TryGetValue(key, out var column)
            ? column.MeshedOnce
                || DrawsNothing(key, column)
                || (orFiner && key.Level > _finestLevel && TryChildren(key, orFiner: true))
            : !key.IsInBounds(_grid);

    /// <summary>
    /// True for a column that draws nothing and keeps no coarser column from splitting: one past the view's end, which
    /// is never meshed (not wanted), or, with no column, one past the map's edge (<paramref name="column"/> null). The
    /// map's half-width need not be a multiple of a coarse column, so a coarse column at its edge has children past it;
    /// and a coarse column wider than the view has children past its end. Either way it splits as any other.
    /// </summary>
    private bool DrawsNothing(ColumnKey key, MeshColumn? column) =>
        column is null
            ? !key.IsInBounds(_grid)
            : !column.Wanted
                && NearestSquared(column, in _viewAt) >= _viewDistanceCentimetres * _viewDistanceCentimetres;

    /// <summary>
    /// Lists the column's blocks with meshes for the pass's lists; a block along <paramref name="sides"/> (the camera's
    /// pass) from its stitched mesh when one is ready for them, else from its plain mesh, asking for a stitched one.
    /// </summary>
    private void Draw(MeshColumn column, TransitionSides sides, in DrawLists lists)
    {
        var level = column.Key.Level;
        var voxelMetres = _grid.EdgeMetres(level);
        var blockMetres = SurfaceNets.BlockSize * voxelMetres;
        var blockCentimetres = (long)SurfaceNets.BlockSize * _grid.Edge(level);
        foreach (var index in column.Drawn)
        {
            ref readonly var block = ref column.Blocks[index];
            var (x, y, z) = column.BlockAt(index);
            var origin = LocalCorner(x, y, z, blockCentimetres);
            if (!_farPass)
            {
                OfferLight(in block, origin, voxelMetres);
            }

            // The vertices lie within half a voxel of the block's box, a stitched mesh's within a voxel.
            var centre = origin + new Vector3(0.5f * blockMetres);
            var extent = 0.5f * blockMetres + voxelMetres;
            var radius = extent * s_sphereOverHalfSide;
            // Room is asked last, so a full list is noted only when a block it should take goes without.
            var seen = !_farPass && _view.InView(centre, extent) && Room(_drawn);
            var near = !_farPass && _view.Shadows && _view.NearCascade.Holds(centre, radius) && Room(_nearShadows);
            var far = _farPass && _view.FarCascade.Holds(centre, radius) && Room(_farShadows);
            var distant =
                !_farPass
                && _view.DistantShadows
                && _view.DistantCascade.Holds(centre, radius)
                && Room(_distantShadows);
            var views = _farPass ? 0 : ViewsSeeing(centre, extent);
            var listed = seen || near || far || distant;
            if (!listed && views == 0)
            {
                continue;
            }

            if (listed && _records >= MeshPass.MaxRecords)
            {
                _listFull = true;
                return;
            }

            var vertexBase = block.VertexBase;
            var firstIndex = block.FirstIndex;
            var indexCount = block.IndexCount;
            var distanceSquared = Vector3.DistanceSquared(centre, _view.Position);
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
                        // A block only an image view or the distant cascade sees asks for none: the views take what the
                        // camera has, and the distant cascade's texels are metres wide.
                        if (!stitch.InFlight && (seen || near))
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

            if (views != 0)
            {
                WriteViewDraws(views, origin, voxelMetres, vertexBase, firstIndex, indexCount, centre);
            }

            if (!listed)
            {
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
                _cameraDistances[_drawn] = _view.Orthographic ? _view.NearFirstKey(centre) : distanceSquared;
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

            if (distant)
            {
                lists.Distant[_distantShadows++] = command;
                _shadowTriangles += indexCount / 3;
            }
        }
    }

    /// <summary>
    /// Writes the camera's commands into <paramref name="camera"/> nearest first, by the distance to each block's
    /// centre (an orthographic view's by its depth, <see cref="MeshView.NearFirstKey"/>). They are gathered in the order the columns loaded and their blocks' meshes arrived, which changes from
    /// run to run; drawn in that order, the leaves behind were often shaded before the leaves in front covered them, a
    /// third of the integrated GPU's time in the leafy views and different in each run (docs/rendering-redesign.md,
    /// stage 1.7). Nearest first, the depth test rejects most hidden surfaces before they are shaded.
    /// </summary>
    private void WriteCameraNearestFirst(Span<DrawIndexedIndirectCommand> camera)
    {
        Array.Sort(_cameraDistances, _cameraCommands, 0, _drawn);
        _cameraCommands.AsSpan(0, _drawn).CopyTo(camera);
    }

    /// <summary>The image views (a bit each, in their order this frame) whose frustum the block's box meets.</summary>
    private int ViewsSeeing(Vector3 centre, float extent)
    {
        var views = 0;
        for (var v = 0; v < _viewCount; v++)
        {
            if (_views[v].View.InView(centre, extent))
            {
                views |= 1 << v;
            }
        }

        return views;
    }

    /// <summary>
    /// A block's draw and command for each view in <paramref name="views"/>, in the view's records (as the camera's
    /// list draws it: its stitched mesh where the camera's does); a full list notes that a block went without.
    /// </summary>
    private void WriteViewDraws(
        int views,
        Vector3 origin,
        float voxelMetres,
        uint vertexBase,
        uint firstIndex,
        int indexCount,
        Vector3 centre
    )
    {
        for (var v = 0; v < _viewCount; v++)
        {
            if ((views & (1 << v)) == 0)
            {
                continue;
            }

            var drawn = _viewDrawn[v];
            if (drawn >= RenderViewTarget.MaxDraws)
            {
                _viewListFull = true;
                continue;
            }

            var target = _views[v];
            target.DrawsOf(_drawSlot)[drawn] = new MeshDraw
            {
                Origin = origin,
                VoxelMetres = voxelMetres,
                VertexBase = vertexBase,
            };
            _viewCommands[v][drawn] = new DrawIndexedIndirectCommand(
                (uint)indexCount,
                1,
                firstIndex,
                0,
                (uint)(RenderViewTarget.RecordBase(_drawSlot) + drawn)
            );
            _viewKeys[v][drawn] = target.View.NearFirstKey(centre);
            _viewDrawn[v] = drawn + 1;
        }
    }

    /// <summary>
    /// Writes each view's commands into its list nearest first, as the camera's (<see cref="WriteCameraNearestFirst"/>),
    /// and its counts into its frame record.
    /// </summary>
    private void WriteViewsNearestFirst(int slot)
    {
        for (var v = 0; v < _viewCount; v++)
        {
            var drawn = _viewDrawn[v];
            Array.Sort(_viewKeys[v], _viewCommands[v], 0, drawn);
            var target = _views[v];
            _viewCommands[v].AsSpan(0, drawn).CopyTo(target.CommandsOf(slot, 0));
            ref var frame = ref target.FrameOf(slot);
            frame.Records = drawn;
            frame.Draws = drawn;
        }
    }

    private static T[][] NewViewArrays<T>()
    {
        var arrays = new T[RenderViews.MostViews][];
        for (var v = 0; v < arrays.Length; v++)
        {
            arrays[v] = new T[RenderViewTarget.MaxDraws];
        }

        return arrays;
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

    /// <summary>
    /// A frame slot's draws and its four command lists, written by <see cref="Draw"/> (the camera's by
    /// <see cref="WriteCameraNearestFirst"/>).
    /// </summary>
    private readonly ref struct DrawLists(
        Span<MeshDraw> draws,
        Span<DrawIndexedIndirectCommand> camera,
        Span<DrawIndexedIndirectCommand> near,
        Span<DrawIndexedIndirectCommand> far,
        Span<DrawIndexedIndirectCommand> distant
    )
    {
        public readonly Span<MeshDraw> Draws = draws;
        public readonly Span<DrawIndexedIndirectCommand> Camera = camera;
        public readonly Span<DrawIndexedIndirectCommand> Near = near;
        public readonly Span<DrawIndexedIndirectCommand> Far = far;
        public readonly Span<DrawIndexedIndirectCommand> Distant = distant;
    }
}
