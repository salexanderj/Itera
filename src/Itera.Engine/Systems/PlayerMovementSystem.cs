using System.Globalization;
using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Modding;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Systems;

/// <summary>
/// Moves players through the voxels (fixed group, after the input), each by its <see cref="MovementStats"/>: turns the
/// horizontal velocity towards the one the command asks for (faster on walkable ground than in the air, where only keys
/// steer and no keys keep the speed), jumps from walkable ground, adds gravity, and hands the move to
/// <see cref="VoxelCharacterMover"/>, which slides the body along walls, steps it onto ledges, and lands it. On ground
/// steeper than its max slope the player slides down, steers only as in the air, and cannot jump, so no slope steeper
/// than the limit can be walked or jumped up. After a step up or down, or a crouch, the eye starts from its old height
/// and eases to the new one (<see cref="EyeSmoothing"/>). Before the moves, the local player crouches while its
/// command holds <see cref="PlayerButtons.Crouch"/> and it walks (<see cref="PlayerMode.Crouching"/>): its height and
/// eye drop to <see cref="CrouchHeightShare"/> of standing, it moves at <see cref="CrouchSpeedShare"/> of its speed, and
/// it cannot jump or sprint; let go, it stands up once the standing body fits where it is
/// (<see cref="VoxelCharacterMover.OverlapsAnything"/>), so it stays down under a low roof. Flying ends the crouch at
/// once. Moving backwards, the local player moves at most <see cref="BackwardSpeedShare"/> of its speed (of its crouched
/// speed while crouched) and cannot sprint. A game's <see cref="IMovementMode"/> may take a walking player's step before
/// the engine steers it (a climb): it gives the velocity and step height, and the move, contact, and eye go on as for any
/// step. A <see cref="Bodiless"/> entity is not moved. The velocity before the move
/// is kept as <see cref="CharacterVelocity.Wanted"/> for a stand-in, and the push loose bodies gave the stand-in
/// (<see cref="CharacterVelocity.Push"/>) is added after steering. While a player flies the debug camera, its feet
/// follow the camera, so walking resumes under it; reading a pose a frame system wrote is a debug case. It creates no
/// entity: a game makes its player with these components. Reads <see cref="PlayerCommand"/>, <see cref="PlayerMode"/>,
/// <see cref="CharacterSize"/>, <see cref="MovementStats"/>, and <see cref="CameraPose"/>; writes
/// <see cref="CharacterPosition"/>, <see cref="CharacterVelocity"/>, <see cref="CharacterContact"/>,
/// <see cref="EyeSmoothing"/>, and for the crouch the local player's <see cref="CharacterSize"/>, command, and
/// <see cref="PlayerMode.Crouching"/>. The console's <c>pos</c> prints where the camera is, and <c>tp</c> moves the
/// local player (or the fly camera) there at the next step.
/// </summary>
public sealed partial class PlayerMovementSystem : BaseSystem<World, FixedStepTime>, IFixedUpdateSystem
{
    /// <summary>The share of the eye's offset kept from one step to the next: most of a step is eased in 0.1 s.</summary>
    public const float EyeEase = 0.75f;

    /// <summary>
    /// How fast steep ground pushes a player down it, as a share of gravity: stronger than steering in the air, so a
    /// player who pushes up a steep slope still slides down it.
    /// </summary>
    public const float SlideShare = 1f;

    /// <summary>How fast the eye moves to a new eye height (a crouch, a new size), in metres per second.</summary>
    public const float HeightEyeMetresPerSecond = 2;

    /// <summary>A crouched body's height and eye as a share of standing: 1.31 m for a 1.7 m player.</summary>
    public const float CrouchHeightShare = 0.77f;

    /// <summary>A crouched player's speed as a share of its speed standing.</summary>
    public const float CrouchSpeedShare = 0.4f;

    /// <summary>The most speed straight back as a share of full speed; to the side it is full, between by how far back.</summary>
    public const float BackwardSpeedShare = 0.7f;

