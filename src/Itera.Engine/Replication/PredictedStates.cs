using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Maths;
using Itera.Engine.Networking;

namespace Itera.Engine.Replication;

/// <summary>
/// A part of a predicted player's movement state that a game keeps in a component of its own (a wall run, a roll), so
/// that a client's prediction and the host's correction carry it as they carry the engine's (registered at the run's
/// start, <see cref="ReplicatedKinds.RegisterPredicted"/>). Its state is a fixed number of bytes: the client keeps it for
/// each step it predicts, the host sends its own in the owner's block, and when the two differ the client takes the
/// host's and runs its later commands again. Positions in it are absolute (through the origin it is given), so a move of
/// the origin changes nothing. Run-wide, so it keeps no match's state; main thread, outside any query; allocates nothing.
/// </summary>
public interface IPredictedState
{
    /// <summary>Its name, which the kinds' fingerprint carries: a client whose parts differ is refused.</summary>
    string Name { get; }

    /// <summary>The bytes its state takes, 1 or more; all of a run's parts take at most <see cref="ReplicatedKinds.MostPredictedBytes"/>.</summary>
    int Bytes { get; }

    /// <summary>
    /// Writes <paramref name="entity"/>'s state into all of <paramref name="state"/> (a player without the component it
    /// keeps writes a state that says so).
    /// </summary>
    void Capture(World world, Entity entity, WorldOrigin origin, Span<byte> state);

    /// <summary>Sets <paramref name="entity"/> to <paramref name="state"/>; a player without the component is left as it is.</summary>
    void Restore(World world, Entity entity, WorldOrigin origin, ReadOnlySpan<byte> state);

    /// <summary>True when the host's state and a predicted one differ enough to correct the client.</summary>
    bool Differs(ReadOnlySpan<byte> host, ReadOnlySpan<byte> predicted);

    /// <summary>
    /// True when <paramref name="state"/> is one a player can have: every number finite and within what any game moves
    /// by, every place within a message's reach. A host does not send a state that is not; a client refuses one.
    /// </summary>
    bool IsValid(ReadOnlySpan<byte> state);
}

/// <summary>The bytes of a predicted player's parts (<see cref="IPredictedState"/>), kept inline so a step's history allocates nothing.</summary>
[InlineArray(ReplicatedKinds.MostPredictedBytes)]
internal struct PredictedStateBytes
{
    private byte _element0;
}

/// <summary>
/// The engine's own part of a predicted player's state, the first of every run's: a <see cref="Climber"/>'s state
/// (<c>ClimbingMode</c>), its target absolute. A climb in another state corrects the client.
/// </summary>
internal sealed class ClimberPredictedState : IPredictedState
{
    // Whether the player climbs, its state, Jump held, the wall's normal, the target, the pull's seconds, and the regrab.
    private const int HasAt = 0;
    private const int StateAt = 1;
    private const int JumpAt = 2;
    private const int NormalAt = 3;
    private const int TargetAt = 11;
    private const int SecondsAt = 35;
    private const int RegrabAt = 39;

    public string Name => "engine.climber";

    public int Bytes => 43;

    public void Capture(World world, Entity entity, WorldOrigin origin, Span<byte> state)
    {
        state.Clear();
        if (!world.Has<Climber>(entity))
        {
            return;
        }

        var climber = world.Get<Climber>(entity);
        var target = origin.ToWorld(climber.Target);
        state[HasAt] = 1;
        state[StateAt] = (byte)climber.State;
        state[JumpAt] = climber.JumpHeld ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(state[NormalAt..], climber.Normal.X);
        BinaryPrimitives.WriteSingleLittleEndian(state[(NormalAt + 4)..], climber.Normal.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(state[TargetAt..], target.X);
        BinaryPrimitives.WriteDoubleLittleEndian(state[(TargetAt + 8)..], target.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(state[(TargetAt + 16)..], target.Z);
        BinaryPrimitives.WriteSingleLittleEndian(state[SecondsAt..], climber.Seconds);
        BinaryPrimitives.WriteSingleLittleEndian(state[RegrabAt..], climber.Regrab);
    }

    public void Restore(World world, Entity entity, WorldOrigin origin, ReadOnlySpan<byte> state)
    {
        if (state[HasAt] == 0 || !world.Has<Climber>(entity))
        {
            return;
        }

        world.Get<Climber>(entity) = new Climber
        {
            State = (ClimbState)state[StateAt],
            JumpHeld = state[JumpAt] != 0,
            Normal = new Vector2(
                BinaryPrimitives.ReadSingleLittleEndian(state[NormalAt..]),
                BinaryPrimitives.ReadSingleLittleEndian(state[(NormalAt + 4)..])
            ),
            Target = origin.ToLocal(TargetOf(state)),
            Seconds = BinaryPrimitives.ReadSingleLittleEndian(state[SecondsAt..]),
            Regrab = BinaryPrimitives.ReadSingleLittleEndian(state[RegrabAt..]),
        };
    }

    public bool Differs(ReadOnlySpan<byte> host, ReadOnlySpan<byte> predicted) =>
        host[HasAt] != 0 && predicted[HasAt] != 0 && host[StateAt] != predicted[StateAt];

    public bool IsValid(ReadOnlySpan<byte> state)
    {
        if (state[HasAt] == 0)
        {
            return true;
        }

        var target = TargetOf(state);
        return state[HasAt] == 1
            && state[StateAt] <= (byte)ClimbState.PullingOver
            && state[JumpAt] <= 1
            && Within(BinaryPrimitives.ReadSingleLittleEndian(state[NormalAt..]), 2)
            && Within(BinaryPrimitives.ReadSingleLittleEndian(state[(NormalAt + 4)..]), 2)
            && target.IsFinite
            && Math.Abs(target.X) < SessionProtocol.MaxPositionMetres
            && Math.Abs(target.Y) < SessionProtocol.MaxPositionMetres
            && Math.Abs(target.Z) < SessionProtocol.MaxPositionMetres
            && float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(state[SecondsAt..]))
            && float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(state[RegrabAt..]));
    }

    private static WorldPoint TargetOf(ReadOnlySpan<byte> state) =>
        new(
            BinaryPrimitives.ReadDoubleLittleEndian(state[TargetAt..]),
            BinaryPrimitives.ReadDoubleLittleEndian(state[(TargetAt + 8)..]),
            BinaryPrimitives.ReadDoubleLittleEndian(state[(TargetAt + 16)..])
        );

    private static bool Within(float value, float most) => float.IsFinite(value) && Math.Abs(value) <= most;
}
