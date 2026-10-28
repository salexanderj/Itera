using System.Text;
using Arch.Core;
using Itera.Engine.Maths;

namespace Itera.Engine.Replication;

/// <summary>What a replicated kind's handler is given: the match's ECS world and origin.</summary>
/// <param name="World">The match's ECS world.</param>
/// <param name="Origin">The match's origin, through which a local position becomes the absolute one a state holds.</param>
public readonly record struct ReplicationContext(World World, WorldOrigin Origin);

/// <summary>
/// A game's side of one replicated kind: the host reads an entity's state into a <see cref="ReplicatedState"/> by its
/// schema, and a client makes, shows, and removes the entities the host sends. Run-wide (registered at the run's start),
/// so it keeps no match's state; it is called on the main thread from the replication systems, outside any query, so
/// structural changes are safe in it.
/// </summary>
public interface IReplicatedKind
{
    /// <summary>On the host: writes <paramref name="entity"/>'s state now (absolute positions: the context's origin).</summary>
    void Capture(in ReplicationContext context, Entity entity, ref ReplicatedState state);

    /// <summary>
    /// On a client: makes the entity for an id the host sent, first shown at <paramref name="state"/>. The engine adds
    /// its <see cref="Components.Replicated"/> after; return <see cref="Entity.Null"/> to leave the id unshown.
    /// </summary>
    Entity Spawn(in ReplicationContext context, in ReplicatedState state);

    /// <summary>
    /// On a client, each step: shows <paramref name="entity"/> at <paramref name="state"/>, blended between the two states
    /// the host sent round the time shown (a fixed step: a pose kept for drawing copies its last into its previous one
    /// first).
    /// </summary>
    void Apply(in ReplicationContext context, Entity entity, in ReplicatedState state);

    /// <summary>
    /// On a client: the host removed the entity, or it went out of this client's reach. Destroy it (or keep it, for a
    /// game's own reasons); the engine forgets its id either way.
    /// </summary>
    void Despawn(in ReplicationContext context, Entity entity);
}

/// <summary>A replicated kind's number, as it travels: its place in <see cref="ReplicatedKinds"/>.</summary>
public readonly record struct ReplicatedKindId(byte Value);

/// <summary>
/// One replicated kind: its name, schema, handler, and how the host chooses what to send of it: within
/// <see cref="RelevanceMetres"/> of a client's player (0: to every client wherever it is), and how fast its priority
/// grows (<see cref="Priority"/>: a player 4, a character 1, a far body 0.25).
/// </summary>
public sealed record ReplicatedKind(
    ReplicatedKindId Id,
    string Name,
    ReplicatedSchema Schema,
    IReplicatedKind Handler,
    float RelevanceMetres,
    float Priority
);

/// <summary>
/// The run's replicated kinds, and the parts of a predicted player's state a game adds (<see cref="IPredictedState"/>,
/// after the engine's climber), registered once at its start (a game's <c>IRunStartup</c>) and sealed by the first
/// match. Their names and fields make a fingerprint a client's hello carries, so a host refuses a client whose kinds or
/// parts differ.
/// </summary>
public sealed class ReplicatedKinds
{
    /// <summary>The most kinds a run registers.</summary>
    public const int MostKinds = 255;

    /// <summary>
    /// The most bytes all of a predicted player's parts take, the engine's climber's 43 among them. The owner's block
    /// carries only the parts' own bytes, so the most changes no message; each step a client keeps holds this many.
    /// </summary>
    public const int MostPredictedBytes = 256;

    private readonly List<ReplicatedKind> _kinds = [];
    private readonly List<IPredictedState> _predicted = [new ClimberPredictedState()];
    private int _predictedBytes = new ClimberPredictedState().Bytes;
    private bool _sealed;
    private uint _heldActions;
    private uint _heldKeyActions;

    public int Count => _kinds.Count;

