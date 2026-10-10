using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// The console's <c>balls</c> (fixed group, before <see cref="PhysicsReadSystem"/>; <see cref="CommandScope.Match"/>):
/// a debug aid that drops balls of one material, made from nothing, in a row across the view 3 m in front of the
/// <see cref="ActiveCamera"/>, at the next step with one. Each is a loose body
/// (<see cref="LooseBodies.TryCreateBall"/>) that falls, rolls, and goes back into the grid when it rests
/// (<see cref="LooseBodySystem"/>; without voxel terrain, what the game decides), so a test run draws models and moves
/// bodies (<c>--Engine:Console:Run=settle 60; balls 8</c>: the settle keeps them bodies past the loading screen). A
/// ball whose space holds any solid voxel is left out (without terrain its space is not checked); when as many bodies
/// as allowed exist, the oldest go first (<see cref="LooseBodyRoom"/>). Reads <see cref="CameraPose"/>; creates
/// <see cref="LooseBody"/> entities.
/// </summary>
public sealed class DebugBallSystem : BaseSystem<World, FixedStepTime>, IFixedUpdateSystem
{
    /// <summary>How far in front of the camera the row lies, in metres.</summary>
    public const float AheadMetres = 3;

    /// <summary>The most balls one command drops.</summary>
    public const int MostBalls = 64;

    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    private readonly LooseBodies _bodies;
    private readonly VoxelWorld? _voxels;
    private readonly LooseBodyRoom _room;
    private readonly ConsoleCommands? _commands;
    private readonly byte _defaultMaterial;
    private int _count;
    private int _radius;
    private byte _material;

    /// <param name="world">The ECS world.</param>
    /// <param name="bodies">The loose bodies.</param>
    /// <param name="voxels">The voxels, which must leave a ball's space empty; null without voxel terrain.</param>
    /// <param name="commands">The console's commands, which get <c>balls</c>; none in tests.</param>
    /// <param name="defaultMaterial">The material a ball is made of when the command names none: the game's (stone, say).</param>
    public DebugBallSystem(
        World world,
        LooseBodies bodies,
        VoxelWorld? voxels,
        ConsoleCommands? commands = null,
        byte defaultMaterial = 1
    )
        : base(world)
    {
        _defaultMaterial = defaultMaterial == VoxelConstants.Air ? (byte)1 : defaultMaterial;
        _bodies = bodies;
        _voxels = voxels;
        _room = new LooseBodyRoom(bodies);
        _commands = commands;
    }

    /// <summary>
    /// Asks the next step with a camera to drop <paramref name="count"/> balls of <paramref name="radius"/>
    /// centimetres, of <paramref name="material"/> (air, the default, is the game's default material).
    /// </summary>
    public void Request(int count, int radius = 12, byte material = VoxelConstants.Air)
    {
        _count = Math.Clamp(count, 0, MostBalls);
        _radius = Math.Clamp(radius, 5, 100);
        _material = material == VoxelConstants.Air ? _defaultMaterial : material;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<CameraPose>(in s_camera);
        _commands?.Register(
            "balls",
            "[count] [radius cm] [material]",
            "Drops balls made from nothing in a row 3 m in front of the camera (8 of 12 cm, of the game's default material, unless given): loose bodies that fall and go back into the grid at rest (without voxel terrain, become what the game decides).",
            CommandScope.Match,
            Balls
        );
    }

    public override void Update(in FixedStepTime step)
    {
        if (_count == 0)
        {
            return;
        }

        // The request waits for a camera: a console line run as the match starts may come before one exists.
        var camera = new FindCamera();
        World.InlineQuery<FindCamera, CameraPose>(in s_camera, ref camera);
        if (!camera.Found)
        {
            return;
        }

        var count = _count;
        _count = 0;

        var forward = ThirdPersonCamera.Forward(camera.Pose.Yaw, 0);
        var right = new Vector3(-forward.Z, 0, forward.X);
        var spacing = 3f * _radius / VoxelConstants.CentimetresPerMetre;
        var first = camera.Pose.Position + forward * AheadMetres - right * (spacing * (count - 1) / 2);
        for (var i = 0; i < count; i++)
        {
            var start = first + right * (spacing * i);
            var space = VoxelEdit.Sphere(
                new VoxelCoord(
                    (int)MathF.Floor(start.X * VoxelConstants.CentimetresPerMetre),
                    (int)MathF.Floor(start.Y * VoxelConstants.CentimetresPerMetre),
                    (int)MathF.Floor(start.Z * VoxelConstants.CentimetresPerMetre)
                ),
                _radius,
                VoxelEditOperation.Fill,
                _material
            );
            if (
                (_voxels is not null && (!_voxels.CanApply(in space) || _voxels.AnySolidIn(in space)))
                || !_room.MakeRoom(World, 1, false)
            )
            {
                continue;
            }

            if (_bodies.TryCreateBall(_radius, _material, start, Vector3.Zero, out var body))
            {
                _bodies.BoxOf(body, out var min, out var max);
                World.Create(
                    new LooseBody
                    {
                        Body = body,
                        BallRadius = _bodies.BallRadiusOf(body),
                        Born = _bodies.BornOf(body),
                    },
                    new BodyPose
                    {
                        Position = start,
                        PreviousPosition = start,
                        Orientation = Quaternion.Identity,
                        PreviousOrientation = Quaternion.Identity,
                    },
                    new BodyBox { Min = min, Max = max }
                );
            }
        }
    }

    private void Balls(CommandContext context)
    {
        var count = 8;
        var radius = 12;
        int material = _defaultMaterial;
        if (
            (context.Count > 0 && (!context.TryGetInt(0, out count) || count < 1 || count > MostBalls))
            || (context.Count > 1 && (!context.TryGetInt(1, out radius) || radius < 5 || radius > 100))
            || (context.Count > 2 && (!context.TryGetInt(2, out material) || material < 1 || material > 255))
        )
        {
            context.PrintUsage();
            return;
        }

        Request(count, radius, (byte)material);
        context.Print($"balls: {count} of {radius} cm, material {material}");
    }

    private struct FindCamera : IForEach<CameraPose>
    {
        public CameraPose Pose;
        public bool Found;

        public void Update(ref CameraPose pose)
        {
            Pose = pose;
            Found = true;
        }
    }
}
