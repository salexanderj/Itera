using System.Numerics;
using Itera.Engine.Physics;

namespace Itera.Engine.Components;

/// <summary>Marks the player this machine's input controls.</summary>
public struct LocalPlayer;

/// <summary>
/// Marks a player of another game in the session. On a host it has the movement components and is moved from the
/// commands that game sends, by the rules the local player moves by (<c>PlayerMovementSystem</c>), and the host streams
/// the world around it (<c>StreamingCentreSystem</c>); on a client it is drawn from the states the host sends, and
/// nothing moves it.
/// </summary>
public struct RemotePlayer;

/// <summary>
/// Marks a character that has no body for now (for example a dead player waiting to come back, or one watching): it
/// does not move, push loose bodies, or take a stand-in, and its command holds only its look.
/// </summary>
public struct Bodiless;

/// <summary>
/// Marks a character the trigger zones see (<c>TriggerCharacterSystem</c>): as an upright capsule of its size at its
/// feet, whose comings and goings are in the match's <c>CharacterTriggers</c>.
/// </summary>
public struct TriggerSensor;

/// <summary>
/// The movement buttons a <see cref="PlayerCommand"/> carries. A game's own buttons go in
/// <see cref="PlayerCommand.Actions"/>. Compare bits directly; no enum helpers in hot code.
/// </summary>
[Flags]
public enum PlayerButtons
{
    None = 0,

    /// <summary>Jump when on walkable ground (once a press; holding it does not jump again).</summary>
    Jump = 1,

    /// <summary>Move at sprinting speed while held.</summary>
    Sprint = 2,

    /// <summary>Crouch while held: lower and slower, with no jump or sprint, until let go with room to stand.</summary>
    Crouch = 4,

    /// <summary>Every button there is; other bits in a command (from a peer) are dropped.</summary>
    AllKnown = Jump | Sprint | Crouch,
}

/// <summary>
/// What a player asks for in one fixed step: movement, where it looks, its movement buttons, and the game's own
/// actions. Built from tick input for the local player; a session's peers may send it to the host as it is.
/// </summary>
public struct PlayerCommand
{
    /// <summary>X to the right, Y forward, each from -1 to 1; at most length 1.</summary>
    public Vector2 Move;

    /// <summary>Turn about +Y in radians: 0 looks along -Z and a positive yaw turns left.</summary>
    public float Yaw;

    /// <summary>Tilt in radians: positive looks up.</summary>
    public float Pitch;

    public PlayerButtons Buttons;

    /// <summary>
    /// The game's own buttons, one bit each, which a game's input system sets after the engine's
    /// (<c>PlayerInputSystem</c>) and its own systems read. The engine never reads them.
    /// </summary>
    public uint Actions;
}

/// <summary>How the player moves now: walking, or flying the debug camera (the console's <c>fly</c>), and crouched.</summary>
public struct PlayerMode
{
    public bool Flying;

    /// <summary>
    /// Crouched: <c>PlayerMovementSystem</c> lowers the body and eye to <c>PlayerMovementSystem.CrouchHeightShare</c>
    /// of standing, and moves it slower with no jump or sprint.
    /// </summary>
    public bool Crouching;
}

/// <summary>
/// The local player's camera: at its eye, or while <see cref="On"/> behind its right shoulder, <see cref="Metres"/>
/// back (<c>ThirdPersonCamera</c>; its key and the console's <c>camera</c> switch it, the wheel moves it).
/// </summary>
public struct ThirdPersonView
{
    public bool On;

    /// <summary>How far behind the shoulder the camera is, in metres, before walls pull it in.</summary>
    public float Metres;

    /// <summary>
    /// Which shoulder the camera is over: 0 the right (the default), 1 the left; a game eases it between them to swap
    /// (<c>ThirdPersonCamera</c>).
    /// </summary>
    public float Left;
}

/// <summary>An upright character body's size, in metres: height, radius seen from above, eye height, and step height.</summary>
public struct CharacterSize
{
    /// <summary>The body's radius as a share of its height: 26 cm at 1.7 m.</summary>
    public const float RadiusShare = 0.15f;

    /// <summary>The eye's height as a share of the body's height: 1.56 m at 1.7 m.</summary>
    public const float EyeShare = 0.92f;

    /// <summary>The highest ledge walked up onto without jumping, as a share of the body's height: 41 cm at 1.7 m.</summary>
    public const float StepShare = 0.24f;

    public float HeightMetres;
    public float RadiusMetres;
    public float EyeMetres;
    public float StepMetres;

    /// <summary>The mass its stand-in pushes loose bodies with, and is pushed by them with, in kilograms.</summary>
    public float MassKilograms;

    /// <summary>
    /// A body of <paramref name="heightMetres"/>: radius, eye, and step as the shares above, and 80 kg at 1.7 m, growing
    /// with the body's volume.
    /// </summary>
    public static CharacterSize ForHeight(float heightMetres) =>
        new()
        {
            HeightMetres = heightMetres,
            RadiusMetres = heightMetres * RadiusShare,
            EyeMetres = heightMetres * EyeShare,
            StepMetres = heightMetres * StepShare,
            MassKilograms = 80 * MathF.Pow(heightMetres / 1.7f, 3),
        };
}

/// <summary>
/// Where a character stands (the centre of its feet), in metres, now and after the previous fixed step, so frames can
/// draw it between the two.
/// </summary>
public struct CharacterPosition
{
    public Vector3 Feet;
    public Vector3 PreviousFeet;
}

