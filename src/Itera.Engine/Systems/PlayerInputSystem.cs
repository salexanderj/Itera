using System.Globalization;
using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Input;
using Itera.Engine.Settings;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Systems;

/// <summary>
/// Builds the local player's <see cref="PlayerCommand"/> from the fixed step's input (fixed group, before every system
/// that reads the command), by the movement keybindings (<see cref="MovementKeys"/>; the defaults in brackets): Forward,
/// Back, Left, and Right to move (W, S, A, D), Sprint (Shift) and Crouch (Ctrl) while held, Jump (Space, once a press),
/// and while the pointer is captured the mouse's movement since the last step added to the look. A <see cref="Bodiless"/>
/// player only looks about: its move and buttons are cleared. <see cref="PlayerCommand.Actions"/> is a game's: this
/// never writes it. The console's <c>fly</c> (left out when a game names its own, <c>flyCommand</c>) and a game's
/// <see cref="RequestFlying"/> switch between walking and the debug fly camera, unless flying is forced (the benchmark
/// and the automatic path); <c>Engine:Camera:StartFlying</c> starts the first local player flying. While flying, the
/// command holds no movement, jump, or sprint and takes its look and place from the <see cref="ActiveCamera"/>
/// (<see cref="PlayerButtons.Fly"/> and <see cref="PlayerCommand.Viewpoint"/>, which a client sends its host), so
/// walking resumes looking where the camera looked; reading a pose a frame system wrote is a debug case. Third person (Tab) switches the local player's camera between its eye and
/// behind its right shoulder (<see cref="ThirdPersonView"/>; not while flying; a game may leave the key out,
/// <see cref="MovementKeys.ThirdPerson"/>), and the wheel moves the shoulder camera in and out
/// (<see cref="ThirdPersonCamera.Wheel"/>); the console's <c>camera</c> does what Third person does. While
/// <see cref="PlayerInputBlock.Blocked"/> the local player's input does nothing, as if no key were down. Reads
/// <see cref="ITickInput"/> and <see cref="CameraPose"/>; writes <see cref="PlayerCommand"/>, <see cref="PlayerMode"/>,
/// and <see cref="ThirdPersonView"/>; no structural changes.
/// </summary>
public sealed partial class PlayerInputSystem : BaseSystem<World, FixedStepTime>, IFixedUpdateSystem
{
    private static readonly QueryDescription s_players = new QueryDescription().WithAll<
        PlayerCommand,
        PlayerMode,
        LocalPlayer
    >();

    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    private static readonly QueryDescription s_bodiless = new QueryDescription().WithAll<
        PlayerCommand,
        Bodiless,
        LocalPlayer
    >();

    private static readonly QueryDescription s_views = new QueryDescription().WithAll<
        PlayerMode,
        ThirdPersonView,
        LocalPlayer
    >();

    /// <summary>The input of a step in which nothing is pressed or held: a blocked input's.</summary>
    private static readonly InputSnapshot s_nothing;

    private readonly ITickInput _input;
    private readonly float _radiansPerPixel;

    /// <summary>The player's settings, whose mouse sensitivity is read at each step; the options' without them (tests).</summary>
    private readonly PlayerSettings? _settings;
    private readonly MovementKeys _keys;
    private readonly bool _forceFlying;
    private readonly ILogger<PlayerInputSystem> _logger;
    private readonly ConsoleCommands? _commands;
    private readonly PlayerInputBlock? _block;
    private readonly bool _flyCommand;

    /// <summary>
    /// What the <c>fly</c> command or <see cref="RequestFlying"/> asked for: -1 nothing, 0 walk, 1 fly. Applied at the
    /// next step with a local player.
    /// </summary>
    private int _flyRequest;

    /// <summary>What the <c>camera</c> command asked for: -1 nothing, 0 first person, 1 third. Applied at the next step.</summary>
    private int _cameraRequest = -1;