    /// <summary>A command's move back further than this (its y, ahead positive) is moving backwards.</summary>
    public const float BackwardFrom = 0.01f;

    /// <summary>The least speed over the ground, in metres per second, at which a sprint counts (<see cref="Sprinted"/>).</summary>
    public const float MinSprintSpeed = 1;

    // A Bodiless entity is not moved: it stays where it is.
    private static readonly QueryDescription s_players = new QueryDescription()
        .WithAll<
            PlayerCommand,
            PlayerMode,
            CharacterSize,
            CharacterPosition,
            CharacterVelocity,
            CharacterContact,
            EyeSmoothing,
            MovementStats
        >()
        .WithNone<Bodiless>();

    // The local player's command, limited by its crouch and moving backwards.
    private static readonly QueryDescription s_limited = new QueryDescription().WithAll<
        LocalPlayer,
        PlayerCommand,
        PlayerMode
    >();

    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    // Only the local player crouches. A bodiless one too: with its buttons cleared (PlayerInputSystem), it stands up.
    private static readonly QueryDescription s_crouchers = new QueryDescription().WithAll<
        LocalPlayer,
        PlayerCommand,
        PlayerMode,
        CharacterSize,
        CharacterPosition
    >();

    private static readonly QueryDescription s_localPlayer = new QueryDescription().WithAll<
        CharacterContact,
        CharacterPosition,
        LocalPlayer
    >();

    private static readonly QueryDescription s_local = new QueryDescription().WithAll<
        LocalPlayer,
        PlayerCommand,
        CharacterSize,
        CharacterPosition,
        CharacterVelocity,
        CharacterContact,
        EyeSmoothing
    >();

    /// <summary>
    /// The cameras a jump moves: the fly camera, and the active one (which <c>OriginShiftSystem</c> follows, so a step
    /// before the next frame does not find it where it was).
    /// </summary>
    private static readonly QueryDescription s_jumpCameras = new QueryDescription()
        .WithAll<CameraPose>()
        .WithAny<FlyCamera, ActiveCamera>();

    private readonly VoxelCharacterMover _mover;
    private readonly PlayArea _area;
    private readonly ILogger<PlayerMovementSystem> _logger;
    private readonly ConsoleCommands? _commands;
    private readonly Func<string, (WorldPoint Eye, float Yaw, float Pitch)?>? _places;
    private readonly IMovementMode? _mode;

    /// <summary>The match's physics, whose gravity's share of the Earth's characters fall by; none for all of their own.</summary>
    private readonly PhysicsWorld? _physics;
    private readonly OriginShifts? _shifts;
    private bool _localWaiting;
    private Request _request;

