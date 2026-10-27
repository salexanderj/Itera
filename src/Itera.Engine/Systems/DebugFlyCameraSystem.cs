using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Input;
using Itera.Engine.Maths;
using Itera.Engine.Settings;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Systems;

/// <summary>
/// The debug fly camera (frame group, after the player's camera, which makes the camera entity). While the local
/// player flies it (the console's <c>fly</c>) or there is no player, moves every <see cref="FlyCamera"/> once per frame:
/// mouse look while the pointer is captured (a left click; Escape opens the menu, which frees it), and by the movement
/// keybindings (<see cref="MovementKeys"/>, the defaults in brackets) Forward, Back, Left, and Right along the view (W, S,
/// A, D), Jump and Move down straight up and down (Space, C), Sprint four times faster (Shift), and the wheel (or the
/// console's <c>flyspeed</c>) changes the speed. When <c>Engine:Voxels:DebugWalkSpeed</c> is above
/// zero it ignores input and flies a circle of 100 m radius around the origin, <c>Engine:Camera:CircleHeightMetres</c> up, looking along the path, for
/// unattended runs. Reads <see cref="IFrameInput"/> and <see cref="PlayerMode"/>; writes <see cref="CameraPose"/> and
/// <see cref="FlyCamera"/>. A walking player's camera is <see cref="PlayerCameraSystem"/>'s.
/// </summary>
public sealed partial class DebugFlyCameraSystem : BaseSystem<World, FrameTime>, IFrameUpdateSystem
{
    /// <summary>Radius of the automatic flight path, in metres.</summary>
    public const double CircleRadiusMetres = 100;

    /// <summary>Downward tilt on the automatic flight path, so the ground is in view.</summary>
    public const float CirclePitchRadians = -0.2f;

    /// <summary>Speed multiplier while Shift is held.</summary>
    public const float FastFactor = 4;

    /// <summary>Speed multiplier per wheel step.</summary>
    public const float WheelFactor = 1.25f;

    /// <summary>Slowest flying speed the wheel reaches, in metres per second.</summary>
    public const float MinSpeed = 0.25f;

    /// <summary>Fastest flying speed the wheel reaches, in metres per second.</summary>
    public const float MaxSpeed = 500;

    private static readonly QueryDescription s_query = new QueryDescription().WithAll<CameraPose, FlyCamera>();

    private static readonly QueryDescription s_player = new QueryDescription().WithAll<PlayerMode, LocalPlayer>();

    private static readonly QueryDescription s_speeds = new QueryDescription().WithAll<FlyCamera>();

    /// <summary>The input of a frame in which nothing is pressed or held: a blocked player's.</summary>
    private static readonly InputSnapshot s_nothing;

    private readonly IFrameInput _input;
    private readonly PlayerInputBlock? _block;
    private readonly float _circleHeight;
    private readonly PlayArea _area;

    // The match's origin: the camera's pose is local to it, the play area absolute.
    private readonly WorldOrigin _origin;
    private readonly ILogger<DebugFlyCameraSystem> _logger;
    private readonly Vector3 _start;
    private readonly float _startYaw;
    private readonly float _startPitch;
    private readonly double _walkSpeed;
    private readonly float _radiansPerPixel;

    /// <summary>The player's settings, whose mouse sensitivity is read each frame; the options' without them (tests).</summary>
    private readonly PlayerSettings? _settings;
    private readonly MovementKeys _keys;
    private readonly ConsoleCommands? _commands;

