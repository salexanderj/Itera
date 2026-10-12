using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Input;
using Itera.Engine.Objects;
using Itera.Engine.Settings;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Systems;

/// <summary>
/// Puts the <see cref="ActiveCamera"/> at the local player's eye (frame group, first) while it walks: the feet and the
/// eye's ease drawn between the last two fixed steps by <see cref="FrameTime.Alpha"/>, looking where the command looks
/// plus the mouse movement since the last step, which the next step will add, so looking stays smooth at any frame
/// rate. While the player flies the debug camera, it leaves the camera to <see cref="DebugFlyCameraSystem"/>. A
/// <see cref="Bodiless"/> player keeps its eye where its body was (nothing moves it) and still looks about; a game may
/// move the camera on from there. With <see cref="PlayerInputBlock.Blocked"/> the mouse adds no look since the step,
/// as the input does nothing at the steps. With the third-person camera on (<see cref="ThirdPersonView"/>) the camera
/// sits behind that eye's right shoulder instead (<see cref="ThirdPersonCamera.Position"/>, pulled in by the voxels and
/// the voxel objects).
/// Reads <see cref="ThirdPersonView"/>, <see cref="PlayerMode"/>, <see cref="PlayerCommand"/>,
/// <see cref="CharacterSize"/>, <see cref="CharacterPosition"/>, <see cref="EyeSmoothing"/>, <see cref="Bodiless"/>,
/// and <see cref="IFrameInput"/>; writes <see cref="CameraPose"/>.
/// </summary>
public sealed class PlayerCameraSystem : BaseSystem<World, FrameTime>, IFrameUpdateSystem
{
    private static readonly QueryDescription s_player = new QueryDescription().WithAll<
        PlayerMode,
        PlayerCommand,
        CharacterSize,
        CharacterPosition,
        EyeSmoothing,
        LocalPlayer
    >();

    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    private static readonly QueryDescription s_bodiless = new QueryDescription().WithAll<LocalPlayer, Bodiless>();

    private static readonly QueryDescription s_view = new QueryDescription().WithAll<LocalPlayer, ThirdPersonView>();

    private readonly IFrameInput _input;
    private readonly float _radiansPerPixel;

    /// <summary>The player's settings, whose mouse sensitivity is read each frame; the options' without them (tests).</summary>
    private readonly PlayerSettings? _settings;
    private readonly VoxelWorld? _voxels;
    private readonly ObjectRays? _objects;
    private readonly PlayerInputBlock? _block;
    private readonly EngineOptions _engine;
    private readonly CameraPose? _start;

    /// <param name="world">The ECS world.</param>
    /// <param name="input">The frame's input, for the mouse since the last step.</param>
    /// <param name="options">Engine options, for the mouse sensitivity.</param>
    /// <param name="voxels">The voxels that pull the third-person camera in; none pull it in without them.</param>
    /// <param name="block">Whether the player's input acts; none in tests that do not block it.</param>
    /// <param name="settings">The player's settings, whose mouse sensitivity is read each frame; the options' without them.</param>
    /// <param name="start">
    /// Where the game's camera starts: <see cref="Initialize"/> makes it there (<see cref="CameraRules.CreateCamera"/>);
    /// none in tests that make their own.
    /// </param>
    /// <param name="objects">The voxel objects that pull the third-person camera in too; none without them.</param>
    public PlayerCameraSystem(
        World world,
        IFrameInput input,
        IOptions<EngineOptions> options,
        VoxelWorld? voxels = null,
        PlayerInputBlock? block = null,
        PlayerSettings? settings = null,
        CameraPose? start = null,
        ObjectRays? objects = null
    )
        : base(world)
    {
        _objects = objects;
        ArgumentNullException.ThrowIfNull(options);
        _engine = options.Value;
        _start = start;
        _block = block;
        _settings = settings;
        _input = input;
        _voxels = voxels;
        _radiansPerPixel = CameraRules.RadiansPerPixel(options.Value);
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<PlayerMode, PlayerCommand, CharacterSize, CharacterPosition>(in s_player);
        EcsChecks.RequireAll<EyeSmoothing>(in s_player);
        EcsChecks.RequireAll<CameraPose>(in s_camera);
        EcsChecks.RequireAll<LocalPlayer, Bodiless>(in s_bodiless);
        EcsChecks.RequireAll<LocalPlayer, ThirdPersonView>(in s_view);
        if (_start is { } start)
        {
            CameraRules.CreateCamera(World, in start, _engine);
        }
    }