    /// <summary>
    /// The bits of a player's game actions (<c>PlayerCommand.Actions</c>) that hold a level from step to step, such as a
    /// mode the player is in: a host that misses a client's command keeps them from its last one for as long as none
    /// comes. The other bits may be one-shot, and a missed command has none of them, but <see cref="HeldKeyActions"/> for
    /// a while. None by default. Set before the first match, which seals it.
    /// </summary>
    public uint HeldActions
    {
        get => _heldActions;
        set
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    "The replicated kinds are sealed by the first match; set the held actions before it."
                );
            }

            _heldActions = value;
        }
    }

    /// <summary>
    /// The bits of a player's game actions set while a key is held, such as a trigger or a grenade primed: a host that
    /// misses a client's command keeps them from its last one while it keeps its move, so a late command lets go of no
    /// key the player still holds. After a longer gap it lets them go, as it stands the player still. None by default.
    /// Set before the first match, which seals it.
    /// </summary>
    public uint HeldKeyActions
    {
        get => _heldKeyActions;
        set
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    "The replicated kinds are sealed by the first match; set the held keys' actions before it."
                );
            }

            _heldKeyActions = value;
        }
    }

    public ReplicatedKind this[ReplicatedKindId id] => _kinds[id.Value];

    /// <summary>
    /// Registers a kind, sealing its schema. Names are unique; <paramref name="relevanceMetres"/> 0 sends it to every
    /// client wherever it is; <paramref name="priority"/> is above 0.
    /// </summary>
    public ReplicatedKindId Register(
        string name,
        ReplicatedSchema schema,
        IReplicatedKind handler,
        float relevanceMetres = 0,
        float priority = 1
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(handler);
        if (_sealed)
        {
            throw new InvalidOperationException(
                $"The replicated kinds are sealed by the first match; {name} is too late."
            );
        }

        if (!float.IsFinite(relevanceMetres) || relevanceMetres < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(relevanceMetres), "Relevance is 0 or more metres.");
        }

        if (!float.IsFinite(priority) || !(priority > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), "Priority is above 0.");
        }

        if (_kinds.Count == MostKinds)
        {
            throw new InvalidOperationException($"At most {MostKinds} replicated kinds; {name} is one too many.");
        }

        foreach (var kind in _kinds)
        {
            if (string.Equals(kind.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The replicated kind {name} is registered already.");
            }
        }

        schema.Seal();
        var id = new ReplicatedKindId((byte)_kinds.Count);
        _kinds.Add(new ReplicatedKind(id, name, schema, handler, relevanceMetres, priority));
        return id;
    }

    /// <summary>
    /// Registers a part of a predicted player's state that a game keeps in a component of its own, after the engine's
    /// and those registered before it. Names are unique, and all the parts take at most <see cref="MostPredictedBytes"/>.
    /// </summary>
    public void RegisterPredicted(IPredictedState part)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(part.Name);
        if (_sealed)
        {
            throw new InvalidOperationException(
                $"The replicated kinds are sealed by the first match; the predicted part {part.Name} is too late."
            );
        }

        if (part.Bytes < 1 || _predictedBytes + part.Bytes > MostPredictedBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(part),
                $"The predicted part {part.Name} takes {part.Bytes} bytes; {MostPredictedBytes - _predictedBytes} are left."
            );
        }

        foreach (var other in _predicted)
        {
            if (string.Equals(other.Name, part.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The predicted part {part.Name} is registered already.");
            }
        }

        _predicted.Add(part);
        _predictedBytes += part.Bytes;
    }

    /// <summary>The parts of a predicted player's state, the engine's climber first, in the order of their registration.</summary>
    internal IReadOnlyList<IPredictedState> Predicted => _predicted;

    /// <summary>The bytes all of a predicted player's parts take.</summary>
    internal int PredictedBytes => _predictedBytes;

    /// <summary>
    /// A number the kinds' names and fields, and the game's predicted parts' names and sizes, make in their order (FNV-1a
    /// over their descriptions): the same build of the same game gives the same one. 0 with neither (the engine's own
    /// climber is the same in every build the protocol's version lets meet).
    /// </summary>
    public ulong Fingerprint
    {
        get
        {
            if (_kinds.Count == 0 && _predicted.Count == 1)
            {
                return 0;
            }

            var text = new StringBuilder();
            foreach (var kind in _kinds)
            {
                text.Append(kind.Name).Append('=').Append(kind.Schema.Description).Append('|');
            }

            for (var i = 1; i < _predicted.Count; i++)
            {
                text.Append("predicted ")
                    .Append(_predicted[i].Name)
                    .Append('=')
                    .Append(_predicted[i].Bytes)
                    .Append('|');
            }

            var hash = 14695981039346656037ul;
            foreach (var b in Encoding.UTF8.GetBytes(text.ToString()))
            {
                hash = (hash ^ b) * 1099511628211ul;
            }

            return hash;
        }
    }

    /// <summary>No more kinds: the first match seals them.</summary>
    internal void Seal() => _sealed = true;
}
