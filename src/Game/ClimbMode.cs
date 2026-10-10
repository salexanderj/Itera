using System.Numerics;
using Game.Components;
using Itera.Engine.Components;
using Itera.Engine.Physics;
using Itera.Engine.Systems;

namespace Game;

/// <summary>
/// The starter's climbing (its <see cref="IMovementMode"/>, offered each walking player's step by the engine's
/// <see cref="PlayerMovementSystem"/>). Space, pressed with a climbable wall (<see cref="GameSurfaceTags.Climbable"/>:
/// the stone boulders) within <see cref="GrabReachMetres"/> ahead of where the player looks, grabs it instead of jumping.
/// On the wall, W and S climb up and down at <see cref="ClimbSpeed"/>, A and D move along it at <see cref="SideSpeed"/>,
/// nothing held hangs; Space jumps off it, away and up (<see cref="WallJumpOut"/>, <see cref="WallJumpUp"/>), and Ctrl
/// lets go, either for <see cref="RegrabSeconds"/> before the next grab. Climbing up past the wall's top pulls the player
/// up onto it where it has room to stand: up past the top, then over the edge. A wall that ends beside or below lets go.
/// The probes are the engine's (<see cref="MovementProbes"/>); the state per player is its <see cref="Climber"/>. Main
/// thread; allocates nothing.
/// </summary>
public sealed class ClimbMode(MovementProbes probes) : IMovementMode
{
    /// <summary>How far past the body's side a wall may lie to be grabbed, in metres.</summary>
    public const float GrabReachMetres = 0.3f;

    /// <summary>How far past the body's side the wall may lie for the climb to keep to it, in metres.</summary>
    public const float HoldReachMetres = 0.45f;

    /// <summary>Climbing speed up and down, in metres per second.</summary>
    public const float ClimbSpeed = 1.5f;

    /// <summary>Speed along the wall, in metres per second.</summary>
    public const float SideSpeed = 2;

    /// <summary>How fast the climber presses into the wall, which keeps it there, in metres per second.</summary>
    public const float HoldSpeed = 0.5f;

    /// <summary>A jump off the wall: its speed away from the wall, in metres per second.</summary>
    public const float WallJumpOut = 4;

    /// <summary>A jump off the wall: its speed up, in metres per second.</summary>
    public const float WallJumpUp = 5;

    /// <summary>How fast a pull-up rises, and then moves over the edge, in metres per second.</summary>
    public const float PullUpSpeed = 2.5f;

    public const float PullOverSpeed = 1.5f;

    /// <summary>How long after a jump off or a letting go before a grab, in seconds.</summary>
    public const float RegrabSeconds = 0.3f;

    // The ledge's ray starts this far over the head, this far past the wall's face.
    private const float LedgeAboveMetres = 0.3f;
    private const float LedgeInsetMetres = 0.15f;

    // The share of the step's wall normal the eased normal takes: the faces of voxels change as the climber moves.
    private const float NormalEase = 0.3f;

    // A pull that has not finished by then lets go.
    private const float MostPullSeconds = 1.5f;

    public bool Steer(in MovementModeInput input, ref MovementModeMove move)
    {
        if (!input.World.Has<Climber>(input.Entity))
        {
            return false;
        }

        ref var climber = ref input.World.Get<Climber>(input.Entity);
        var jump = (input.Command.Buttons & PlayerButtons.Jump) != 0;
        var pressed = jump && !climber.JumpHeld;
        climber.JumpHeld = jump;
        climber.Regrab = Math.Max(0, climber.Regrab - input.Seconds);
        var size = input.Size;
        var shape = new CharacterShape(
            size.HeightMetres,
            size.RadiusMetres,
            size.StepMetres,
            input.Stats.MaxSlopeDegrees
        );
        return climber.State switch
        {
            ClimbState.Climbing => Climb(in input, ref climber, in shape, pressed, ref move),
            ClimbState.PullingUp => PullUp(in input, ref climber, ref move),
            ClimbState.PullingOver => PullOver(in input, ref climber, ref move),
            _ => TryGrab(in input, ref climber, in shape, pressed, ref move),
        };
    }

    /// <summary>A fresh Space with a climbable wall within reach ahead grabs it: the step holds there.</summary>
    private bool TryGrab(
        in MovementModeInput input,
        ref Climber climber,
        in CharacterShape shape,
        bool pressed,
        ref MovementModeMove move
    )
    {
        if (
            !pressed
            || climber.Regrab > 0
            || !probes.TryWallAhead(input.Feet, in shape, Forward(input.Command.Yaw), GrabReachMetres, out var wall)
            || (wall.Tags & GameSurfaceTags.Climbable) == 0
        )
        {
            return false;
        }

        climber.State = ClimbState.Climbing;
        climber.Normal = wall.Normal;
        Hold(in climber, Vector2.Zero, ref move);
        return true;
    }