    /// <param name="world">The ECS world.</param>
    /// <param name="input">The input of the fixed step being run.</param>
    /// <param name="options">Engine options, for the mouse sensitivity.</param>
    /// <param name="forceFlying">True when the debug camera must fly (the benchmark, the automatic path).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="commands">The console's commands, which gets <c>fly</c> and <c>camera</c>; none in tests.</param>
    /// <param name="block">Whether the player's input acts; none in tests that do not block it.</param>
    /// <param name="settings">The player's settings, whose mouse sensitivity is read each step; the options' without them.</param>
    /// <param name="keys">The movement keybindings; the defaults without them.</param>
    /// <param name="flyCommand">False when the game names its own command for flying, through <see cref="RequestFlying"/>.</param>
    public PlayerInputSystem(
        World world,
        ITickInput input,
        IOptions<EngineOptions> options,
        bool forceFlying,
        ILogger<PlayerInputSystem> logger,
        ConsoleCommands? commands = null,
        PlayerInputBlock? block = null,
        PlayerSettings? settings = null,
        MovementKeys? keys = null,
        bool flyCommand = true
    )
        : base(world)
    {
        ArgumentNullException.ThrowIfNull(options);
        _flyCommand = flyCommand;
        _flyRequest = options.Value.Camera.StartFlying ? 1 : -1;
        _keys = keys ?? MovementKeys.Defaults();
        _block = block;
        _settings = settings;
        _input = input;
        _radiansPerPixel = CameraRules.RadiansPerPixel(options.Value);
        _forceFlying = forceFlying;
        _logger = logger;
        _commands = commands;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<PlayerCommand, PlayerMode>(in s_players);
        EcsChecks.RequireAll<CameraPose>(in s_camera);
        EcsChecks.RequireAll<PlayerCommand, Bodiless>(in s_bodiless);
        EcsChecks.RequireAll<PlayerMode, ThirdPersonView, LocalPlayer>(in s_views);
        if (_flyCommand)
        {
            _commands?.Register(
                "fly",
                "[on|off]",
                "Flies the debug camera, or walks again where it is.",
                CommandScope.Local,
                Fly
            );
        }

        _commands?.Register(
            "camera",
            "[first|third]",
            "The camera at the player's eye, or behind its right shoulder (its key: see Keybindings; the wheel moves it).",
            CommandScope.Local,
            Camera
        );
    }

    public override void Update(in FixedStepTime step)
    {
        ref readonly var input = ref (_block is { Blocked: true } ? ref s_nothing : ref _input.Tick);
        var camera = new FindCamera();
        World.InlineQuery<FindCamera, CameraPose>(in s_camera, ref camera);

        var keys = _keys;
        var buttons = PlayerButtons.None;
        // One jump per press: holding Jump does not jump again on landing.
        if (keys.WasPressed(in input, keys.Jump))
        {
            buttons |= PlayerButtons.Jump;
        }

        if (keys.IsDown(in input, keys.Sprint))
        {
            buttons |= PlayerButtons.Sprint;
        }

        // Crouch whether or not the pointer is captured, as Sprint.
        if (keys.IsDown(in input, keys.Crouch))
        {
            buttons |= PlayerButtons.Crouch;
        }

        var move = new Vector2(Axis(in input, keys.Right, keys.Left), Axis(in input, keys.Forward, keys.Back));
        if (move.LengthSquared() > 1)
        {
            move = Vector2.Normalize(move);
        }

        var job = new Build
        {
            ForceFlying = _forceFlying,
            Request = _forceFlying ? -1 : _flyRequest,
            Move = move,
            Buttons = buttons,
            Look = input.PointerCaptured,
            // Mouse right turns right (yaw goes down); mouse down (screen +Y) looks down.
            YawDelta = -input.MouseDelta.X * (_settings?.RadiansPerPixel ?? _radiansPerPixel),
            PitchDelta = -input.MouseDelta.Y * (_settings?.RadiansPerPixel ?? _radiansPerPixel),
            CameraFound = camera.Found,
            Camera = camera.Pose,
        };
        World.InlineQuery<Build, PlayerCommand, PlayerMode>(in s_players, ref job);
        // A player with no body only looks about.
        World.Query(
            in s_bodiless,
            static (ref PlayerCommand command) =>
            {
                command.Move = Vector2.Zero;
                command.Buttons = PlayerButtons.None;
            }
        );
        // A request waits for a local player: the first one may come after the first step (a client's).
        if (job.Found)
        {
            _flyRequest = -1;
        }

        // Tab and the wheel, after the fly switch, so neither does anything while flying.
        var view = new View
        {
            Toggle = keys.ThirdPerson is { } thirdPerson && keys.WasPressed(in input, thirdPerson),
            Request = _cameraRequest,
            Scroll = input.Scroll,
        };
        World.InlineQuery<View, PlayerMode, ThirdPersonView>(in s_views, ref view);
        _cameraRequest = -1;
        if (view.Changed)
        {
            LogCamera(view.On ? "third person" : "first person", step.Tick);
        }

        if (job.Toggled)
        {
            var flying = job.FlyingNow;
            LogToggled(flying, step.Tick);
        }
    }

    /// <summary>
    /// Asks for the local player to fly the debug camera, or to walk again where it is, at the next step with a local
    /// player: a game's own command or key. False, and nothing asked, while flying is forced.
    /// </summary>
    public bool RequestFlying(bool on)
    {
        if (_forceFlying)
        {
            return false;
        }

        _flyRequest = on ? 1 : 0;
        return true;
    }