    public override void Update(in FrameTime frame)
    {
        var eye = new Eye { Alpha = Math.Clamp(frame.Alpha, 0, 1) };
        World.InlineQuery<Eye, PlayerMode, PlayerCommand, CharacterSize, CharacterPosition, EyeSmoothing>(
            in s_player,
            ref eye
        );
        var bodiless = World.CountEntities(in s_bodiless) > 0;
        if (!eye.Found || (eye.Flying && !bodiless))
        {
            return;
        }

        var radiansPerPixel = _settings?.RadiansPerPixel ?? _radiansPerPixel;
        // Blocked, the input does nothing at the steps: the look since the step would turn the picture only to snap back
        // at the next.
        var blocked = _block is { Blocked: true };
        var look = _input.Frame.PointerCaptured && !blocked ? _input.MouseSinceTick * radiansPerPixel : Vector2.Zero;
        var yaw = MathF.IEEERemainder(eye.Yaw - look.X, 2 * MathF.PI);
        var pitch = Math.Clamp(eye.Pitch - look.Y, -CameraRules.MaxPitch, CameraRules.MaxPitch);
        var shoulder = new FindView();
        World.InlineQuery<FindView, ThirdPersonView>(in s_view, ref shoulder);
        var set = new SetPose
        {
            Pose = new CameraPose
            {
                Position = shoulder.On
                    ? ThirdPersonCamera.Position(_voxels, eye.Position, yaw, pitch, shoulder.Metres, _objects)
                    : eye.Position,
                Yaw = yaw,
                Pitch = pitch,
            },
        };
        World.InlineQuery<SetPose, CameraPose>(in s_camera, ref set);
    }

    /// <summary>
    /// The eye drawn <paramref name="alpha"/> of the way from the last step to this one: the feet between the two, the
    /// eye's height, and the eye's ease after a step or a crouch between the two.
    /// </summary>
    public static Vector3 EyeAt(
        in CharacterPosition position,
        in CharacterSize size,
        in EyeSmoothing smoothing,
        float alpha
    )
    {
        var feet = Vector3.Lerp(position.PreviousFeet, position.Feet, alpha);
        var offset =
            smoothing.PreviousOffset
            + (smoothing.Offset - smoothing.PreviousOffset) * alpha
            + smoothing.PreviousHeight
            + (smoothing.Height - smoothing.PreviousHeight) * alpha;
        return feet + new Vector3(0, size.EyeMetres + offset, 0);
    }

    private struct Eye : IForEach<PlayerMode, PlayerCommand, CharacterSize, CharacterPosition, EyeSmoothing>
    {
        public float Alpha;
        public bool Found;
        public bool Flying;
        public Vector3 Position;
        public float Yaw;
        public float Pitch;

        public void Update(
            ref PlayerMode mode,
            ref PlayerCommand command,
            ref CharacterSize size,
            ref CharacterPosition position,
            ref EyeSmoothing smoothing
        )
        {
            // There is one local player; if a bug made several, the last one visited wins.
            Found = true;
            Flying = mode.Flying;
            Position = EyeAt(in position, in size, in smoothing, Alpha);
            Yaw = command.Yaw;
            Pitch = command.Pitch;
        }
    }

    private struct FindView : IForEach<ThirdPersonView>
    {
        public bool On;
        public float Metres;

        public void Update(ref ThirdPersonView view)
        {
            On = view.On;
            Metres = view.Metres > 0 ? view.Metres : ThirdPersonCamera.StartMetres;
        }
    }

    private struct SetPose : IForEach<CameraPose>
    {
        public CameraPose Pose;

        public readonly void Update(ref CameraPose pose) => pose = Pose;
    }
}