    /// <param name="world">The ECS world.</param>
    /// <param name="mover">Moves bodies through the voxels.</param>
    /// <param name="area">The box <c>tp</c> keeps the eye in.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="commands">The console's commands, which gets <c>pos</c> and <c>tp</c>; none in tests.</param>
    /// <param name="places">
    /// The eye, yaw, and pitch (radians) of a named place for <c>tp</c>, or null; the callback returns null for a name it
    /// lacks.
    /// </param>
    /// <param name="mode">A game's own way of moving, offered each walking player's step first; none for the engine's alone.</param>
    /// <param name="shifts">
    /// The match's origin moves: <c>tp</c> to an eye far from the origin (as far as <c>Engine:Origin:ShiftMetres</c>)
    /// asks <c>OriginShiftSystem</c> to move the origin there at the next step's start, before the jump, so a jump of any
    /// distance lands exactly; none in a test that leaves the origin.
    /// </param>
    /// <param name="physics">
    /// The match's physics: characters fall by their own gravity times its gravity's share of the Earth's
    /// (<see cref="PhysicsWorld.GravityShare"/>); none for their own gravity alone.
    /// </param>
    public PlayerMovementSystem(
        World world,
        VoxelCharacterMover mover,
        PlayArea area,
        ILogger<PlayerMovementSystem> logger,
        ConsoleCommands? commands = null,
        Func<string, (WorldPoint Eye, float Yaw, float Pitch)?>? places = null,
        IMovementMode? mode = null,
        OriginShifts? shifts = null,
        PhysicsWorld? physics = null
    )
        : base(world)
    {
        _mode = mode;
        _shifts = shifts;
        _physics = physics;
        ArgumentNullException.ThrowIfNull(mover);
        ArgumentNullException.ThrowIfNull(area);
        _commands = commands;
        _places = places;
        _mover = mover;
        _area = area;
        _logger = logger;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<PlayerCommand, PlayerMode, CharacterSize, CharacterPosition>(in s_players);
        EcsChecks.RequireAll<CharacterVelocity, CharacterContact, EyeSmoothing, MovementStats>(in s_players);
        EcsChecks.RequireAll<LocalPlayer, PlayerCommand, PlayerMode>(in s_limited);
        EcsChecks.RequireAll<CameraPose>(in s_camera);
        EcsChecks.RequireAll<CharacterContact, CharacterPosition>(in s_localPlayer);
        EcsChecks.RequireAll<LocalPlayer, PlayerCommand, PlayerMode, CharacterSize>(in s_crouchers);
        EcsChecks.RequireAll<CharacterPosition>(in s_crouchers);
        EcsChecks.RequireAll<LocalPlayer, PlayerCommand, CharacterSize, CharacterPosition>(in s_local);
        EcsChecks.RequireAll<CharacterVelocity, CharacterContact, EyeSmoothing>(in s_local);
        EcsChecks.RequireAll<CameraPose>(in s_jumpCameras);
        RegisterCommands();
    }

    public override void Update(in FixedStepTime step)
    {
        if (_request.Teleport)
        {
            ApplyRequest();
        }

        var crouch = new Crouch { Mover = _mover };
        World.InlineQuery<Crouch, PlayerCommand, PlayerMode, CharacterSize, CharacterPosition>(
            in s_crouchers,
            ref crouch
        );
        var limit = new LimitLocal();
        World.InlineQuery<LimitLocal, PlayerCommand, PlayerMode>(in s_limited, ref limit);

        var camera = new FindCamera();
        World.InlineQuery<FindCamera, CameraPose>(in s_camera, ref camera);
        var job = new Move
        {
            Mover = _mover,
            Mode = _mode,
            World = World,
            Seconds = step.StepSeconds,
            GravityShare = _physics?.GravityShare ?? 1,
            Physics = _physics,
            CameraFound = camera.Found,
            CameraPosition = camera.Position,
        };
        // A chunk loop, so a movement mode knows each player's entity.
        foreach (ref var chunk in World.Query(in s_players))
        {
            var commands = chunk.GetSpan<PlayerCommand>();
            var modes = chunk.GetSpan<PlayerMode>();
            var sizes = chunk.GetSpan<CharacterSize>();
            var positions = chunk.GetSpan<CharacterPosition>();
            var velocities = chunk.GetSpan<CharacterVelocity>();
            var contacts = chunk.GetSpan<CharacterContact>();
            var eyes = chunk.GetSpan<EyeSmoothing>();
            var stats = chunk.GetSpan<MovementStats>();
            foreach (var index in chunk)
            {
                job.Update(
                    chunk.Entity(index),
                    ref commands[index],
                    ref modes[index],
                    ref sizes[index],
                    ref positions[index],
                    ref velocities[index],
                    ref contacts[index],
                    ref eyes[index],
                    ref stats[index]
                );
            }
        }

        // Only the local player's waiting is logged: other bodies move through the same job.
        var local = new FindLocalWaiting();
        World.InlineQuery<FindLocalWaiting, CharacterContact, CharacterPosition>(in s_localPlayer, ref local);
        if (local.Found && local.Waiting != _localWaiting)
        {
            _localWaiting = local.Waiting;
            var feet = local.Feet;
            LogWaiting(local.Waiting, feet.X, feet.Y, feet.Z, step.Tick);
        }
    }