    private float Axis(in InputSnapshot input, KeyAction positive, KeyAction negative) =>
        (_keys.IsDown(in input, positive) ? 1 : 0) - (_keys.IsDown(in input, negative) ? 1 : 0);

    private void Fly(CommandContext context)
    {
        if (context.Count > 0)
        {
            if (!context.TryGetOnOff(0, out var on))
            {
                context.PrintUsage();
                return;
            }

            if (_forceFlying)
            {
                context.PrintError("fly: the benchmark or the automatic path flies the camera; it cannot walk.");
                return;
            }

            _flyRequest = on ? 1 : 0;
            context.Print("fly " + CommandContext.OnOff(on));
            return;
        }

        var find = new FindFlying();
        World.InlineQuery<FindFlying, PlayerMode>(in s_players, ref find);
        context.Print("fly " + CommandContext.OnOff(find.Flying));
    }

    private void Camera(CommandContext context)
    {
        if (context.Count > 0)
        {
            if (context.Is(0, "first") || context.Is(0, "third"))
            {
                _cameraRequest = context.Is(0, "third") ? 1 : 0;
                context.Print("camera " + (_cameraRequest == 1 ? "third" : "first"));
                return;
            }

            context.PrintUsage();
            return;
        }

        var find = new FindView();
        World.InlineQuery<FindView, PlayerMode, ThirdPersonView>(in s_views, ref find);
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"camera {(find.On ? "third" : "first")}, {find.Metres:0.##} m behind the shoulder"
            )
        );
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Player flying the debug camera: {Flying} (tick {Tick})")]
    private partial void LogToggled(bool flying, ulong tick);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Player: camera {View} (tick {Tick})")]
    private partial void LogCamera(string view, ulong tick);

    /// <summary>
    /// Tab or the <c>camera</c> command switches the camera (not while flying); the wheel moves the shoulder camera while
    /// it is on.
    /// </summary>
    private struct View : IForEach<PlayerMode, ThirdPersonView>
    {
        public bool Toggle;
        public int Request;
        public float Scroll;
        public bool Changed;
        public bool On;

        public void Update(ref PlayerMode mode, ref ThirdPersonView view)
        {
            var was = view.On;
            if (Request >= 0)
            {
                view.On = Request == 1;
            }
            else if (Toggle && !mode.Flying)
            {
                view.On = !view.On;
            }

            if (view.Metres <= 0)
            {
                view.Metres = ThirdPersonCamera.StartMetres;
            }

            if (view.On && !mode.Flying && Scroll != 0)
            {
                view.Metres = ThirdPersonCamera.Wheel(view.Metres, Scroll);
            }

            Changed |= view.On != was;
            On = view.On;
        }
    }

    private struct FindView : IForEach<PlayerMode, ThirdPersonView>
    {
        public bool On;
        public float Metres;

        public void Update(ref PlayerMode mode, ref ThirdPersonView view)
        {
            On = view.On;
            Metres = view.Metres;
        }
    }

    private struct FindFlying : IForEach<PlayerMode>
    {
        public bool Flying;

        public void Update(ref PlayerMode mode) => Flying = mode.Flying;
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

    private struct Build : IForEach<PlayerCommand, PlayerMode>
    {
        public bool ForceFlying;

        /// <summary>The fly command's request: -1 nothing, 0 walk, 1 fly.</summary>
        public int Request;
        public Vector2 Move;
        public PlayerButtons Buttons;
        public bool Look;
        public float YawDelta;
        public float PitchDelta;
        public bool CameraFound;
        public CameraPose Camera;
        public bool Toggled;
        public bool FlyingNow;
        public bool Found;

        public void Update(ref PlayerCommand command, ref PlayerMode mode)
        {
            Found = true;
            if (ForceFlying)
            {
                mode.Flying = true;
            }
            else if (Request >= 0 && mode.Flying != (Request == 1))
            {
                mode.Flying = !mode.Flying;
                Toggled = true;
                FlyingNow = mode.Flying;
            }

            if (mode.Flying)
            {
                command.Move = Vector2.Zero;
                command.Buttons = Buttons & ~(PlayerButtons.Jump | PlayerButtons.Sprint);
                if (CameraFound)
                {
                    command.Yaw = Camera.Yaw;
                    command.Pitch = Camera.Pitch;
                    command.Buttons |= PlayerButtons.Fly;
                    command.Viewpoint = Camera.Position;
                }

                return;
            }

            command.Move = Move;
            command.Buttons = Buttons;
            if (Look)
            {
                command.Yaw = MathF.IEEERemainder(command.Yaw + YawDelta, 2 * MathF.PI);
                command.Pitch = Math.Clamp(command.Pitch + PitchDelta, -CameraRules.MaxPitch, CameraRules.MaxPitch);
            }
        }
    }
}
