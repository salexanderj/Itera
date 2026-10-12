using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Meshing;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Voxels;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The models (<see cref="VoxelModels"/>) on the GPU: each level of each model is meshed as the world is, in blocks of
/// 32 voxels with a 2-voxel apron of air round the model's box, on the mesh workers (with its densities where it keeps
/// them, <see cref="VoxelModels.HasDensities"/>, else its materials' defaults), and each block's mesh goes into a
/// heap range of its own. A block meshes the faces whose lower voxel is in it, so a model's blocks start one voxel before
/// its box (<see cref="ModelBlockShift"/>): block b covers voxels 32 b - 1 to 32 b + 30, and the faces on the box's low
/// sides fall in block 0. A model counts as uploaded once every block of every level is in the heap; a replaced model
/// (a part that was cut) keeps its old meshes drawn until the new ones are all in, and a removed model's ranges are freed in
/// the next frame. Each frame the models drawn (<see cref="ModelInstances"/> and the retired ones) are grouped by model
/// and level, and each block of a level becomes one command whose instances are that level's draws, in each list that
/// takes them: the camera's when the model's bounding sphere is in view, and a shadow cascade's as the world's blocks
/// are chosen.
/// </summary>
public sealed unsafe partial class WorldMeshes
{
    /// <summary>Voxels a model's blocks start before its box (<c>mesh-data.glsl</c>'s <c>meshModelWorld</c> moves them back).</summary>
    internal const int ModelBlockShift = ModelBlocks.Shift;

    // A model level's voxels, decoded once to read its blocks: index x + 128y + 16384z; and its densities, for a model
    // that keeps them.
    private byte[] _modelVoxels = [];
    private byte[] _modelDensities = [];
    private int _decodedHandle = -1;
    private int _decodedRevision;
    private int _decodedTree = -1;
    private ModelMesh?[] _modelMeshes = new ModelMesh?[64];
    private readonly List<ModelMesh> _trackedModels = [];
    private bool _modelListFullReported;
    private bool _viewModelListFullReported;

    // The frame's model draws in view or casting, and per list the places of those it takes: the camera's, the cascades',
    // then each image view's drawn this frame.
    private readonly ModelFrameDraw[] _modelFrame = new ModelFrameDraw[ModelInstances.MaxModels];
    private int _modelFrameCount;
    private readonly int[][] _listDraws = NewModelLists();
    private readonly int[] _listCounts = new int[MeshPass.Lists + RenderViews.MostViews];
    private readonly long[] _modelTrianglesByLevel = new long[VoxelModels.MaxLevels];

    // The grouping of one list's draws by model level: a count, then a start, per key (handle * levels + tree), the
    // keys used this frame, and the draws in key order.
    private int[] _keyCounts = new int[64 * VoxelModels.MaxLevels];
    private int[] _keyStarts = new int[64 * VoxelModels.MaxLevels];
    private readonly List<int> _keysUsed = [];
    private readonly int[] _sortedDraws = new int[ModelInstances.MaxModels];

    // The camera's list's draws of one model level that fade, while its whole ones are moved before them, and each
    // level's draws (where they start among the sorted draws, how many, how many shown whole), written in two rounds.
    private readonly int[] _fadedDraws = new int[ModelInstances.MaxModels];
    private readonly (int Start, int Count, int Whole)[] _keyGroups = new (int, int, int)[ModelInstances.MaxModels];