/// <summary>A character's velocity, in metres per second.</summary>
public struct CharacterVelocity
{
    public Vector3 MetresPerSecond;

    /// <summary>
    /// The velocity the character tried to move at in the last step, before walls, floors, and bodies stopped it. Its
    /// stand-in moves towards this, so walking into a loose body pushes it.
    /// </summary>
    public Vector3 Wanted;

    /// <summary>
    /// The push loose bodies gave the character's stand-in in the last physics step, in metres per second: added to the
    /// velocity after steering in the next step, so even a push the feet's grip takes away moves the character once.
    /// </summary>
    public Vector3 Push;
}

/// <summary>What a character touched in the last fixed step.</summary>
public struct CharacterContact
{
    /// <summary>Standing on something.</summary>
    public bool Grounded;

    /// <summary>
    /// Standing only on a thin rim: on a slope steeper than the limit, or with the centre hanging past an edge. The
    /// character slides towards <see cref="Downhill"/> and cannot jump.
    /// </summary>
    public bool Steep;

    /// <summary>On steep ground, the horizontal direction (x, z) the character slides.</summary>
    public Vector2 Downhill;

    /// <summary>
    /// When the last step's move met a wall, its normal across x and z (a unit length, out of the wall towards the
    /// character); zero when it met none.
    /// </summary>
    public Vector2 WallNormal;

    /// <summary>Holding still because voxels around it are not loaded yet.</summary>
    public bool Waiting;

    /// <summary>A jump fired in the last step's move.</summary>
    public bool Jumped;

    /// <summary>
    /// Grounded, the physics material of what it stands on (a voxel's by its voxel material, a mesh collider's by its
    /// settings); the default on a loose body or in the air. The engine's walking grips by its friction, at most 1.
    /// </summary>
    public PhysicsMaterialId GroundMaterial;

    /// <summary>Grounded, the surface tags of what it stands on (the game's bits); 0 on a loose body or in the air.</summary>
    public uint GroundTags;
}

/// <summary>
/// How far the eye lies below (negative) or above where the body puts it, in metres, now and after the previous fixed
/// step: after a step up or down (<see cref="Offset"/>, which shrinks by a share each step) and after a change of the
/// body's eye height, such as a crouch (<see cref="Height"/>, which moves at a steady pace). So the eye eases over steps
/// instead of jumping. Presentation only; gameplay never reads it.
/// </summary>
public struct EyeSmoothing
{
    public float Offset;
    public float PreviousOffset;

    /// <summary>
    /// How far the eye lies from where a new eye height puts it, in metres (<c>PlayerMovementSystem</c>: it starts where
    /// it was and moves to its new height at <c>PlayerMovementSystem.HeightEyeMetresPerSecond</c>).
    /// </summary>
    public float Height;

    public float PreviousHeight;

    /// <summary>The eye's height the last step moved with, in metres, to see it change; 0 before the first.</summary>
    public float EyeMetres;

    /// <summary>
    /// On a client, the body's offset after a correction of its prediction (the host's state put it elsewhere): what it
    /// is drawn off by, easing to none (<c>ReplicationService.CorrectionEase</c>); a direction, not a position. With
    /// <see cref="PreviousCorrection"/>, its value at the step before.
    /// </summary>
    public Vector3 Correction;

    public Vector3 PreviousCorrection;
}

/// <summary>
/// What a character moves by: speeds in metres per second, the jump's height in metres, accelerations in metres per
/// second squared, and the steepest slope it walks in degrees. A game may write them from its own rules each step.
/// </summary>
public struct MovementStats
{
    public float Walk;
    public float Sprint;
    public float JumpHeight;
    public float Gravity;
    public float GroundAcceleration;
    public float AirAcceleration;
    public float MaxFall;
    public float MaxSlopeDegrees;

    /// <summary>
    /// A person's: walks at 4.5 m/s, sprints at 7, jumps 1.1 m, falls under 20 m/s² up to 50 m/s, turns its speed by 50
    /// m/s² on the ground and 10 in the air, and walks up slopes of up to 45 degrees.
    /// </summary>
    public static MovementStats Default =>
        new()
        {
            Walk = 4.5f,
            Sprint = 7,
            JumpHeight = 1.1f,
            Gravity = 20,
            GroundAcceleration = 50,
            AirAcceleration = 10,
            MaxFall = 50,
            MaxSlopeDegrees = 45,
        };
}

/// <summary>Where a player is in a climb (<c>ClimbingMode</c>).</summary>
public enum ClimbState : byte
{
    /// <summary>Not climbing: the engine walks it.</summary>
    Free = 0,

    /// <summary>On a wall.</summary>
    Climbing = 1,

    /// <summary>Rising past a wall's top to stand on it.</summary>
    PullingUp = 2,

    /// <summary>Moving over a wall's top edge onto it.</summary>
    PullingOver = 3,
}

/// <summary>
/// A player's climb (<c>ClimbingMode</c>): its state, the wall's normal across x and z (eased from step to step), the
/// feet's place on the ledge while pulling up, how long the pull has gone on, how long before it may grab again, and
/// whether Jump was held at the last step (a grab or a jump off needs a fresh press). A game gives one to each player
/// that climbs.
/// </summary>
public struct Climber
{
    public ClimbState State;
    public Vector2 Normal;
    public Vector3 Target;
    public float Seconds;
    public float Regrab;
    public bool JumpHeld;
}