    private bool Climb(
        in MovementModeInput input,
        ref Climber climber,
        in CharacterShape shape,
        bool pressed,
        ref MovementModeMove move
    )
    {
        if ((input.Command.Buttons & PlayerButtons.Crouch) != 0)
        {
            LetGo(ref climber);
            return false;
        }

        var normal = climber.Normal;
        if (pressed)
        {
            LetGo(ref climber);
            move.Velocity = new Vector3(normal.X * WallJumpOut, WallJumpUp, normal.Y * WallJumpOut);
            move.StepMetres = 0;
            return true;
        }

        if (
            !probes.TryWallAhead(input.Feet, in shape, -normal, HoldReachMetres, out var wall)
            || (wall.Tags & GameSurfaceTags.Climbable) == 0
        )
        {
            LetGo(ref climber);
            return false;
        }

        climber.Normal = Vector2.Normalize(Vector2.Lerp(normal, wall.Normal, NormalEase));
        // Climbing past the top: the upper rays miss the wall. Up onto it where its top is within the body's height and
        // the body has room to stand there.
        if (
            input.Command.Move.Y > 0
            && wall.Rays < 3
            && probes.TryLedgeTop(input.Feet, in shape, in wall, LedgeAboveMetres, LedgeInsetMetres, out var top)
            && top.Y - input.Feet.Y <= shape.HeightMetres
        )
        {
            var target = new Vector3(
                wall.Point.X - climber.Normal.X * (shape.RadiusMetres + 0.05f),
                top.Y,
                wall.Point.Z - climber.Normal.Y * (shape.RadiusMetres + 0.05f)
            );
            if (probes.HasRoom(target + new Vector3(0, 0.01f, 0), in shape))
            {
                climber.State = ClimbState.PullingUp;
                climber.Target = target;
                climber.Seconds = 0;
                return PullUp(in input, ref climber, ref move);
            }
        }

        Hold(in climber, input.Command.Move, ref move);
        return true;
    }

    /// <summary>Rises past the wall's top, then moves over the edge.</summary>
    private static bool PullUp(in MovementModeInput input, ref Climber climber, ref MovementModeMove move)
    {
        climber.Seconds += input.Seconds;
        if (input.Feet.Y >= climber.Target.Y + 0.02f)
        {
            climber.State = ClimbState.PullingOver;
            climber.Seconds = 0;
            return PullOver(in input, ref climber, ref move);
        }

        if (climber.Seconds > MostPullSeconds)
        {
            LetGo(ref climber);
            return false;
        }

        move.Velocity = new Vector3(0, PullUpSpeed, 0);
        move.StepMetres = 0;
        return true;
    }

    private static bool PullOver(in MovementModeInput input, ref Climber climber, ref MovementModeMove move)
    {
        climber.Seconds += input.Seconds;
        var away = new Vector2(climber.Target.X - input.Feet.X, climber.Target.Z - input.Feet.Z);
        var length = away.Length();
        if (length < 0.05f || climber.Seconds > MostPullSeconds)
        {
            climber.State = ClimbState.Free;
            return false;
        }

        var way = away / length * MathF.Min(PullOverSpeed, length / input.Seconds);
        move.Velocity = new Vector3(way.X, 0, way.Y);
        move.StepMetres = input.Size.StepMetres;
        return true;
    }

    /// <summary>
    /// On the wall: up and down by the command's forward, along it by its side (the command's way, not its length: the
    /// engine holds a move back to 0.7 of full), and into it to keep to it.
    /// </summary>
    private static void Hold(in Climber climber, Vector2 command, ref MovementModeMove move)
    {
        var length = command.Length();
        command = length > 0.01f ? command / length : Vector2.Zero;
        var normal = climber.Normal;
        // Facing the wall (along -normal), right is (normal.y, -normal.x) across x and z.
        var right = new Vector3(normal.Y, 0, -normal.X);
        move.Velocity =
            new Vector3(0, command.Y * ClimbSpeed, 0)
            + right * (command.X * SideSpeed)
            - new Vector3(normal.X, 0, normal.Y) * HoldSpeed;
        move.StepMetres = 0;
    }

    private static void LetGo(ref Climber climber)
    {
        climber.State = ClimbState.Free;
        climber.Regrab = RegrabSeconds;
    }

    /// <summary>Where a yaw looks across x and z: (-sin, -cos), as the engine's movement.</summary>
    private static Vector2 Forward(float yaw) => new(-MathF.Sin(yaw), -MathF.Cos(yaw));
}