    /// <summary>
    /// Meshes models: restarts any whose revision changed (new, replaced, or a handle reused), and starts their blocks'
    /// jobs, oldest model first, until the time budget or the job pool runs out. Before the world's jobs, so an edit to a model shows
    /// soon.
    /// </summary>
    private void StartModelJobs(long deadline)
    {
        var models = _models!;
        var pending = models.Pending;
        for (var p = 0; p < pending.Count; p++)
        {
            var handle = pending[p];
            // A model that shares a base's meshes waits for them.
            var shared = models.BaseOf(handle);
            if (shared >= 0 && !models.IsUploaded(shared))
            {
                continue;
            }

            var mesh = MeshOf(handle);
            var revision = models.Revision(handle);
            if (mesh.MeshingRevision != revision)
            {
                Restart(mesh, revision, models.LevelCount(handle));
            }

            while (mesh.NextTree < mesh.Trees)
            {
                if (_freeJobs.Count == 0 || Stopwatch.GetTimestamp() > deadline)
                {
                    return;
                }

                StartModelBlock(models, mesh);
            }

            // Every job back, and nothing to mesh (a model of air): done now.
            if (mesh.Pending == 0)
            {
                Swap(models, mesh);
                p--;
            }
        }
    }

    /// <summary>
    /// The meshes of model <paramref name="handle"/>, followed from its first use; made once per handle in a match, and
    /// kept for the handle's next model after one is removed.
    /// </summary>
    private ModelMesh MeshOf(int handle)
    {
        if (handle >= _modelMeshes.Length)
        {
            var size = _modelMeshes.Length;
            while (size <= handle)
            {
                size *= 2;
            }

            Array.Resize(ref _modelMeshes, size);
            Array.Resize(ref _keyCounts, size * VoxelModels.MaxLevels);
            Array.Resize(ref _keyStarts, size * VoxelModels.MaxLevels);
        }

        var mesh = _modelMeshes[handle] ??= new ModelMesh { Handle = handle };
        if (!mesh.Followed)
        {
            mesh.Followed = true;
            _trackedModels.Add(mesh);
        }

        return mesh;
    }

    /// <summary>Starts meshing <paramref name="revision"/> of the model: the meshes of an earlier start go unused.</summary>
    private void Restart(ModelMesh mesh, int revision, int trees)
    {
        FreeBlocks(mesh.Next);
        mesh.MeshingRevision = revision;
        mesh.Trees = trees;
        mesh.NextTree = 0;
        mesh.NextBlock = 0;
        mesh.Pending = 0;
        _stats.ModelsStarted++;
    }

    /// <summary>Reads the model's next block (decoding its level first if needed) and queues its job.</summary>
    private void StartModelBlock(VoxelModels models, ModelMesh mesh)
    {
        var handle = mesh.Handle;
        var tree = mesh.NextTree;
        var size = TreeSize(models.Size(handle), tree);
        // Voxels -1 to size - 1 along each axis, in blocks.
        var blocksX = (size.X + ModelBlockShift + SurfaceNets.BlockSize - 1) / SurfaceNets.BlockSize;
        var blocksY = (size.Y + ModelBlockShift + SurfaceNets.BlockSize - 1) / SurfaceNets.BlockSize;
        var blocksZ = (size.Z + ModelBlockShift + SurfaceNets.BlockSize - 1) / SurfaceNets.BlockSize;
        var block = mesh.NextBlock;
        if (++mesh.NextBlock == blocksX * blocksY * blocksZ)
        {
            mesh.NextTree++;
            mesh.NextBlock = 0;
        }

        // A block it shares with its base is the base's mesh (Swap adds it).
        if (!models.MeshesBlock(handle, tree, block))
        {
            return;
        }

        var densities = models.HasDensities(handle);
        if (_decodedHandle != handle || _decodedRevision != mesh.MeshingRevision || _decodedTree != tree)
        {
            var decoded = new ModelTree(models.Tree(handle, tree));
            models.Decode(decoded, _modelVoxels, size);
            if (densities)
            {
                models.DecodeDensities(decoded, _modelDensities, size);
            }

            _decodedHandle = handle;
            _decodedRevision = mesh.MeshingRevision;
            _decodedTree = tree;
        }

        var bx = block % blocksX;
        var by = block / blocksX % blocksY;
        var bz = block / (blocksX * blocksY);
        var job = _freeJobs.Pop();
        ReadModelBlock(_modelVoxels, size, bx, by, bz, job.Samples);
        if (densities)
        {
            // Outside the box is air of no density, as the samples there.
            ReadModelBlock(_modelDensities, size, bx, by, bz, job.Densities);
        }

        job.Model = mesh;
        job.HasDensities = densities;
        // A part owns the edges of its own box alone; the block's voxels start one before its 32 (ModelBlockShift).
        job.Windowed = models.IsPart(handle);
        if (job.Windowed)
        {
            var (ownedMin, ownedMax) = models.OwnedAt(handle, tree);
            var origin = new VoxelCoord(
                bx * SurfaceNets.BlockSize - ModelBlockShift,
                by * SurfaceNets.BlockSize - ModelBlockShift,
                bz * SurfaceNets.BlockSize - ModelBlockShift
            );
            job.OwnedMin = new VoxelCoord(ownedMin.X - origin.X, ownedMin.Y - origin.Y, ownedMin.Z - origin.Z);
            job.OwnedMax = new VoxelCoord(ownedMax.X - origin.X, ownedMax.Y - origin.Y, ownedMax.Z - origin.Z);
        }

        job.Owner = null;
        job.Revision = mesh.MeshingRevision;
        job.Tree = tree;
        job.Block = MeshModelDraw.PackBlock(bx, by, bz);
        mesh.Pending++;
        _workers!.Enqueue(job);
    }