    /// <summary>
    /// True when a body's last move was a sprint: sprint still held after the command's limits, not flying, on the ground,
    /// and at least <see cref="MinSprintSpeed"/> over it. The mover's fact, which a game spends energy or trains by; a
    /// jump is <see cref="CharacterContact.Jumped"/>.
    /// </summary>
    public static bool Sprinted(
        in PlayerCommand command,
        in PlayerMode mode,
        in CharacterContact contact,
        in CharacterVelocity velocity
    )
    {
        var v = velocity.MetresPerSecond;
        return (command.Buttons & PlayerButtons.Sprint) != 0
            && !mode.Flying
            && contact.Grounded
            && v.X * v.X + v.Z * v.Z >= MinSprintSpeed * MinSprintSpeed;
    }

    /// <summary>
    /// Makes a command safe to act on, whatever wrote it (local input, or a peer): non-finite numbers become 0, the
    /// move is at most length 1, the pitch stays within <see cref="CameraRules.MaxPitch"/>, the yaw within one turn,
    /// and unknown button bits are dropped. Runs where commands enter movement, once per step. The game's
    /// <see cref="PlayerCommand.Actions"/> are left as they are.
    /// </summary>
    public static void Sanitize(ref PlayerCommand command)
    {
        if (!float.IsFinite(command.Move.X) || !float.IsFinite(command.Move.Y))
        {
            command.Move = Vector2.Zero;
        }
        else if (command.Move.LengthSquared() > 1)
        {
            command.Move = Vector2.Normalize(command.Move);
        }

        command.Yaw = float.IsFinite(command.Yaw) ? MathF.IEEERemainder(command.Yaw, 2 * MathF.PI) : 0;
        command.Pitch = float.IsFinite(command.Pitch)
            ? Math.Clamp(command.Pitch, -CameraRules.MaxPitch, CameraRules.MaxPitch)
            : 0;
        command.Buttons &= PlayerButtons.AllKnown;
    }

    /// <summary>
    /// The share of full speed a move may keep: 1 ahead and to the side, <see cref="BackwardSpeedShare"/> straight back,
    /// and between them by the share of the move that goes back.
    /// </summary>
    public static float BackwardShare(Vector2 move)
    {
        var length = move.Length();
        return length <= 0 || move.Y >= 0 ? 1 : 1 - (1 - BackwardSpeedShare) * (-move.Y / length);
    }

    /// <summary>Holds <paramref name="command"/>'s move to at most <paramref name="most"/> of full speed.</summary>
    private static void Hold(ref PlayerCommand command, float most)
    {
        var length = command.Move.Length();
        if (length > most)
        {
            command.Move *= MathF.Max(0, most) / length;
        }
    }

    /// <summary>
    /// The local player's crouch for the step (<see cref="PlayerButtons.Crouch"/>): down while it is held and the player
    /// walks, up once it is let go and the standing body fits, at once when it flies. Going down or up scales the height
    /// and eye (the step stays); the move then eases the eye to its new height (<see cref="EyeSmoothing.Height"/>). What a
    /// crouch allows the command is <see cref="LimitLocal"/>'s.
    /// </summary>
    private struct Crouch : IForEach<PlayerCommand, PlayerMode, CharacterSize, CharacterPosition>
    {
        public VoxelCharacterMover Mover;

