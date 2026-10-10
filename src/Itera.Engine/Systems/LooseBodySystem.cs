using System.Numerics;
using Arch.Buffer;
using Arch.Core;
using Arch.System;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Physics;
using Itera.Engine.Rendering.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Systems;

/// <summary>
/// Ends loose bodies (fixed group, after the tools): a body at rest (<see cref="LooseBodies.IsAtRest"/>) for
/// <c>Engine:Physics:SettleSeconds</c> in a row goes back into the voxel grid where it lies
/// (<see cref="LooseBodies.Settle"/>), unless its bounds reach into a character's cylinder (the piece would fill the air
/// the character stands in); it waits then. Without voxel terrain there is no grid: the game's
/// <see cref="ILooseBodyRest"/> decides whether it stays (<see cref="LooseBody.Kept"/> until it moves), goes, or becomes
/// the game's (<see cref="LooseBodies.Release"/>); with none, it stays. A <see cref="CutPiece"/> never goes into the grid: it is removed after
/// <see cref="CutRestSeconds"/> at rest, where a body frozen because the player left its columns behind counts as
/// resting (<see cref="LooseBodies.IsAtRestOrFrozen"/>), so cut pieces left behind do not stay for the rest of the run
/// and fill <c>Engine:Physics:MaxLooseBodies</c>; and at the latest <see cref="CutMostSeconds"/> after it was made,
/// resting or not. A body that fell out of the world is removed. Either way the Bepu body goes first, then the entity,
/// through a <see cref="CommandBuffer"/> played back after the query. Reads <see cref="CharacterPosition"/> and
/// <see cref="CharacterSize"/>; writes <see cref="LooseBody"/>; destroys loose-body entities. The console's
/// <c>settle</c> changes how long a body rests before it goes back into the grid.
/// </summary>
public sealed class LooseBodySystem : BaseSystem<World, FixedStepTime>, IFixedUpdateSystem
{
    // A client's mirrors of the host's bodies go when the host says, never by resting here.
    private static readonly QueryDescription s_bodies = new QueryDescription()
        .WithAll<LooseBody>()
        .WithNone<RemoteBody>();

    private static readonly QueryDescription s_characters = new QueryDescription().WithAll<
        CharacterPosition,
        CharacterSize
    >();

    /// <summary>How long a <see cref="CutPiece"/> rests before it is removed, in seconds. It is never put into the grid.</summary>
    public const float CutRestSeconds = 30;

    /// <summary>
    /// How long a <see cref="CutPiece"/> lasts at most, in seconds, whether it rests or not: a ragdoll that never comes
    /// to rest, such as one that fell with parts inside a tree, is removed. The bodies of one ragdoll are made in one
    /// step, so they go together.
    /// </summary>
    public const float CutMostSeconds = 60;

    private readonly LooseBodies _bodies;
    private readonly ILooseBodyRest? _rest;
    private readonly VoxelTintSampler? _tint;
    private readonly int _cutTicks;
    private readonly int _cutMostTicks;
    private readonly int _stepHz;
    private readonly ConsoleCommands? _console;
    private int _settleTicks;
    private readonly CommandBuffer _commands = new();
    private bool _disposed;

    /// <param name="world">The ECS world.</param>
    /// <param name="bodies">The loose bodies.</param>
    /// <param name="options">The engine's options, for how long a body rests before it settles.</param>
    /// <param name="tint">
    /// The ground's tint where a body settles, which its model keeps until the world's GPU copy holds its voxels; none
    /// gives 0.
    /// </param>
    /// <param name="console">The console's commands, which gets <c>settle</c>; none in tests.</param>
    /// <param name="rest">What the game makes of a rested body without voxel terrain; none keeps it.</param>
    public LooseBodySystem(
        World world,
        LooseBodies bodies,
        IOptions<EngineOptions> options,
        VoxelTintSampler? tint = null,
        ConsoleCommands? console = null,
        ILooseBodyRest? rest = null
    )
        : base(world)
    {
        _bodies = bodies;
        _rest = rest;
        _tint = tint;
        _console = console;
        var engine = options.Value;
        _stepHz = engine.FixedStepHz;
        _settleTicks = Math.Max(1, (int)Math.Ceiling(engine.Physics.SettleSeconds * engine.FixedStepHz));
        _cutTicks = Math.Max(1, (int)Math.Ceiling(CutRestSeconds * engine.FixedStepHz));
        _cutMostTicks = Math.Max(1, (int)Math.Ceiling(CutMostSeconds * engine.FixedStepHz));
    }