    /// <summary>
    /// Copies block (<paramref name="bx"/>, <paramref name="by"/>, <paramref name="bz"/>) of a decoded model level whose
    /// box is <paramref name="size"/> into <paramref name="samples"/>, with its apron: sample s is voxel
    /// 32 b + s - 2 - <see cref="ModelBlockShift"/> on each axis, and voxels outside the box are air.
    /// </summary>
    internal static void ReadModelBlock(
        ReadOnlySpan<byte> voxels,
        VoxelCoord size,
        int bx,
        int by,
        int bz,
        Span<byte> samples
    )
    {
        const int edge = VoxelWorld.BlockSampleEdge;
        const int apron = VoxelWorld.BlockApron;
        samples.Clear();
        var x0 = bx * SurfaceNets.BlockSize - apron - ModelBlockShift;
        var y0 = by * SurfaceNets.BlockSize - apron - ModelBlockShift;
        var z0 = bz * SurfaceNets.BlockSize - apron - ModelBlockShift;
        // The samples' x range that lies inside the box.
        var first = Math.Max(0, -x0);
        var last = Math.Min(edge, size.X - x0);
        if (last <= first)
        {
            return;
        }

        for (var sz = 0; sz < edge; sz++)
        {
            var z = z0 + sz;
            if ((uint)z >= (uint)size.Z)
            {
                continue;
            }

            for (var sy = 0; sy < edge; sy++)
            {
                var y = y0 + sy;
                if ((uint)y >= (uint)size.Y)
                {
                    continue;
                }

                var from = x0 + first + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z);
                voxels.Slice(from, last - first).CopyTo(samples.Slice(first + edge * (sy + edge * sz), last - first));
            }
        }
    }

    /// <summary>A model's box at tree <paramref name="tree"/>: its base-level box halved per tree, rounded up.</summary>
    private static VoxelCoord TreeSize(VoxelCoord size, int tree)
    {
        var cell = (1 << tree) - 1;
        return new VoxelCoord((size.X + cell) >> tree, (size.Y + cell) >> tree, (size.Z + cell) >> tree);
    }

    /// <summary>
    /// Uploads a model job's mesh, or drops it when the model was restarted since. False only when the staging region has
    /// no room left this frame; the job then waits.
    /// </summary>
    private bool TryFinishModel(MeshJob job)
    {
        var mesh = job.Model!;
        if (
            job.Revision != mesh.MeshingRevision
            || (uint)mesh.Handle >= (uint)_modelMeshes.Length
            || !ReferenceEquals(_modelMeshes[mesh.Handle], mesh)
        )
        {
            // Restarted since, or its model is gone (or its match: the meshes were forgotten).
            job.Model = null;
            _freeJobs.Push(job);
            return true;
        }

        if (job.Triangles > 0)
        {
            var buffers = job.Mesh;
            var staged = TryStage(buffers, null, out var offset, out var words, out var vertexBase, out var firstIndex);
            if (staged == Staged.NoStagingRoom)
            {
                return false;
            }

            // A full heap leaves the model without this block until it is meshed again.
            if (staged == Staged.Done)
            {
                mesh.Next[job.Tree]
                    .Add(new ModelBlock(offset, words, vertexBase, firstIndex, buffers.IndexCount, job.Block));
                _stats.Uploaded++;
                _stats.UploadedBytes += (ulong)words * sizeof(uint);
            }
        }

        job.Model = null;
        _freeJobs.Push(job);
        if (--mesh.Pending == 0 && mesh.NextTree == mesh.Trees)
        {
            Swap(_models!, mesh);
        }

        return true;
    }

    /// <summary>
    /// The model's new meshes are all in: they are drawn from now on, the old ones' ranges are freed (later frames'
    /// copies wait for this frame's reads, as the world's do), and the model counts as uploaded unless it changed again
    /// since it was read.
    /// </summary>
    private void Swap(VoxelModels models, ModelMesh mesh)
    {
        AddSharedBlocks(models, mesh);
        FreeBlocks(mesh.Drawn);
        (mesh.Drawn, mesh.Next) = (mesh.Next, mesh.Drawn);
        mesh.Revision = mesh.MeshingRevision;
        mesh.DrawnTrees = mesh.Trees;
        mesh.MeshingRevision = 0;
        if (models.Revision(mesh.Handle) == mesh.Revision)
        {
            models.SetUploaded(mesh.Handle);
        }
    }

    private void FreeBlocks(List<ModelBlock>[] levels)
    {
        foreach (var blocks in levels)
        {
            foreach (var block in blocks)
            {
                // A base's mesh stays the base's.
                if (!block.Shared)
                {
                    FreeRange(block.RangeOffset, block.RangeWords);
                }
            }

            blocks.Clear();
        }
    }

    /// <summary>
    /// Adds to a model that shares a base's meshes (<see cref="VoxelModels.ShareBlocks"/>) the base's drawn meshes of the
    /// blocks it does not mesh itself, marked shared.
    /// </summary>
    private void AddSharedBlocks(VoxelModels models, ModelMesh mesh)
    {
        var shared = models.BaseOf(mesh.Handle);
        if (shared < 0 || (uint)shared >= (uint)_modelMeshes.Length || _modelMeshes[shared] is not { } source)
        {
            return;
        }

        var size = models.Size(mesh.Handle);
        for (var tree = 0; tree < mesh.Trees && tree < source.DrawnTrees; tree++)
        {
            var (countX, countY, _) = ModelBlocks.Counts(TreeSize(size, tree));
            foreach (var block in source.Drawn[tree])
            {
                int bx = (int)(block.Block & 0xFF),
                    by = (int)((block.Block >> 8) & 0xFF),
                    bz = (int)(block.Block >> 16);
                if (!models.MeshesBlock(mesh.Handle, tree, bx + countX * (by + countY * bz)))
                {
                    mesh.Next[tree].Add(block with { Shared = true });
                }
            }
        }
    }

    /// <summary>
    /// The meshes of models removed since the last frame go, and their records wait for the handle's next model (a
    /// handle reused since is restarted instead). Jobs still out for them come back stale: their revision is not 0.
    /// </summary>
    private void ForgetRemovedModels()
    {
        var models = _models!;
        for (var i = _trackedModels.Count - 1; i >= 0; i--)
        {
            var mesh = _trackedModels[i];
            if (models.Revision(mesh.Handle) != 0)
            {
                continue;
            }

            FreeBlocks(mesh.Drawn);
            FreeBlocks(mesh.Next);
            mesh.Revision = 0;
            mesh.DrawnTrees = 0;
            mesh.MeshingRevision = 0;
            mesh.Pending = 0;
            mesh.Followed = false;
            _trackedModels[i] = _trackedModels[^1];
            _trackedModels.RemoveAt(_trackedModels.Count - 1);
        }
    }

    /// <summary>The drawn meshes of level <paramref name="tree"/> of model <paramref name="handle"/>. For tests.</summary>
    internal ReadOnlySpan<ModelBlock> ModelBlocksOf(int handle, int tree) =>
        (uint)handle < (uint)_modelMeshes.Length && _modelMeshes[handle] is { } mesh
            ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mesh.Drawn[tree])
            : default;

    /// <summary>Forgets every model's meshes: the match's heap goes with it.</summary>
    private void ClearModels()
    {
        Array.Clear(_modelMeshes);
        _trackedModels.Clear();
        _decodedHandle = -1;
        _modelListFullReported = false;
        _viewModelListFullReported = false;
    }

    /// <summary>
    /// Writes the frame's model draws into the three lists of <paramref name="slot"/>: the given draws and the retired
    /// models, each at its level clamped to the model's, when its meshes are in.
    /// </summary>
    private (MeshModelList Camera, MeshModelList Near, MeshModelList Far) WriteModelDraws(
        int slot,
        ReadOnlySpan<ModelDraw> draws
    )
    {
        var models = _models!;
        _stats.LastModelTriangles = 0;
        Array.Clear(_modelTrianglesByLevel);
        _modelFrameCount = 0;

        Array.Clear(_listCounts);
        foreach (ref readonly var draw in draws)
        {
            GatherModel(models, draw.Model, draw.Level, draw.Corner, draw.Turn, draw.Tint, draw.Shadow);
        }

        foreach (ref readonly var retired in models.Retired)
        {
            GatherModel(
                models,
                retired.Handle,
                retired.Level,
                retired.Corner,
                retired.Turn,
                retired.Tint,
                ModelShadow.Casts
            );
        }

        GatherObjects(models);

        for (var v = 0; v < _viewCount; v++)
        {
            var target = _views[v];
            target.FrameOf(slot).Models = WriteModelList(
                slot,
                MeshPass.Lists + v,
                target,
                RenderViewTarget.ModelRecordBase(slot),
                RenderViewTarget.MaxModelRecords,
                RenderViewTarget.MaxModelCommands
            );
        }

        return (WriteModelList(slot, 0), WriteModelList(slot, 1), WriteModelList(slot, 2));
    }

    private static int[][] NewModelLists()
    {
        var lists = new int[MeshPass.Lists + RenderViews.MostViews][];
        for (var i = 0; i < lists.Length; i++)
        {
            lists[i] = new int[ModelInstances.MaxModels];
        }

        return lists;
    }

    // The lists a draw may go into (GatherModel's lists): the camera's, the near and far cascades', and each image view's.
    private const int CameraList = 1;
    private const int NearList = 2;
    private const int FarList = 4;
    private const int FirstViewList = 8;
    private const int AllLists = -1;

    /// <summary>
    /// Takes one model draw into the lists whose tests it passes, of those <paramref name="lists"/> allows (every one
    /// unless told): the camera's unless <paramref name="shadow"/> is <see cref="ModelShadow.Only"/>, the cascades'
    /// unless it is <see cref="ModelShadow.None"/>.
    /// </summary>
    private void GatherModel(
        VoxelModels models,
        int handle,
        int level,
        Vector3 corner,
        Quaternion turn,
        float tint,
        ModelShadow shadow,
        int lists = AllLists,
        uint shown = MeshModelDraw.ShowsAll
    )
    {
        if (
            _modelFrameCount == _modelFrame.Length
            || (uint)handle >= (uint)_modelMeshes.Length
            || _modelMeshes[handle] is not { DrawnTrees: > 0 } mesh
            || models.Revision(handle) == 0
            || !models.IsUploaded(handle)
        )
        {
            return;
        }

        var baseLevel = models.BaseLevel(handle);
        var tree = Math.Clamp(level - baseLevel, 0, mesh.DrawnTrees - 1);
        if (mesh.Drawn[tree].Count == 0)
        {
            return;
        }

        var voxelMetres = models.VoxelMetres(handle, tree);
        var size = TreeSize(models.Size(handle), tree);
        var extent = new Vector3(size.X, size.Y, size.Z) * voxelMetres;
        var centre = corner + Vector3.Transform(extent * 0.5f, turn);
        var radius = extent.Length() * 0.5f;
        var seen = (lists & CameraList) != 0 && shadow != ModelShadow.Only && _view.InView(centre, radius);
        var casts = shadow != ModelShadow.None && _view.Shadows;
        var near = (lists & NearList) != 0 && casts && _view.NearCascade.Holds(centre, radius);
        var far = (lists & FarList) != 0 && casts && _view.FarCascade.Holds(centre, radius);
        var views = 0;
        // The views show the player's own body (Only) but not what the camera alone shows (None: the first-person arms).
        for (var v = 0; v < _viewCount && shadow != ModelShadow.None; v++)
        {
            views |= (lists & (FirstViewList << v)) != 0 && _views[v].View.InView(centre, radius) ? 1 << v : 0;
        }

        if (!seen && !near && !far && views == 0)
        {
            return;
        }

        var index = _modelFrameCount++;
        _modelFrame[index] = new ModelFrameDraw(mesh, tree, corner, voxelMetres, turn, tint, shown);
        if (seen)
        {
            _listDraws[0][_listCounts[0]++] = index;
        }

        if (near)
        {
            _listDraws[1][_listCounts[1]++] = index;
        }

        if (far)
        {
            _listDraws[2][_listCounts[2]++] = index;
        }

        for (var v = 0; v < _viewCount; v++)
        {
            if ((views & (1 << v)) != 0)
            {
                var list = MeshPass.Lists + v;
                _listDraws[list][_listCounts[list]++] = index;
            }
        }
    }

    /// <summary>
    /// Writes list <paramref name="list"/>'s draws grouped by model level: for each level drawn and each of its blocks,
    /// one command over that block's draws, which lie together in address order. When they need more records than the
    /// list holds, every level takes the same share of its draws, the first gathered first (warned once a match).
    /// </summary>
    private MeshModelList WriteModelList(int slot, int list) =>
        WriteModelList(
            slot,
            list,
            _pass,
            MeshPass.ModelRecordBase(slot, list),
            MeshPass.MaxModelRecords,
            MeshPass.MaxModelCommands
        );

    /// <summary>
    /// Writes list <paramref name="list"/> into <paramref name="target"/>'s model list of <paramref name="slot"/> (its
    /// list <paramref name="list"/>, or an image view's one list), whose first draw the shaders find at
    /// <paramref name="recordBase"/>, at most <paramref name="mostRecords"/> draws and <paramref name="mostCommands"/>
    /// commands.
    /// </summary>
    private MeshModelList WriteModelList(
        int slot,
        int list,
        IMeshDrawTarget target,
        int recordBase,
        int mostRecords,
        int mostCommands
    )
    {
        var count = _listCounts[list];
        if (count == 0)
        {
            return default;
        }

        // Count the draws of each model level, then give each level its place among the draws in key order.
        var draws = _listDraws[list];
        _keysUsed.Clear();
        for (var i = 0; i < count; i++)
        {
            var key = KeyOf(draws[i]);
            if (_keyCounts[key]++ == 0)
            {
                _keysUsed.Add(key);
            }
        }

        var start = 0;
        foreach (var key in _keysUsed)
        {
            _keyStarts[key] = start;
            start += _keyCounts[key];
        }

        for (var i = 0; i < count; i++)
        {
            _sortedDraws[_keyStarts[KeyOf(draws[i])]++] = draws[i];
        }

        // When the draws need more records than the list holds, every model level takes the same share of its draws, the
        // first gathered (the nearest objects) first, so neither a kind nor a part of one (a tree's crowns) drops whole.
        var needed = 0L;
        foreach (var key in _keysUsed)
        {
            var firstOfKey = _sortedDraws[_keyStarts[key] - _keyCounts[key]];
            needed += (long)_keyCounts[key] * _modelFrame[firstOfKey].Mesh.Drawn[key % VoxelModels.MaxLevels].Count;
        }

        var share = needed > mostRecords ? (double)mostRecords / needed : 1;
        if (share < 1)
        {
            ReportModelListFull(list >= MeshPass.Lists, mostRecords, mostCommands);
        }

        // Each level's draws kept, those of the camera's list shown whole before those that fade.
        var groups = 0;
        var from = 0;
        foreach (var key in _keysUsed)
        {
            var instances = _keyCounts[key];
            _keyCounts[key] = 0;
            var at = from;
            from += instances;
            if (share < 1)
            {
                instances = (int)(instances * share);
            }

            if (instances == 0)
            {
                ReportModelListFull(list >= MeshPass.Lists, mostRecords, mostCommands);
                continue;
            }

            var group = _sortedDraws.AsSpan(at, instances);
            _keyGroups[groups++] = (at, instances, list == 0 ? WholeFirst(group) : instances);
        }

        // Block by block, in two rounds: the draws shown whole, then those that fade, so the pass draws the first with
        // shaders that never discard a pixel (one that may keeps the depth test from running before it shades: a third of
        // the forest's models' time).
        var records = target.ModelDrawsOf(slot, list);
        var commands = target.ModelCommandsOf(slot, list);
        var written = 0;
        var commanded = 0;
        var faded = 0;
        for (var round = 0; round < 2; round++)
        {
            for (var g = 0; g < groups; g++)
            {
                var (at, drawn, whole) = _keyGroups[g];
                var group =
                    round == 0 ? _sortedDraws.AsSpan(at, whole) : _sortedDraws.AsSpan(at + whole, drawn - whole);
                if (group.Length > 0)
                {
                    WriteModelGroup(
                        list,
                        group,
                        records,
                        commands,
                        recordBase,
                        mostRecords,
                        mostCommands,
                        ref written,
                        ref commanded,
                        ref faded,
                        round == 1
                    );
                }
            }
        }

        return new MeshModelList(written, commanded, faded);
    }

    /// <summary>
    /// Moves the draws of <paramref name="group"/> shown whole before those that fade, each in the order gathered; the
    /// count shown whole.
    /// </summary>
    private int WholeFirst(Span<int> group)
    {
        var whole = 0;
        var faded = 0;
        foreach (var draw in group)
        {
            if (_modelFrame[draw].Shown == MeshModelDraw.ShowsAll)
            {
                group[whole++] = draw;
            }
            else
            {
                _fadedDraws[faded++] = draw;
            }
        }

        _fadedDraws.AsSpan(0, faded).CopyTo(group[whole..]);
        return whole;
    }

    /// <summary>
    /// Writes one model level's draws <paramref name="group"/> into a list: a command for each of its blocks over a
    /// record per draw, counted into <paramref name="faded"/> too where they fade.
    /// </summary>
    private void WriteModelGroup(
        int list,
        ReadOnlySpan<int> group,
        Span<MeshModelDraw> records,
        Span<DrawIndexedIndirectCommand> commands,
        int recordBase,
        int mostRecords,
        int mostCommands,
        ref int written,
        ref int commanded,
        ref int faded,
        bool fades
    )
    {
        var instances = group.Length;
        ref readonly var first = ref _modelFrame[group[0]];
        var blocks = first.Mesh.Drawn[first.Tree];
        var level = _models!.BaseLevel(first.Mesh.Handle) + first.Tree;
        if (written + blocks.Count * instances > mostRecords || commanded + blocks.Count > mostCommands)
        {
            ReportModelListFull(list >= MeshPass.Lists, mostRecords, mostCommands);
            return;
        }

        faded += fades ? blocks.Count : 0;
        foreach (var block in blocks)
        {
            commands[commanded++] = new DrawIndexedIndirectCommand(
                (uint)block.IndexCount,
                (uint)instances,
                block.FirstIndex,
                0,
                (uint)(recordBase + written)
            );
            foreach (var draw in group)
            {
                ref readonly var model = ref _modelFrame[draw];
                records[written++] = new MeshModelDraw
                {
                    Corner = model.Corner,
                    VoxelMetres = model.VoxelMetres,
                    Turn = model.Turn,
                    VertexBase = block.VertexBase,
                    Block = block.Block,
                    Tint = model.Tint,
                    Shown = model.Shown,
                };
            }

            var triangles = (long)block.IndexCount / 3 * instances;
            if (list == 0)
            {
                _stats.LastModelTriangles += triangles;
                _modelTrianglesByLevel[Math.Min(level, VoxelModels.MaxLevels - 1)] += triangles;
            }
            else if (list < MeshPass.Lists)
            {
                _stats.LastShadowTriangles += triangles;
            }
        }
    }

    private int KeyOf(int draw)
    {
        ref readonly var model = ref _modelFrame[draw];
        return model.Mesh.Handle * VoxelModels.MaxLevels + model.Tree;
    }

    /// <summary>A model list was full: warned once a match for the camera's and the cascades', once for the views'.</summary>
    private void ReportModelListFull(bool view, int mostRecords, int mostCommands)
    {
        ref var reported = ref view ? ref _viewModelListFullReported : ref _modelListFullReported;
        if (reported)
        {
            return;
        }

        reported = true;
        LogModelListFull(mostRecords, mostCommands);
    }

    /// <summary>One model draw taken this frame: its meshes, the tree drawn, and where and how it stands.</summary>
    private readonly record struct ModelFrameDraw(
        ModelMesh Mesh,
        int Tree,
        Vector3 Corner,
        float VoxelMetres,
        Quaternion Turn,
        float Tint,
        uint Shown
    );
}