        public readonly void Update(
            ref PlayerCommand command,
            ref PlayerMode mode,
            ref CharacterSize size,
            ref CharacterPosition position
        )
        {
            var walking = !mode.Flying;
            var down = walking && (command.Buttons & PlayerButtons.Crouch) != 0;
            if (mode.Crouching && !down && walking)
            {
                // Up only with room for the standing body where it is.
                var standing = new CharacterShape(
                    size.HeightMetres / CrouchHeightShare,
                    size.RadiusMetres,
                    size.StepMetres
                );
                down = Mover.OverlapsAnything(position.Feet, in standing);
            }

            if (down != mode.Crouching)
            {
                var share = down ? CrouchHeightShare : 1 / CrouchHeightShare;
                size.HeightMetres *= share;
                size.EyeMetres *= share;
                mode.Crouching = down;
            }
        }
    }

    /// <summary>The local player's command, limited by its crouch and by moving backwards.</summary>
    private struct LimitLocal : IForEach<PlayerCommand, PlayerMode>
    {
        public readonly void Update(ref PlayerCommand command, ref PlayerMode mode)
        {
            if (mode.Crouching)
            {
                Hold(ref command, CrouchSpeedShare);
                command.Buttons &= ~(PlayerButtons.Jump | PlayerButtons.Sprint);
            }

            if (!mode.Flying && command.Move.Y < -BackwardFrom)
            {
                Hold(ref command, (mode.Crouching ? CrouchSpeedShare : 1) * BackwardShare(command.Move));
                command.Buttons &= ~PlayerButtons.Sprint;
            }
        }
    }

    /// <summary>Moves the local player (and the fly camera) as the console asked.</summary>
    private void ApplyRequest()
    {
        // Local to the origin at the step, not at the command, in case it moved between them.
        var job = new Apply { Request = _request, Eye = _mover.Origin.ToLocal(_request.Eye) };
        World.InlineQuery<
            Apply,
            PlayerCommand,
            CharacterSize,
            CharacterPosition,
            CharacterVelocity,
            CharacterContact,
            EyeSmoothing
        >(in s_local, ref job);
        if (_request.Teleport)
        {
            World.InlineQuery<Apply, CameraPose>(in s_jumpCameras, ref job);
        }

        _request = default;
    }

    private void RegisterCommands()
    {
        if (_commands is null)
        {
            return;
        }

        _commands.Register(
            "pos",
            "",
            "Prints where the player's eye is and where it looks (the fly camera while it flies), as tp takes it (x,y,z,yaw,pitch).",
            CommandScope.Local,
            Pos
        );
        _commands.Register(
            "tp",
            _places is null ? "x y z [yaw pitch] | x,y,z,yaw,pitch" : "x y z [yaw pitch] | x,y,z,yaw,pitch | PLACE",
            "Moves the player there (metres and degrees), or the fly camera while it flies.",
            CommandScope.Match,
            Teleport
        );
    }

    /// <summary>
    /// The console's <c>pos</c>: a walking player's eye and look, which <c>tp</c> takes (in third person not the shoulder
    /// camera), or the fly camera while it flies.
    /// </summary>
    private void Pos(CommandContext context)
    {
        var player = ToolAim.FindPlayer(World);
        var camera = new FindCameraPose();
        World.InlineQuery<FindCameraPose, CameraPose>(in s_camera, ref camera);
        if (player.Found && !player.Flying)
        {
            camera.Pose = new CameraPose
            {
                Position = player.Eye,
                Yaw = player.Yaw,
                Pitch = player.Pitch,
            };
        }
        else if (!camera.Found)
        {
            context.PrintError("pos: there is no camera yet.");
            return;
        }

        // Absolute, whatever the match's origin, so tp and a start pose take it as it is.
        var pose = camera.Pose;
        context.Print(CameraPoseText.Format(_mover.Origin.ToWorld(pose.Position), pose.Yaw, pose.Pitch));
    }

