using System.Globalization;
using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Assets;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Rendering;
using Itera.Engine.Voxels;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Hands every placed mesh asset to the renderer (<see cref="MeshInstances"/>) (frame group, after
/// <c>CameraExtractSystem</c> and <see cref="LooseBodyModelSystem"/>, before <c>AtmosphereSystem</c>): each entity with a
/// <see cref="MeshPlacement"/> and a <see cref="DrawnMesh"/> is drawn where it is between the last two fixed steps, its
/// pose blended by <see cref="FrameTime.Alpha"/> and scaled. Reads those two; writes no components. Registers the
/// console's <c>meshes</c> (the run's assets; placing one or many in front of the camera, on the ground where a voxel
/// world is, tagged <see cref="ConsoleMesh"/>; removing those).
/// </summary>
public sealed class MeshDrawSystem : BaseSystem<World, FrameTime>, IFrameUpdateSystem
{
    /// <summary>How far in front of the camera <c>meshes place</c> puts a mesh, in metres.</summary>
    public const float PlaceMetres = 4;

    /// <summary>The most meshes <c>meshes test</c> places.</summary>
    public const int MostTest = 4096;

    private static readonly QueryDescription s_meshes = new QueryDescription().WithAll<MeshPlacement, DrawnMesh>();
    private static readonly QueryDescription s_placed = new QueryDescription().WithAll<ConsoleMesh>();
    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    private readonly MeshInstances _instances;
    private readonly MeshAssets _assets;
    private readonly VoxelWorld? _voxels;
    private readonly ConsoleCommands? _commands;

    /// <param name="world">The ECS world.</param>
    /// <param name="instances">The frame's mesh instances.</param>
    /// <param name="assets">The run's mesh assets, which <c>meshes</c> lists and places.</param>
    /// <param name="voxels">The match's voxel world, whose ground <c>meshes</c> places on; none for a world without voxels.</param>
    /// <param name="commands">The console's commands, for <c>meshes</c>; none in tests.</param>
    public MeshDrawSystem(
        World world,
        MeshInstances instances,
        MeshAssets assets,
        VoxelWorld? voxels = null,
        ConsoleCommands? commands = null
    )
        : base(world)
    {
        _instances = instances;
        _assets = assets;
        _voxels = voxels;
        _commands = commands;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<MeshPlacement, DrawnMesh>(in s_meshes);
        EcsChecks.RequireAll<CameraPose, ActiveCamera>(in s_camera);
        _commands?.Register(
            "meshes",
            "[place NAME [SCALE] | test COUNT [NAME] | clear]",
            "Lists the run's mesh assets (models from glTF files); place puts one 4 m in front of the camera, facing it, on the ground; test puts COUNT in rows ahead (the first asset unless named); clear removes those it placed.",
            CommandScope.Local,
            Meshes
        );
    }

    public override void Update(in FrameTime time)
    {
        var job = new AddMeshes { Instances = _instances, Alpha = time.Alpha };
        World.InlineQuery<AddMeshes, MeshPlacement, DrawnMesh>(in s_meshes, ref job);
    }

    private void Meshes(CommandContext context)
    {
        if (context.Count == 0)
        {
            for (var i = 0; i < _assets.Count; i++)
            {
                var asset = _assets[i];
                var extent = asset.BoundsMax - asset.BoundsMin;
                context.Print(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{i}: {asset.Name}, {asset.TriangleCount} triangles, {asset.Materials.Length} material(s), {asset.Textures.Length} texture(s), {extent.X:F2} x {extent.Y:F2} x {extent.Z:F2} m"
                    )
                );
            }

            context.Print($"{_assets.Count} mesh asset(s)");
            return;
        }

        if (context.Is(0, "clear"))
        {
            var count = World.CountEntities(in s_placed);
            World.Destroy(in s_placed);
            context.Print($"{count} mesh(es) removed");
            return;
        }

        var place = context.Is(0, "place");
        var test = context.Is(0, "test");
        if (!place && !test)
        {
            context.PrintUsage();
            return;
        }

        if (!FindCamera(out var camera))
        {
            context.PrintError("meshes: no camera to place it in front of");
            return;
        }

        var name = place ? (context.Count > 1 ? context[1] : null) : (context.Count > 2 ? context[2] : null);
        var index = name is null ? (_assets.Count > 0 ? 0 : -1) : _assets.Find(name);
        if (index < 0)
        {
            context.PrintError(
                name is null ? "meshes: the run has no mesh assets" : $"meshes: no mesh asset named {name}"
            );
            return;
        }

        var forward = ThirdPersonCamera.Forward(camera.Yaw, 0);
        var right = new Vector3(-forward.Z, 0, forward.X);
        // The asset's front (glTF's +z) faces the camera.
        var facing = Quaternion.CreateFromYawPitchRoll(camera.Yaw, 0, 0);
        if (place)
        {
            var scale = 1f;
            if (context.Count > 2 && !(context.TryGetFloat(2, out scale) && scale > 0))
            {
                context.PrintUsage();
                return;
            }

            var at = OnGround(camera.Position + forward * PlaceMetres);
            World.Create(MeshPlacement.At(at, facing, scale), DrawnMesh.Of(index), new ConsoleMesh());
            context.Print(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_assets[index].Name} placed at ({at.X:F2}, {at.Y:F2}, {at.Z:F2}) m"
                )
            );
            return;
        }

        if (!context.TryGetInt(1, out var many) || many < 1)
        {
            context.PrintUsage();
            return;
        }

        many = Math.Min(many, MostTest);
        var size = _assets[index].BoundsMax - _assets[index].BoundsMin;
        var spacing = Math.Max(1.5f, 1.25f * Math.Max(size.X, size.Z));
        var columns = (int)MathF.Ceiling(MathF.Sqrt(many));
        for (var i = 0; i < many; i++)
        {
            var row = i / columns;
            var column = i % columns - (columns - 1) / 2f;
            var at = OnGround(camera.Position + forward * (PlaceMetres + row * spacing) + right * (column * spacing));
            World.Create(MeshPlacement.At(at, facing), DrawnMesh.Of(index), new ConsoleMesh());
        }

        context.Print($"{many} {_assets[index].Name} placed");
    }

    /// <summary>
    /// The top of the voxel world's ground under (or over) <paramref name="point"/>: a ray down from 64 m above it, through
    /// coarser levels where level 0 is not loaded. Without a voxel world, with no ground within 192 m below it, or with the
    /// ray starting inside the ground, 1.6 m below it.
    /// </summary>
    private Vector3 OnGround(Vector3 point)
    {
        const float Above = 64;
        const float Reach = 256;
        if (
            _voxels is not null
            && _voxels.Raycast(point + new Vector3(0, Above, 0), -Vector3.UnitY, Reach, out var hit, coarse: true)
            && hit.DistanceMetres > 0
        )
        {
            return point with { Y = point.Y + Above - hit.DistanceMetres };
        }

        return point - new Vector3(0, 1.6f, 0);
    }

    private bool FindCamera(out CameraPose camera)
    {
        var find = new FindActiveCamera();
        World.InlineQuery<FindActiveCamera, CameraPose>(in s_camera, ref find);
        camera = find.Pose;
        return find.Found;
    }

    private struct FindActiveCamera : IForEach<CameraPose>
    {
        public CameraPose Pose;
        public bool Found;

        public void Update(ref CameraPose pose)
        {
            Pose = pose;
            Found = true;
        }
    }

    private struct AddMeshes : IForEach<MeshPlacement, DrawnMesh>
    {
        public MeshInstances Instances;
        public float Alpha;

        public readonly void Update(ref MeshPlacement placement, ref DrawnMesh mesh)
        {
            var position = Vector3.Lerp(placement.PreviousPosition, placement.Position, Alpha);
            var orientation = Quaternion.Slerp(placement.PreviousOrientation, placement.Orientation, Alpha);
            var transform =
                Matrix4x4.CreateScale(placement.Scale)
                * Matrix4x4.CreateFromQuaternion(orientation)
                * Matrix4x4.CreateTranslation(position);
            Instances.Add(mesh.Asset, in transform, mesh.Tint, mesh.Shadow);
        }
    }
}