    /// <summary>Fixed steps a body must rest before it goes back into the grid.</summary>
    public int SettleTicks => _settleTicks;

    public override void Initialize()
    {
        EcsChecks.RequireAll<LooseBody>(in s_bodies);
        EcsChecks.RequireAll<CharacterPosition, CharacterSize>(in s_characters);
        _console?.Register(
            "settle",
            "[seconds]",
            "How long a thrown ball rests before it goes back into the grid, 0.5 to 60 s.",
            CommandScope.Match,
            Settle
        );
    }

    private void Settle(CommandContext context)
    {
        if (context.Count > 0)
        {
            // The range of Engine:Physics:SettleSeconds.
            if (!context.TryGetFloat(0, out var seconds) || seconds < 0.5f || seconds > 60)
            {
                context.PrintUsage();
                return;
            }

            _settleTicks = Math.Max(1, (int)Math.Ceiling(seconds * _stepHz));
        }

        context.Print($"settle {CommandContext.Number((double)_settleTicks / _stepHz)} s");
    }

    public override void Update(in FixedStepTime step)
    {
        foreach (ref var chunk in World.Query(in s_bodies))
        {
            var bodies = chunk.GetSpan<LooseBody>();
            foreach (var index in chunk)
            {
                ref var body = ref bodies[index];
                if (_bodies.HasFallenOut(body.Body))
                {
                    _bodies.Remove(body.Body);
                    _commands.Destroy(chunk.Entity(index));
                    continue;
                }

                var cut = _bodies.IsCut(body.Body);
                var resting = cut ? _bodies.IsAtRestOrFrozen(body.Body) : _bodies.IsAtRest(body.Body);
                body.RestTicks = resting ? body.RestTicks + 1 : 0;
                body.Kept &= resting;
                if (cut)
                {
                    // A cut piece: removed after resting (or lying frozen), or at the latest when old enough; never put
                    // into the grid.
                    body.AgeTicks++;
                    if (body.RestTicks >= _cutTicks || body.AgeTicks >= _cutMostTicks)
                    {
                        _bodies.Remove(body.Body);
                        _commands.Destroy(chunk.Entity(index));
                    }

                    continue;
                }

                if (body.RestTicks < _settleTicks || body.Kept)
                {
                    continue;
                }

                if (!_bodies.CanSettle)
                {
                    var choice = _rest?.Rested(_bodies.Rested(body.Body)) ?? LooseBodyRestChoice.Keep;
                    if (choice == LooseBodyRestChoice.Remove)
                    {
                        _bodies.Remove(body.Body);
                        _commands.Destroy(chunk.Entity(index));
                    }
                    else if (choice == LooseBodyRestChoice.TakeOver)
                    {
                        _bodies.Release(body.Body);
                        _commands.Destroy(chunk.Entity(index));
                    }
                    else
                    {
                        body.Kept = true;
                    }

                    continue;
                }

                _bodies.BoundsOf(body.Body, out var min, out var max);
                var touch = new TouchesCharacter { Min = min, Max = max };
                World.InlineQuery<TouchesCharacter, CharacterPosition, CharacterSize>(in s_characters, ref touch);
                if (touch.Touches)
                {
                    continue;
                }

                _bodies.Settle(body.Body, _tint?.At(_bodies.Origin.ToWorld(_bodies.PivotOf(body.Body))) ?? 0);
                _commands.Destroy(chunk.Entity(index));
            }
        }

        _commands.Playback(World);
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commands.Dispose();
        base.Dispose();
    }

    /// <summary>Whether a box comes into any character's upright cylinder.</summary>
    private struct TouchesCharacter : IForEach<CharacterPosition, CharacterSize>
    {
        public Vector3 Min;
        public Vector3 Max;
        public bool Touches;

        public void Update(ref CharacterPosition position, ref CharacterSize size)
        {
            var feet = position.Feet;
            var nearestX = Math.Clamp(feet.X, Min.X, Max.X);
            var nearestZ = Math.Clamp(feet.Z, Min.Z, Max.Z);
            var across = new Vector2(nearestX - feet.X, nearestZ - feet.Z).LengthSquared();
            Touches |=
                across < size.RadiusMetres * size.RadiusMetres && Max.Y > feet.Y && Min.Y < feet.Y + size.HeightMetres;
        }
    }
}