    /// <param name="world">The ECS world.</param>
    /// <param name="input">The input of the frame being built.</param>
    /// <param name="options">Camera options, and the automatic flight speed.</param>
    /// <param name="area">The box the camera keeps to.</param>
    /// <param name="start">Where the camera starts, in metres.</param>
    /// <param name="startYaw">The camera's yaw at the start, in radians (0 looks along -Z).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="startPitch">The camera's pitch at the start, in radians (0 is level, negative looks down).</param>
    /// <param name="commands">The console's commands, which gets <c>flyspeed</c>; none in tests.</param>
    /// <param name="settings">The player's settings, whose mouse sensitivity is read each frame; the options' without them.</param>
    /// <param name="keys">The movement keybindings; the defaults without them.</param>
    /// <param name="block">The match's input block: while it is set, the camera reads a frame with nothing pressed; none in tests.</param>
    /// <param name="origin">The match's origin, which the camera's pose is local to; the map's middle in tests.</param>
    public DebugFlyCameraSystem(
        World world,
        IFrameInput input,
        IOptions<EngineOptions> options,
        PlayArea area,
        Vector3 start,
        float startYaw,
        ILogger<DebugFlyCameraSystem> logger,
        float startPitch = 0,
        ConsoleCommands? commands = null,
        PlayerSettings? settings = null,
        MovementKeys? keys = null,
        PlayerInputBlock? block = null,
        WorldOrigin? origin = null
    )
        : base(world)
    {
        _block = block;
        _origin = origin ?? new WorldOrigin();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(area);
        _keys = keys ?? MovementKeys.Defaults();
        _commands = commands;
        _settings = settings;
        _input = input;
        _area = area;
        _logger = logger;
        _start = start;
        _startYaw = startYaw;
        _startPitch = startPitch;
        var engine = options.Value;
        _walkSpeed = engine.Voxels.DebugWalkSpeed;
        _circleHeight = (float)engine.Camera.CircleHeightMetres;
        _radiansPerPixel = CameraRules.RadiansPerPixel(engine);
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<CameraPose, FlyCamera>(in s_query);
        EcsChecks.RequireAll<PlayerMode>(in s_player);
        EcsChecks.RequireAll<FlyCamera>(in s_speeds);
        _commands?.Register(
            "flyspeed",
            "[metres per second]",
            "The fly camera's speed, 0.25 to 500 m/s (the wheel changes it too).",
            CommandScope.Local,
            FlySpeed
        );
        if (_walkSpeed > 0)
        {
            LogAutomaticPath(_walkSpeed);
        }
        else
        {
            var yawDegrees = _startYaw * 180 / MathF.PI;
            LogStart(_start.X, _start.Y, _start.Z, yawDegrees);
        }
    }

    public override void Update(in FrameTime frame)
    {
        if (_walkSpeed <= 0)
        {
            var player = new FindMode();
            World.InlineQuery<FindMode, PlayerMode>(in s_player, ref player);
            if (player.Found && !player.Flying)
            {
                return;
            }
        }

        // While the input is blocked, the camera reads a frame with nothing pressed, as the player's input does.
        ref readonly var input = ref (_block is { Blocked: true } ? ref s_nothing : ref _input.Frame);
        var job = _walkSpeed > 0 ? FollowCircle(frame.TotalSeconds) : Steer(in input, frame.DeltaSeconds);
        World.InlineQuery<Fly, CameraPose, FlyCamera>(in s_query, ref job);
    }

    /// <summary>
    /// The pose on the automatic flight path after <paramref name="seconds"/>: a circle round the map's middle,
    /// <paramref name="heightMetres"/> up, local to <paramref name="origin"/>.
    /// </summary>
    public static CameraPose CirclePose(WorldOrigin origin, double speed, double seconds, float heightMetres)
    {
        ArgumentNullException.ThrowIfNull(origin);
        // Sin and Cos are fine here: this is a debug aid, not generation, so peers need not agree on it.
        var angle = speed * seconds / CircleRadiusMetres;
        return new CameraPose
        {
            Position = origin.ToLocal(
                new WorldPoint(CircleRadiusMetres * Math.Cos(angle), heightMetres, CircleRadiusMetres * Math.Sin(angle))
            ),
            // Forward at yaw y is (-sin y, 0, -cos y); the path's direction is (-sin a, 0, cos a), so y = pi - a.
            Yaw = (float)Math.IEEERemainder(Math.PI - angle, 2 * Math.PI),
            Pitch = CirclePitchRadians,
        };
    }

    private Fly FollowCircle(double seconds) =>
        new() { FollowPath = true, Path = CirclePose(_origin, _walkSpeed, seconds, _circleHeight) };