    private void Teleport(CommandContext context)
    {
        WorldPoint eye;
        float yaw = 0,
            pitch = 0;
        var angles = false;
        if (context.Count == 1 && _places?.Invoke(context[0]) is { } place)
        {
            (eye, yaw, angles) = (place.Eye, place.Yaw, true);
            pitch = Math.Clamp(place.Pitch, -CameraRules.MaxPitch, CameraRules.MaxPitch);
        }
        else if (context.Count == 1 && CameraPoseText.TryParse(context[0], out eye, out yaw, out pitch))
        {
            angles = true;
        }
        else if (
            (context.Count == 3 || context.Count == 5)
            && context.TryGetDouble(0, out var x)
            && context.TryGetDouble(1, out var y)
            && context.TryGetDouble(2, out var z)
        )
        {
            eye = new WorldPoint(x, y, z);
            if (context.Count == 5)
            {
                if (!context.TryGetFloat(3, out var yawDegrees) || !context.TryGetFloat(4, out var pitchDegrees))
                {
                    context.PrintUsage();
                    return;
                }

                yaw = yawDegrees * MathF.PI / 180;
                pitch = Math.Clamp(pitchDegrees * MathF.PI / 180, -CameraRules.MaxPitch, CameraRules.MaxPitch);
                angles = true;
            }
        }
        else
        {
            context.PrintUsage();
            return;
        }

        if (!_area.HoldsEye(eye))
        {
            context.PrintError(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"tp: ({eye.X:F0}, {eye.Y:F0}, {eye.Z:F0}) is outside the play area: {_area.EyeBounds}."
                )
            );
            return;
        }