/// <summary>
/// One model's meshes (<see cref="WorldMeshes"/>): the blocks drawn per tree, and those being meshed for a newer
/// revision of the model, which take their place once all are in.
/// </summary>
internal sealed class ModelMesh
{
    /// <summary>The model's handle in <see cref="VoxelModels"/>.</summary>
    public int Handle;

    /// <summary>The model's revision the drawn blocks are of; 0 before the first are in.</summary>
    public int Revision;

    /// <summary>Trees the drawn blocks cover.</summary>
    public int DrawnTrees;

    /// <summary>The revision being meshed, or 0 for none.</summary>
    public int MeshingRevision;

    /// <summary>Trees of the revision being meshed.</summary>
    public int Trees;

    /// <summary>The tree and block (in x, then y, then z order) of the next job to start.</summary>
    public int NextTree;

    /// <summary>See <see cref="NextTree"/>.</summary>
    public int NextBlock;

    /// <summary>Jobs started for the revision being meshed and not back yet.</summary>
    public int Pending;

    /// <summary>True while the handle has a model the meshes follow (<c>WorldMeshes</c>' list of them).</summary>
    public bool Followed;

    /// <summary>The drawn blocks of each tree.</summary>
    public List<ModelBlock>[] Drawn =
    [
        [],
        [],
        [],
    ];

    /// <summary>The blocks of each tree of the revision being meshed, as they come in.</summary>
    public List<ModelBlock>[] Next =
    [
        [],
        [],
        [],
    ];
}

/// <summary>One block of a model level in the mesh heap: its range, where its vertices and indices start, and its place.</summary>
internal readonly record struct ModelBlock(
    uint RangeOffset,
    int RangeWords,
    uint VertexBase,
    uint FirstIndex,
    int IndexCount,
    uint Block,
    bool Shared = false
);