    private Fly Steer(in InputSnapshot input, float deltaSeconds)
    {
        var job = new Fly
        {
            Area = _area,
            Origin = _origin,
            DeltaSeconds = deltaSeconds,
            SpeedScale = input.Scroll == 0 ? 1 : MathF.Pow(WheelFactor, input.Scroll),
            Boost = _keys.IsDown(in input, _keys.Sprint) ? FastFactor : 1,
            Move = new Vector3(
                Axis(in input, _keys.Right, _keys.Left),
                Axis(in input, _keys.Jump, _keys.MoveDown),
                Axis(in input, _keys.Forward, _keys.Back)
            ),
        };

        if (input.PointerCaptured)
        {
            // Mouse right turns right (yaw goes down); mouse down (screen +Y) looks down.
            var radiansPerPixel = _settings?.RadiansPerPixel ?? _radiansPerPixel;
            job.YawDelta = -input.MouseDelta.X * radiansPerPixel;
            job.PitchDelta = -input.MouseDelta.Y * radiansPerPixel;
        }

        return job;
    }

    private float Axis(in InputSnapshot input, KeyAction positive, KeyAction negative) =>
        (_keys.IsDown(in input, positive) ? 1 : 0) - (_keys.IsDown(in input, negative) ? 1 : 0);

    private void FlySpeed(CommandContext context)
    {
        var speed = new SetSpeed { Speed = -1 };
        if (context.Count > 0)
        {
            if (!context.TryGetFloat(0, out var wanted) || wanted < MinSpeed || wanted > MaxSpeed)
            {
                context.PrintUsage();
                return;
            }

            speed.Speed = wanted;
        }

        World.InlineQuery<SetSpeed, FlyCamera>(in s_speeds, ref speed);
        context.Print($"flyspeed {CommandContext.Number(speed.Now)} m/s");
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Debug camera at ({X:F1}, {Y:F1}, {Z:F1}) m, yaw {YawDegrees:F0} degrees; click to look (Escape: the menu), W A S D to fly"
    )]
    private partial void LogStart(float x, float y, float z, float yawDegrees);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Debug camera flies the 100 m circle at {Speed} m/s (Engine:Voxels:DebugWalkSpeed); input is ignored"
    )]
    private partial void LogAutomaticPath(double speed);

    /// <summary>Sets every fly camera's speed when <see cref="Speed"/> is not negative, and reads it.</summary>
    private struct SetSpeed : IForEach<FlyCamera>
    {
        public float Speed;
        public float Now;

        public void Update(ref FlyCamera fly)
        {
            if (Speed >= 0)
            {
                fly.SpeedMetresPerSecond = Speed;
            }

            Now = fly.SpeedMetresPerSecond;
        }
    }

    private struct FindMode : IForEach<PlayerMode>
    {
        public bool Found;
        public bool Flying;

        public void Update(ref PlayerMode mode)
        {
            Found = true;
            Flying = mode.Flying;
        }
    }

    private struct Fly : IForEach<CameraPose, FlyCamera>
    {
        public PlayArea? Area;
        public WorldOrigin? Origin;
        public bool FollowPath;
        public CameraPose Path;
        public float DeltaSeconds;
        public float YawDelta;
        public float PitchDelta;
        public float SpeedScale;
        public float Boost;

        /// <summary>X right, Y world up, Z forward; each -1, 0, or 1.</summary>
        public Vector3 Move;

        public readonly void Update(ref CameraPose pose, ref FlyCamera fly)
        {
            if (FollowPath)
            {
                pose = Path;
                return;
            }

            fly.SpeedMetresPerSecond = Math.Clamp(fly.SpeedMetresPerSecond * SpeedScale, MinSpeed, MaxSpeed);
            pose.Yaw = MathF.IEEERemainder(pose.Yaw + YawDelta, 2 * MathF.PI);
            pose.Pitch = Math.Clamp(pose.Pitch + PitchDelta, -CameraRules.MaxPitch, CameraRules.MaxPitch);
            if (Move == Vector3.Zero)
            {
                return;
            }

            var orientation = Quaternion.CreateFromYawPitchRoll(pose.Yaw, pose.Pitch, 0);
            var direction =
                Vector3.Transform(Vector3.UnitX, orientation) * Move.X
                + Vector3.UnitY * Move.Y
                + Vector3.Transform(-Vector3.UnitZ, orientation) * Move.Z;
            if (direction != Vector3.Zero)
            {
                // It keeps to the play area: a camera flown far out makes column walks and edits very large.
                var moved =
                    pose.Position + Vector3.Normalize(direction) * (fly.SpeedMetresPerSecond * Boost * DeltaSeconds);
                pose.Position = Origin!.ToLocal(Area!.ClampEye(Origin.ToWorld(moved)));
            }
        }
    }
}