        // The origin moves to a far eye at the next step's start (OriginShiftSystem), before the jump, so the eye is local
        // and small.
        _shifts?.RequestFollow(eye);
        _request.Teleport = true;
        _request.Eye = eye;
        _request.Angles = angles;
        _request.Yaw = yaw;
        _request.Pitch = pitch;
        context.Print(string.Create(CultureInfo.InvariantCulture, $"tp {eye.X:F2} {eye.Y:F2} {eye.Z:F2}"));
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Player waiting for voxels to load: {Waiting}, feet at ({X:F2}, {Y:F2}, {Z:F2}) m (tick {Tick})"
    )]
    private partial void LogWaiting(bool waiting, float x, float y, float z, ulong tick);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>The local player's waiting state and feet, for the log.</summary>
    private struct FindLocalWaiting : IForEach<CharacterContact, CharacterPosition>
    {
        public bool Found;
        public bool Waiting;
        public Vector3 Feet;

        public void Update(ref CharacterContact contact, ref CharacterPosition position)
        {
            Found = true;
            Waiting = contact.Waiting;
            Feet = position.Feet;
        }
    }

    /// <summary>What the console asked movement to do at the next step.</summary>
    private struct Request
    {
        public bool Teleport;

        /// <summary>The eye, absolute.</summary>
        public WorldPoint Eye;
        public bool Angles;
        public float Yaw;
        public float Pitch;
    }

    /// <summary>Applies a <see cref="Request"/> to the local player, and a teleport to the fly camera.</summary>
    private struct Apply
        : IForEach<PlayerCommand, CharacterSize, CharacterPosition, CharacterVelocity, CharacterContact, EyeSmoothing>,
            IForEach<CameraPose>
    {
        public Request Request;

        /// <summary>The request's eye, local.</summary>
        public Vector3 Eye;

        public readonly void Update(
            ref PlayerCommand command,
            ref CharacterSize size,
            ref CharacterPosition position,
            ref CharacterVelocity velocity,
            ref CharacterContact contact,
            ref EyeSmoothing eye
        )
        {
            if (!Request.Teleport)
            {
                return;
            }

            // The mover waits where the voxels are not loaded yet, as at the start.
            position.Feet = Eye - new Vector3(0, size.EyeMetres, 0);
            position.PreviousFeet = position.Feet;
            velocity = default;
            contact = default;
            eye = default;
            if (Request.Angles)
            {
                command.Yaw = Request.Yaw;
                command.Pitch = Request.Pitch;
            }
        }

        public readonly void Update(ref CameraPose pose)
        {
            pose.Position = Eye;
            if (Request.Angles)
            {
                pose.Yaw = Request.Yaw;
                pose.Pitch = Request.Pitch;
            }
        }
    }

    private struct FindCameraPose : IForEach<CameraPose>
    {
        public CameraPose Pose;
        public bool Found;

        public void Update(ref CameraPose pose)
        {
            Pose = pose;
            Found = true;
        }
    }

    private struct FindCamera : IForEach<CameraPose>
    {
        public Vector3 Position;
        public bool Found;

        public void Update(ref CameraPose pose)
        {
            Position = pose.Position;
            Found = true;
        }
    }

    private struct Move
    {
        public VoxelCharacterMover Mover;
        public IMovementMode? Mode;
        public World World;
        public float Seconds;

        /// <summary>The match's gravity as a share of the Earth's: each character falls by its own gravity times it.</summary>
        public float GravityShare;

        /// <summary>The match's physics, for the friction of the ground a character stands on; none grips fully.</summary>
        public PhysicsWorld? Physics;

        public bool CameraFound;
        public Vector3 CameraPosition;

        public readonly void Update(
            Entity entity,
            ref PlayerCommand command,
            ref PlayerMode mode,
            ref CharacterSize size,
            ref CharacterPosition position,
            ref CharacterVelocity velocity,
            ref CharacterContact contact,
            ref EyeSmoothing eye,
            ref MovementStats stats
        )
        {
            Sanitize(ref command);
            if (!IsFinite(velocity.MetresPerSecond) || !IsFinite(velocity.Push) || !IsFinite(position.Feet))
            {
                // A bad number from a physics push or an earlier step would freeze the character for good (NaN never
                // clears) or throw in the mover; the character stops and stands where it last was.
                velocity.MetresPerSecond = IsFinite(velocity.MetresPerSecond) ? velocity.MetresPerSecond : default;
                velocity.Push = default;
                position.Feet = IsFinite(position.Feet) ? position.Feet : position.PreviousFeet;
            }

            position.PreviousFeet = position.Feet;
            eye.PreviousOffset = eye.Offset;
            eye.PreviousHeight = eye.Height;
            if (mode.Flying)
            {
                if (CameraFound)
                {
                    position.Feet = CameraPosition - new Vector3(0, size.EyeMetres, 0);
                    position.PreviousFeet = position.Feet;
                }

                velocity = default;
                contact = default;
                eye = default;
                return;
            }

            // A new eye height (a crouch, a new size) moved the eye: it starts where it was and moves to its new height.
            if (eye.EyeMetres > 0 && size.EyeMetres != eye.EyeMetres)
            {
                eye.Height += eye.EyeMetres - size.EyeMetres;
                eye.PreviousHeight = eye.Height;
            }

            eye.EyeMetres = size.EyeMetres;
            var most = HeightEyeMetresPerSecond * Seconds;
            eye.Height = MathF.Abs(eye.Height) <= most ? 0 : eye.Height - MathF.CopySign(most, eye.Height);

            var v = velocity.MetresPerSecond;
            var jumped = false;
            var step = size.StepMetres;
            var taken = new MovementModeMove { Velocity = v, StepMetres = step };
            if (
                Mode is not null
                && Mode.Steer(
                    new MovementModeInput(
                        entity,
                        World,
                        command,
                        size,
                        position.Feet,
                        v,
                        contact,
                        stats with
                        {
                            Gravity = stats.Gravity * GravityShare,
                        },
                        Seconds
                    ),
                    ref taken
                )
            )
            {
                // The game's mode took the step: its velocity, with the loose bodies' push, and its step height.
                v = taken.Velocity + velocity.Push;
                velocity.Push = default;
                step = float.IsFinite(taken.StepMetres) ? Math.Max(0, taken.StepMetres) : 0;
                if (!IsFinite(v))
                {
                    v = Vector3.Zero;
                }
            }
            else
            {
                v = Steer(in command, in contact, ref velocity, in stats, out jumped);
            }

            var shape = new CharacterShape(size.HeightMetres, size.RadiusMetres, step, stats.MaxSlopeDegrees);
            velocity.Wanted = v;
            var result = Mover.Move(ref position.Feet, ref v, in shape, Seconds);
            velocity.MetresPerSecond = v;

            var waiting = result.Status == CharacterMoveStatus.Waiting;
            contact.Grounded = result.Grounded;
            contact.Steep = result.Steep;
            contact.Downhill = result.Downhill;
            contact.WallNormal = result.WallNormal;
            contact.Waiting = waiting;
            contact.Jumped = jumped;
            contact.GroundMaterial = result.GroundMaterial;
            contact.GroundTags = result.GroundTags;

            // The eye starts where it was before a step and eases to where the body puts it.
            var offset = Math.Clamp(eye.Offset - result.SteppedMetres, -size.StepMetres, size.StepMetres) * EyeEase;
            eye.Offset = MathF.Abs(offset) < 0.001f ? 0 : offset;
        }

        /// <summary>
        /// The engine's walking: the horizontal velocity turned towards the command's, a slide down steep ground, the
        /// loose bodies' push, a jump from walkable ground, and gravity (the character's own times the match's share). On
        /// walkable ground it turns by the ground acceleration times the ground's friction, at most 1 (on ice of 0.05 a
        /// person takes 1.8 s to reach walking speed, and as long to stop). A jump leaves at the speed that reaches its
        /// height under the character's own gravity, so under less it rises higher (half gravity, twice the height and
        /// time). Returns the velocity to move by.
        /// </summary>
        private readonly Vector3 Steer(
            in PlayerCommand command,
            in CharacterContact contact,
            ref CharacterVelocity velocity,
            in MovementStats stats,
            out bool jumped
        )
        {
            // Forward at yaw y is (-sin y, 0, -cos y) and right is (cos y, 0, -sin y).
            var sin = MathF.Sin(command.Yaw);
            var cos = MathF.Cos(command.Yaw);
            var speed = (command.Buttons & PlayerButtons.Sprint) != 0 ? stats.Sprint : stats.Walk;
            var wanted =
                new Vector2(command.Move.X * cos - command.Move.Y * sin, -command.Move.X * sin - command.Move.Y * cos)
                * speed;

            var v = velocity.MetresPerSecond;
            var horizontal = new Vector2(v.X, v.Z);
            var change = wanted - horizontal;
            var walkable = contact.Grounded && !contact.Steep;
            // On walkable ground the feet grip and stop the player without keys; in the air and on steep ground only
            // keys steer, so a fall or a slide keeps its speed.
            if (walkable || wanted != Vector2.Zero)
            {
                var grip =
                    walkable && Physics is { } physics ? MathF.Min(physics.FrictionOf(contact.GroundMaterial), 1) : 1;
                var limit = (walkable ? stats.GroundAcceleration * grip : stats.AirAcceleration) * Seconds;
                var length = change.Length();
                horizontal = length <= limit ? wanted : horizontal + change * (limit / length);
            }

            var fall = stats.Gravity * GravityShare;
            if (contact.Steep)
            {
                horizontal += contact.Downhill * (fall * SlideShare * Seconds);
            }

            // A loose body's push, after steering, so it moves the player at least this step.
            horizontal += new Vector2(velocity.Push.X, velocity.Push.Z);
            v.Y += velocity.Push.Y;
            velocity.Push = default;

            var vy = v.Y;
            jumped = (command.Buttons & PlayerButtons.Jump) != 0 && walkable;
            if (jumped)
            {
                // The height grows with the square of the speed.
                // Gravity is taken off before each step's move, so half a step of it is added back to reach the height.
                vy = MathF.Sqrt(2 * stats.Gravity * stats.JumpHeight) + fall * Seconds / 2;
            }

            vy = MathF.Max(vy - fall * Seconds, -stats.MaxFall);
            return new Vector3(horizontal.X, vy, horizontal.Y);
        }
    }
}
