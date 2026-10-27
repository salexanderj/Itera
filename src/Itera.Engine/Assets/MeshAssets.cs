using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Assets;

/// <summary>
/// The run's mesh assets (<see cref="MeshAsset"/>): models a game declares from glTF files (<see cref="Declare"/>: a whole
/// file, a part of one by its node, or a model bound to another's rig), meshes it builds in code (<see cref="Add"/>), and
/// variants of either with some materials' base colours replaced
/// (<see cref="DeclareVariant"/>: a team's colours), each known by its name and its index (<see cref="Find"/>), and
/// which assets are drawn in another's place where it is small on the image (<see cref="DeclareLevel"/>). A game
/// declares them at its run startup (<c>IRunStartup</c>, the run's "game" step), mods at theirs; the run's "mesh assets"
/// step, after the mods', declares the extra models of <c>Engine:Assets:Extra</c> and reads every declared file on
/// worker threads (<see cref="LoadAll"/>), and a file that cannot be read stops the run with a message naming it. Assets
/// stay for the whole run, so an index never goes stale and needs no generation. A run with graphics reads a model's
/// textures from its compressed textures where they match its images (<see cref="CompressedTextures"/>) and decodes the
/// rest; a run without graphics reads none. Main thread.
/// </summary>
public sealed partial class MeshAssets(ILogger<MeshAssets> logger, IOptions<EngineOptions>? options = null)
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    /// <summary>The assets declared and added.</summary>
    public int Count => _entries.Count;

    /// <summary>True once the run's "mesh assets" step has read every declared file.</summary>
    public bool Loaded { get; private set; }

    /// <summary>
    /// Declares the glTF file at <paramref name="path"/> (relative paths from the executable's folder) as the asset
    /// <paramref name="name"/>, its units <paramref name="metresPerUnit"/> metres (glTF's are metres; 0.01 for a file in
    /// centimetres), read at the run's "mesh assets" step. Returns its index. Before that step only.
    /// <list type="bullet">
    /// <item>With <paramref name="node"/>, the asset is that node of the file and what lies under it, a part: in the
    /// space of the rig's joint above it, so a model attached to that joint stands as the file holds it; rigid, with no
    /// rig, and only the materials it uses. A whole file leaves out every mesh not skinned that hangs under a joint.</item>
    /// <item>With <paramref name="rigOf"/>, an asset declared before it whose rig it was bound on: its joints matched
    /// by name, it takes that rig's joint order, so the same index is the same joint in both, and its clips. Its joints,
    /// their parents, and their rest pose must be that rig's, and it has no clips of its own, or the step stops naming
    /// the first difference.</item>
    /// </list>
    /// </summary>
    public int Declare(string name, string path, float metresPerUnit = 1f, string? node = null, string? rigOf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (node is not null && rigOf is not null)
        {
            throw new ArgumentException($"The mesh asset '{name}' is a part, which has no rig to bind.", nameof(rigOf));
        }

        if (rigOf is not null && !_byName.ContainsKey(rigOf))
        {
            throw new InvalidOperationException(
                $"The mesh asset '{name}' is bound on the rig of '{rigOf}', which is not declared or added before it."
            );
        }

        if (!(metresPerUnit > 0) || !float.IsFinite(metresPerUnit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(metresPerUnit),
                metresPerUnit,
                "A model's unit must be a positive number of metres."
            );
        }

        var full = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        return AddEntry(name, new Entry(name, full, metresPerUnit, null) { Node = node, RigOf = rigOf });
    }

    /// <summary>Adds a mesh built in code as the asset <paramref name="name"/> and returns its index. Before the "mesh assets" step only.</summary>
    public int Add(string name, MeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return AddEntry(name, new Entry(name, null, 1f, asset));
    }

    /// <summary>
    /// Declares the asset <paramref name="name"/> as a variant of the asset <paramref name="of"/> (declared or added
    /// before it): the same vertices, indices, primitives, textures, rig, and clips, with the base colour of each of its
    /// materials named in <paramref name="baseColours"/> (linear RGBA) replaced, so a model can be drawn in a team's
    /// colours. Made at the run's "mesh assets" step, after the files are read; a material name the source lacks stops
    /// the step, naming it. Returns its index. Before that step only.
    /// </summary>
    public int DeclareVariant(string name, string of, IReadOnlyDictionary<string, Vector4> baseColours)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(of);
        ArgumentNullException.ThrowIfNull(baseColours);
        if (!_byName.ContainsKey(of))
        {
            throw new InvalidOperationException(
                $"The mesh asset '{name}' is a variant of '{of}', which is not declared or added before it."
            );
        }

        foreach (var (material, colour) in baseColours)
        {
            if (
                !float.IsFinite(colour.X)
                || !float.IsFinite(colour.Y)
                || !float.IsFinite(colour.Z)
                || !float.IsFinite(colour.W)
            )
            {
                throw new ArgumentException($"The colour of material '{material}' is not finite.", nameof(baseColours));
            }
        }

        return AddEntry(
            name,
            new Entry(name, null, 1f, null)
            {
                Of = of,
                Colours = new Dictionary<string, Vector4>(baseColours, StringComparer.Ordinal),
            }
        );
    }

    /// <summary>The most levels one asset may have (<see cref="DeclareLevel"/>).</summary>
    public const int MostLevels = 8;

    /// <summary>
    /// Declares the asset <paramref name="level"/> (declared or added before it) a level of detail of the asset
    /// <paramref name="asset"/>: drawn in its place where an instance's bounding sphere is less than
    /// <paramref name="belowPixels"/> pixels tall on the image it is drawn into (in a shadow cascade, texels). An asset
    /// has at most <see cref="MostLevels"/> levels, kept largest <paramref name="belowPixels"/> first, no two alike. A
    /// level has no levels of its own. A skinned asset's level is bound on its rig (<c>rigOf: asset</c> in
    /// <see cref="Declare"/>, a variant of such a level, or a mesh built on the same <see cref="Rig"/>), so its
    /// instances' skinning matrices pose every level; a rigid asset's level is rigid. The step stops, naming both, when
    /// a level is not. A variant (<see cref="DeclareVariant"/>) has no levels unless its own are declared. Before the
    /// "mesh assets" step only.
    /// </summary>
    public void DeclareLevel(string asset, string level, float belowPixels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        if (Loaded)
        {
            throw new InvalidOperationException(
                $"The level '{level}' of '{asset}' comes after the run's mesh assets step: declare levels at the run's startup."
            );
        }

        if (!(belowPixels > 0) || !float.IsFinite(belowPixels))
        {
            throw new ArgumentOutOfRangeException(
                nameof(belowPixels),
                belowPixels,
                "A level's height must be a positive number of pixels."
            );
        }

        if (!_byName.TryGetValue(asset, out var assetIndex) || !_byName.TryGetValue(level, out var levelIndex))
        {
            throw new InvalidOperationException(
                $"The level '{level}' of '{asset}': both must be declared or added before it."
            );
        }

        if (assetIndex == levelIndex)
        {
            throw new ArgumentException($"The mesh asset '{asset}' cannot be a level of itself.", nameof(level));
        }

        var entry = _entries[assetIndex];
        var made = _entries[levelIndex];
        if (entry.IsLevel || made.Levels is not null)
        {
            throw new InvalidOperationException(
                $"The level '{level}' of '{asset}': a level has no levels of its own, and '{(entry.IsLevel ? asset : level)}' "
                    + (entry.IsLevel ? "is a level." : "has levels.")
            );
        }

        // A level bound on a rig must be bound on the asset's (a variant on either side counts as its source).
        if (Root(made).RigOf is { } rigOf && !ReferenceEquals(Root(_entries[_byName[rigOf]]), Root(entry)))
        {
            throw new InvalidOperationException(
                $"The level '{level}' of '{asset}' is bound on the rig of '{rigOf}': bind it with rigOf: '{asset}'."
            );
        }

        var levels = entry.Levels ??= [];
        if (levels.Count == MostLevels)
        {
            throw new InvalidOperationException($"The mesh asset '{asset}' has {MostLevels} levels, the most it may.");
        }

        var at = 0;
        foreach (var other in levels)
        {
            if (other.Asset == levelIndex)
            {
                throw new InvalidOperationException($"The mesh asset '{level}' is a level of '{asset}' already.");
            }

            if (other.BelowPixels == belowPixels)
            {
                throw new InvalidOperationException(
                    $"The mesh asset '{asset}' has a level below {belowPixels} pixels already: give '{level}' another height."
                );
            }

            at += other.BelowPixels > belowPixels ? 1 : 0;
        }

        levels.Insert(at, new MeshAssetLevel(levelIndex, belowPixels));
        made.IsLevel = true;
    }

    /// <summary>
    /// The levels of the asset at <paramref name="index"/> (<see cref="DeclareLevel"/>), largest
    /// <see cref="MeshAssetLevel.BelowPixels"/> first; none for an asset without levels.
    /// </summary>
    public IReadOnlyList<MeshAssetLevel> LevelsOf(int index) =>
        _entries[index].Levels ?? (IReadOnlyList<MeshAssetLevel>)[];

    /// <summary>The index of the asset <paramref name="name"/>, or -1 when there is none.</summary>
    public int Find(string name) => _byName.TryGetValue(name, out var index) ? index : -1;

    /// <summary>
    /// The glTF files declared so far, in order, each once with its full path and unit, under the name of its first
    /// declaration: what a check of a game's models reads (<see cref="ModelInspection"/>). The extra models of
    /// <c>Engine:Assets:Extra</c> join them at the mesh assets step.
    /// </summary>
    public IReadOnlyList<DeclaredModel> Files =>
        [
            .. _entries
                .Where(entry => entry.Path is not null)
                .DistinctBy(entry => entry.Path, StringComparer.Ordinal)
                .Select(entry => new DeclaredModel(entry.Name, entry.Path!, entry.MetresPerUnit)),
        ];

    /// <summary>
    /// The asset at <paramref name="index"/> when there is one and it is read (a declared file's once the run's "mesh
    /// assets" step has read it; one added in code at once). Allocates nothing.
    /// </summary>
    public bool TryGet(int index, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MeshAsset? asset)
    {
        asset = (uint)index < (uint)_entries.Count ? _entries[index].Asset : null;
        return asset is not null;
    }

    /// <summary>The asset at <paramref name="index"/> (<see cref="Find"/>); a declared file's once the run has read it.</summary>
    public MeshAsset this[int index] =>
        _entries[index].Asset
        ?? throw new InvalidOperationException(
            $"The mesh asset '{_entries[index].Name}' is not read yet: models are read at the run's mesh assets step."
        );

    /// <summary>
    /// Reads every declared file not read yet, in parallel on worker threads, logs their warnings and notes, and then
    /// the totals; with <paramref name="textures"/> false (no graphics), without their textures. Throws when a file
    /// cannot be read, after logging each that failed. The run's "mesh assets" step; once.
    /// </summary>
    internal void LoadAll(bool textures)
    {
        if (Loaded)
        {
            return;
        }

        DeclareExtras();
        var start = Stopwatch.GetTimestamp();
        var warnings = new List<string>[_entries.Count];
        var notes = new List<string>[_entries.Count];
        var reading = textures ? TextureReading.PreferCompressed : TextureReading.None;
        var failures = new Exception?[_entries.Count];
        for (var i = 0; i < _entries.Count; i++)
        {
            (warnings[i], notes[i]) = ([], []);
        }

        // Each file once, on a worker thread, its assets read from it in turn: a file's parts and its whole share its
        // textures (GltfImport.Import).
        var files = Enumerable
            .Range(0, _entries.Count)
            .Where(i => _entries[i].Asset is null && _entries[i].Path is not null)
            .GroupBy(i => _entries[i].Path!, StringComparer.Ordinal)
            .Select(group => group.ToArray())
            .ToArray();
        Parallel.ForEach(
            files,
            group =>
            {
                GltfFile file;
                try
                {
                    file = GltfImport.Open(_entries[group[0]].Path!, reading);
                }
                catch (Exception ex)
                {
                    foreach (var i in group)
                    {
                        failures[i] = ex;
                    }

                    return;
                }

                foreach (var i in group)
                {
                    var entry = _entries[i];
                    try
                    {
                        entry.Asset = GltfImport.Import(
                            file,
                            entry.Name,
                            entry.MetresPerUnit,
                            warnings[i],
                            notes[i],
                            node: entry.Node
                        );
                    }
                    catch (Exception ex)
                    {
                        failures[i] = ex;
                    }
                }
            }
        );

        var failed = 0;
        string? firstFailure = null;
        for (var i = 0; i < _entries.Count; i++)
        {
            foreach (var warning in warnings[i])
            {
                LogWarning(_entries[i].Name, warning);
            }

            foreach (var note in notes[i])
            {
                LogNote(_entries[i].Name, note);
            }

            if (failures[i] is { } failure)
            {
                failed++;
                firstFailure ??= failure.Message;
                LogFailed(failure, _entries[i].Name, _entries[i].Path ?? "");
            }
        }

        if (failed > 0)
        {
            throw new InvalidOperationException($"{failed} mesh asset(s) could not be read; the first: {firstFailure}");
        }

        BindRigs();
        MakeVariants();
        CheckLevels();
        Loaded = true;
        long triangles = 0;
        long textureBytes = 0;
        var compressed = 0;
        // A texture assets share counts once.
        var counted = new HashSet<MeshTextureData>(ReferenceEqualityComparer.Instance);
        foreach (var entry in _entries)
        {
            triangles += entry.Asset!.TriangleCount;
            foreach (var texture in entry.Asset.Textures)
            {
                if (counted.Add(texture))
                {
                    textureBytes += texture.Texels.Length;
                    compressed += texture.Compressed ? 1 : 0;
                }
            }
        }

        var textureCount = counted.Count;

        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var megabytes = textureBytes / (1024.0 * 1024.0);
        LogLoaded(
            _entries.Count,
            triangles,
            textureCount,
            compressed,
            megabytes,
            textures ? "" : " (not decoded: no graphics)",
            milliseconds
        );
    }

    /// <summary>
    /// Declares the extra models of <c>Engine:Assets:Extra</c>, after the game's and the mods' own, each path from the
    /// folder the run started in. A name a model has already stops the run, naming the option.
    /// </summary>
    private void DeclareExtras()
    {
        var extras = options?.Value.Assets.Extra ?? [];
        for (var i = 0; i < extras.Length; i++)
        {
            var extra = extras[i];
            if (_byName.ContainsKey(extra.Name))
            {
                throw new InvalidOperationException(
                    $"{EngineOptions.SectionName}:Assets:Extra:{i}:Name is {extra.Name}, which a model of the game's or a "
                        + "mod's has already: give the extra model another name."
                );
            }

            var path = Path.GetFullPath(extra.Path);
            Declare(extra.Name, path, extra.MetresPerUnit);
            LogExtra(extra.Name, path);
        }
    }

    /// <summary>Binds each asset declared on another's rig to that rig, in the order declared (<see cref="Bound"/>).</summary>
    private void BindRigs()
    {
        foreach (var entry in _entries)
        {
            if (entry.RigOf is not null)
            {
                entry.Asset = Bound(entry.Asset!, _entries[_byName[entry.RigOf]].Asset!, entry.RigOf);
            }
        }
    }

    /// <summary>
    /// <paramref name="asset"/> bound on <paramref name="source"/>'s rig (named <paramref name="of"/>): its joints taken
    /// in the source's order, its vertices' joints and its points' joints moved with them, keeping its own rest, offsets,
    /// inverse binds, and reaches, and the source's clips. A joint may rest at another place than the source's (a body
    /// of other proportions): the clips are then rebased onto its own (<see cref="Rebased"/>). Throws, naming the first
    /// difference, when the joints, their parents, or their rest turns are not the source's (within
    /// <see cref="BoundRestTurn"/>), or when it has clips of its own.
    /// </summary>
    internal static MeshAsset Bound(MeshAsset asset, MeshAsset source, string of)
    {
        var name = asset.Name;
        if (asset.Rig is not { } own || asset.Skin is not { } skin)
        {
            throw new InvalidOperationException($"The mesh asset '{name}' has no rig to bind on '{of}''s.");
        }

        if (source.Rig is not { } rig)
        {
            throw new InvalidOperationException($"The mesh asset '{name}' is bound on '{of}''s rig, which has none.");
        }

        if (own.Clips.Length > 0)
        {
            throw new InvalidOperationException(
                $"The mesh asset '{name}' has {own.Clips.Length} clip(s) of its own: one bound on '{of}''s rig plays its."
            );
        }

        if (own.JointCount != rig.JointCount)
        {
            throw new InvalidOperationException(
                $"The mesh asset '{name}' has {own.JointCount} joints, and '{of}''s rig {rig.JointCount}."
            );
        }

        // Each of the source's joints' place in the asset's own rig.
        var count = rig.JointCount;
        var ownOf = new int[count];
        var sourceOf = new int[count];
        for (var j = 0; j < count; j++)
        {
            var at = own.FindJoint(rig.Names[j]);
            if (at < 0)
            {
                throw new InvalidOperationException(
                    $"The mesh asset '{name}' has no joint '{rig.Names[j]}' of '{of}''s rig."
                );
            }

            (ownOf[j], sourceOf[at]) = (at, j);
        }

        var moved = false;
        for (var j = 0; j < count; j++)
        {
            var parent = rig.Parents[j];
            var ownParent = own.Parents[ownOf[j]];
            if ((parent < 0 ? -1 : ownOf[parent]) != ownParent)
            {
                throw new InvalidOperationException(
                    $"The mesh asset '{name}''s joint '{rig.Names[j]}' has another parent than in '{of}''s rig."
                );
            }

            var (rest, ownRest) = (rig.Rest[j], own.Rest[ownOf[j]]);
            if (MathF.Abs(Quaternion.Dot(rest.Rotation, ownRest.Rotation)) < MathF.Cos(BoundRestTurn / 2))
            {
                throw new InvalidOperationException(
                    $"The mesh asset '{name}''s joint '{rig.Names[j]}' rests turned otherwise than in '{of}''s rig: its "
                        + "clips would not fit it."
                );
            }

            moved |= Vector3.Distance(rest.Translation, ownRest.Translation) > BoundRestMetres;
        }

        JointTransform[] ownRests = [.. ownOf.Select(at => own.Rest[at])];
        var bound = new Rig(
            rig.Names,
            rig.Parents,
            [.. ownOf.Select(at => own.Offsets[at])],
            ownRests,
            [.. ownOf.Select(at => own.InverseBinds[at])],
            [.. ownOf.Select(at => own.Reaches[at])],
            moved ? Rebased(rig.Clips, rig.Rest, ownRests) : rig.Clips
        );
        var skins = new MeshSkinVertex[skin.Length];
        for (var v = 0; v < skin.Length; v++)
        {
            var vertex = skin[v];
            skins[v] = MeshSkinVertex.Pack(
                sourceOf[vertex.Joint(0)],
                sourceOf[vertex.Joint(1)],
                sourceOf[vertex.Joint(2)],
                sourceOf[vertex.Joint(3)],
                new Vector4(vertex.Weight(0), vertex.Weight(1), vertex.Weight(2), vertex.Weight(3))
            );
        }

        var points = Array.ConvertAll(
            asset.Points,
            point => point.Joint < 0 ? point : point with { Joint = sourceOf[point.Joint] }
        );
        return new MeshAsset(
            name,
            asset.Vertices,
            asset.Indices,
            asset.Primitives,
            asset.Materials,
            asset.Textures,
            bound,
            skins,
            points
        );
    }

    /// <summary>
    /// The source's <paramref name="clips"/> for a bound asset whose joints rest at <paramref name="own"/> rather than
    /// the source's <paramref name="rest"/> (one a joint, in the source's order): each clip keeps its name, length, and
    /// turning and scaling tracks. A translation track that only holds the source's rest is left out, so its joint stays
    /// at its own rest. Another, such as the hips' sway, moves by the difference of the two rests.
    /// </summary>
    internal static RigClip[] Rebased(RigClip[] clips, JointTransform[] rest, JointTransform[] own)
    {
        var rebased = new RigClip[clips.Length];
        var tracks = new List<RigTrack>();
        for (var c = 0; c < clips.Length; c++)
        {
            tracks.Clear();
            foreach (var track in clips[c].Tracks)
            {
                var shift = own[track.Joint].Translation - rest[track.Joint].Translation;
                if (track.Path != RigPath.Translation || shift.Length() <= BoundRestMetres)
                {
                    tracks.Add(track);
                }
                else if (!Holds(track, rest[track.Joint].Translation))
                {
                    tracks.Add(Shifted(track, shift));
                }
            }

            rebased[c] = new RigClip(clips[c].Name, [.. tracks], clips[c].Duration);
        }

        return rebased;
    }

    /// <summary>
    /// True when every key of <paramref name="track"/> lies within <see cref="BoundRestMetres"/> of
    /// <paramref name="at"/>, a cubic spline's tangents within it of none.
    /// </summary>
    private static bool Holds(RigTrack track, Vector3 at)
    {
        var cubic = track.Interpolation == RigInterpolation.CubicSpline;
        for (var i = 0; i < track.Values.Length; i++)
        {
            var value = track.Values[i];
            var expected = !cubic || i % 3 == 1 ? at : Vector3.Zero;
            if (Vector3.Distance(new Vector3(value.X, value.Y, value.Z), expected) > BoundRestMetres)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><paramref name="track"/> with each key moved by <paramref name="shift"/>, a cubic spline's tangents kept.</summary>
    private static RigTrack Shifted(RigTrack track, Vector3 shift)
    {
        var cubic = track.Interpolation == RigInterpolation.CubicSpline;
        var values = new Vector4[track.Values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = !cubic || i % 3 == 1 ? track.Values[i] + new Vector4(shift, 0) : track.Values[i];
        }

        return new RigTrack(track.Joint, track.Path, track.Interpolation, track.Times, values);
    }

    /// <summary>
    /// How far a bound asset's joint may rest from the source rig's, in metres, with the source's clips kept as they are;
    /// and how near a translation key must lie to the source's rest to be left out (<see cref="Rebased"/>).
    /// </summary>
    public const float BoundRestMetres = 0.002f;

    /// <summary>How far a bound asset's joint may rest turned from the source rig's, in radians.</summary>
    public const float BoundRestTurn = 0.005f;

    /// <summary>Makes each variant from its source, in the order declared, so a variant of a variant follows it.</summary>
    private void MakeVariants()
    {
        foreach (var entry in _entries)
        {
            if (entry.Of is null || entry.Asset is not null)
            {
                continue;
            }

            var source = _entries[_byName[entry.Of]].Asset!;
            var colours = entry.Colours!;
            var missing = colours.Keys.Where(material => !Array.Exists(source.Materials, m => m.Name == material));
            if (missing.Any())
            {
                throw new InvalidOperationException(
                    $"The mesh asset '{entry.Name}' recolours material(s) {string.Join(", ", missing)}, which '{entry.Of}' lacks."
                );
            }

            var materials = Array.ConvertAll(
                source.Materials,
                material =>
                    colours.TryGetValue(material.Name, out var colour)
                        ? material with
                        {
                            BaseColour = colour,
                        }
                        : material
            );
            entry.Asset = new MeshAsset(
                entry.Name,
                source.Vertices,
                source.Indices,
                source.Primitives,
                materials,
                source.Textures,
                source.Rig,
                source.Skin,
                source.Points
            );
        }
    }

    /// <summary>
    /// Checks each level against its asset, the assets read (<see cref="DeclareLevel"/>): a rigid asset's level is rigid,
    /// and a skinned asset's is bound on its rig (declared with <c>rigOf</c> it, a variant of one so declared, or built on
    /// the same <see cref="Rig"/>). Throws, naming both, at the first that is not.
    /// </summary>
    private void CheckLevels()
    {
        foreach (var entry in _entries)
        {
            foreach (var level in entry.Levels ?? [])
            {
                var made = _entries[level.Asset];
                var (rig, levelRig) = (entry.Asset!.Rig, made.Asset!.Rig);
                if (ReferenceEquals(rig, levelRig))
                {
                    continue;
                }

                if (rig is null)
                {
                    throw new InvalidOperationException(
                        $"The mesh asset '{made.Name}' is a level of '{entry.Name}', which is rigid: a rigid asset's levels are rigid."
                    );
                }

                var boundOn = Root(made).RigOf;
                if (
                    levelRig is null
                    || boundOn is null
                    || !ReferenceEquals(Root(_entries[_byName[boundOn]]), Root(entry))
                )
                {
                    throw new InvalidOperationException(
                        $"The mesh asset '{made.Name}' is a level of '{entry.Name}' but not bound on its rig: declare it with "
                            + $"rigOf: '{entry.Name}', so the same skinning matrices pose both."
                    );
                }
            }
        }
    }

    /// <summary>The entry a variant is made from at the end of its chain (<see cref="DeclareVariant"/>); any other entry itself.</summary>
    private Entry Root(Entry entry)
    {
        while (entry.Of is not null)
        {
            entry = _entries[_byName[entry.Of]];
        }

        return entry;
    }

    private int AddEntry(string name, Entry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Loaded)
        {
            throw new InvalidOperationException(
                $"The mesh asset '{name}' comes after the run's mesh assets step: declare models at the run's startup."
            );
        }

        if (!_byName.TryAdd(name, _entries.Count))
        {
            throw new InvalidOperationException($"There is a mesh asset named '{name}' already.");
        }

        _entries.Add(entry);
        return _entries.Count - 1;
    }

    private sealed class Entry(string name, string? path, float metresPerUnit, MeshAsset? asset)
    {
        public string Name { get; } = name;

        public string? Path { get; } = path;

        public float MetresPerUnit { get; } = metresPerUnit;

        public MeshAsset? Asset { get; set; } = asset;

        /// <summary>The asset a variant is made from, or null for a file or a mesh built in code.</summary>
        public string? Of { get; init; }

        /// <summary>The node a part is read from, or null for the whole file.</summary>
        public string? Node { get; init; }

        /// <summary>The asset whose rig a file's is bound on, or null.</summary>
        public string? RigOf { get; init; }

        /// <summary>A variant's base colours by material name.</summary>
        public Dictionary<string, Vector4>? Colours { get; init; }

        /// <summary>The asset's levels, largest height first, or null for none.</summary>
        public List<MeshAssetLevel>? Levels { get; set; }

        /// <summary>True for an asset that is a level of another.</summary>
        public bool IsLevel { get; set; }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Mesh assets: {Models} model(s), {Triangles} triangles, {Textures} texture(s) ({Compressed} compressed) of {Megabytes:F1} MB{Decoded}, read in {Milliseconds:F0} ms"
    )]
    private partial void LogLoaded(
        int models,
        long triangles,
        int textures,
        int compressed,
        double megabytes,
        string decoded,
        double milliseconds
    );

    [LoggerMessage(Level = LogLevel.Information, Message = "Mesh assets: extra model {Name} from {Path}")]
    private partial void LogExtra(string name, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mesh assets: {Name}: {Warning}")]
    private partial void LogWarning(string name, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mesh assets: {Name}: {Note}")]
    private partial void LogNote(string name, string note);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mesh assets: {Name} ({Path}) could not be read")]
    private partial void LogFailed(Exception exception, string name, string path);
}

/// <summary>
/// A level of detail of a mesh asset (<see cref="MeshAssets.DeclareLevel"/>): the asset drawn in its place (its index in
/// <see cref="MeshAssets"/>) where an instance's bounding sphere is less than <paramref name="BelowPixels"/> pixels tall.
/// </summary>
/// <param name="Asset">The level's asset index.</param>
/// <param name="BelowPixels">The height in pixels (in a shadow cascade, texels) under which it is drawn.</param>
public readonly record struct MeshAssetLevel(int Asset, float BelowPixels);
