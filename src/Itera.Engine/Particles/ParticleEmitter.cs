using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Particles;

/// <summary>What <see cref="ParticleEmitter.Drain"/> handed to the frame.</summary>
/// <param name="Bursts">Bursts written.</param>
/// <param name="Spawns">Particles they make.</param>
/// <param name="Active">
/// True while particles may be alive: from a frame that spawns until the longest life among the recipes of its bursts
/// has passed on the particles' clock, which runs only in frames that simulate them. Only then does the frame simulate
/// and draw them, and copy the world's depth for them.
/// </param>
/// <param name="Clear">
/// True in the first frame after <see cref="ParticleEmitter.Clear"/>: every particle made before it dies before this
/// frame's bursts are made.
/// </param>
/// <param name="LiveFirst">
/// The first pool slot that may hold a living particle once this frame's bursts are made: 0 to the pool's capacity less
/// one, and 0 when <paramref name="LiveCount"/> is 0.
/// </param>
/// <param name="LiveCount">
/// How many pool slots, from <paramref name="LiveFirst"/> on and wrapping round from the pool's last slot to slot 0,
/// may hold a living particle: 0 to the pool's capacity. No slot outside them holds one. 0 when the frame is not
/// <paramref name="Active"/>; the whole pool, from slot 0, when particles still alive were overwritten.
/// </param>
public readonly record struct ParticleFrame(
    int Bursts,
    int Spawns,
    bool Active,
    bool Clear = false,
    int LiveFirst = 0,
    int LiveCount = 0
);

/// <summary>
/// Where game code asks for particles. Fixed and frame systems queue <see cref="ParticleBurst"/>s with
/// <see cref="Emit"/>; the bursts wait until the next frame is built, where <see cref="Drain"/> hands them to the GPU
/// (<c>ParticlePass.Write</c>) with the pool slots each one takes. The pool is a ring: each burst takes the next slots
/// in one piece (one that would pass the end starts again at slot 0), so when particles are made faster than they die
/// the oldest are overwritten, living or not. Particles live only on the GPU: they move, meet the world, and are drawn
/// there, and nothing reads them back into the game. A frame takes at most
/// <see cref="ParticleSettings.MaxBurstsPerFrame"/> bursts and <see cref="ParticleSettings.MaxSpawnsPerFrame"/>
/// particles; the rest are dropped, with one Warning. A burst of a detail recipe (<see cref="ParticleRecipe.Detail"/>)
/// makes the effects' share of its particles as it is drained (<see cref="EffectsBudget.Share"/>). Each frame that makes particles is kept as a spawn batch (where
/// it starts in the ring, and when its longest-lived particle dies), so a frame knows which slots can still be alive
/// (<see cref="ParticleFrame.LiveFirst"/>). It also holds the attractors (points that bursts' particles fly to) and the
/// console's stress test. Main thread only; allocates nothing.
/// </summary>
public sealed partial class ParticleEmitter
{
    /// <summary>Attractor slots; slot 0 means none, so there are this many less one.</summary>
    public const int MaxAttractors = 64;

    /// <summary>The attractor the console's pulled test bursts fly to, which the engine keeps at the camera.</summary>
    public const int CameraAttractor = MaxAttractors - 1;

    /// <summary>The spawn batches kept; when full, the two oldest become one.</summary>
    internal const int MaxBatches = 256;

    private readonly ParticleBurst[] _queue;
    private readonly Vector4[] _attractors = new Vector4[MaxAttractors];
    private readonly SpawnBatch[] _batches = new SpawnBatch[MaxBatches];
    private readonly ILogger<ParticleEmitter> _logger;
    private readonly EffectsBudget _effects;
    private int _queued;
    private int _queuedSpawns;
    private int _head;

    // Slots handed out since the start or the last clear, with those a burst skipped at the pool's end: the head's
    // place along the ring, laps and all. The head is always this modulo the capacity.
    private long _written;
    private int _oldestBatch;
    private int _batchCount;

    // The particles' clock in seconds: the sum of the deltas of the frames that moved them, as the GPU ages them.
    private double _particleSeconds;
    private uint _seed = 0x9E37_79B9;
    private bool _clear;
    private bool _warned;

    /// <param name="options">The particles' options.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="effects">
    /// The share of a detail recipe's particles made (<see cref="ParticleRecipe.Detail"/>); one of its own, at its
    /// ceiling, when null (tests).
    /// </param>
    public ParticleEmitter(
        IOptions<EngineOptions> options,
        ILogger<ParticleEmitter> logger,
        EffectsBudget? effects = null
    )
    {
        var settings = options.Value.Particles;
        _logger = logger;
        _effects = effects ?? new EffectsBudget();
        Enabled = settings.Enabled;
        Capacity = settings.Capacity;
        MaxBursts = settings.MaxBurstsPerFrame;
        // At most half the pool a frame, so two bursts of one frame never take the same slots of the ring.
        MaxSpawns = Math.Min(settings.MaxSpawnsPerFrame, settings.Capacity / 2);
        _queue = new ParticleBurst[MaxBursts];
    }

    /// <summary>The recipes bursts name.</summary>
    public ParticleRecipes Recipes { get; } = new();

    /// <summary>False drops every burst and draws nothing (the console's <c>particles off</c>).</summary>
    public bool Enabled { get; set; }

    /// <summary>Particles the pool holds.</summary>
    public int Capacity { get; }

    /// <summary>The most bursts one frame takes.</summary>
    public int MaxBursts { get; }

    /// <summary>The most particles one frame makes.</summary>
    public int MaxSpawns { get; }

    /// <summary>Bursts waiting for the next frame.</summary>
    public int QueuedBursts => _queued;

    /// <summary>
    /// Particles the waiting bursts ask for, before a detail recipe's are thinned (<see cref="EffectsBudget.Share"/>):
    /// what the frame's cap counts.
    /// </summary>
    public int QueuedSpawns => _queuedSpawns;

    /// <summary>Bursts dropped since the start because a frame was full.</summary>
    public long DroppedBursts { get; private set; }

    /// <summary>The attractors: world metres in x, y, z, and 1 in w while set.</summary>
    public ReadOnlySpan<Vector4> Attractors => _attractors;

    /// <summary>
    /// The console's stress test: while above 0, every frame adds this many bursts of <see cref="StressRecipe"/>
    /// spread over a 6 m square around <see cref="StressOrigin"/>, which the console's command keeps where the camera
    /// looks (<c>ParticleCommands.UpdateStress</c>).
    /// </summary>
    public int StressBursts { get; set; }

    /// <summary>The recipe of the stress test's bursts.</summary>
    public int StressRecipe { get; set; }

    /// <summary>Particles in each stress test burst.</summary>
    public int StressCount { get; set; } = 64;

    /// <summary>Where the stress test's bursts are made.</summary>
    public Vector3 StressOrigin { get; set; }

    /// <summary>
    /// Queues <paramref name="burst"/> for the next frame. Dropped while <see cref="Enabled"/> is false, and when the
    /// frame already holds as many bursts or particles as it takes. Throws for a recipe that is not registered.
    /// </summary>
    public void Emit(in ParticleBurst burst)
    {
        if ((uint)burst.Recipe >= (uint)Recipes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(burst), burst.Recipe, "No such particle recipe.");
        }

        if (!Enabled || burst.Count <= 0)
        {
            return;
        }

        var count = Math.Min(burst.Count, MaxSpawns);
        if (_queued == _queue.Length || _queuedSpawns + count > MaxSpawns)
        {
            DroppedBursts++;
            if (!_warned)
            {
                _warned = true;
                LogFull(MaxBursts, MaxSpawns);
            }

            return;
        }

        ref var queued = ref _queue[_queued++];
        queued = burst;
        queued.Count = count;
        _queuedSpawns += count;
    }

    /// <summary>Puts attractor <paramref name="slot"/> (1 to <see cref="MaxAttractors"/> - 1) at <paramref name="position"/>.</summary>
    public void SetAttractor(int slot, Vector3 position)
    {
        CheckAttractor(slot);
        _attractors[slot] = new Vector4(position, 1);
    }

    /// <summary>Clears attractor <paramref name="slot"/>: its particles fly on as they are.</summary>
    public void ClearAttractor(int slot)
    {
        CheckAttractor(slot);
        _attractors[slot] = default;
    }

    /// <summary>
    /// Moves what the emitter keeps in local positions with the match's origin (<c>OriginShifts</c>): the bursts queued
    /// for the next frame, the attractors set, and the stress test's place, each less <paramref name="metres"/>. The
    /// particles already made are the GPU pool's.
    /// </summary>
    public void Shift(Vector3 metres)
    {
        for (var i = 0; i < _queued; i++)
        {
            _queue[i].Origin -= metres;
            _queue[i].End -= metres;
        }

        for (var i = 0; i < _attractors.Length; i++)
        {
            if (_attractors[i].W != 0)
            {
                _attractors[i] -= new Vector4(metres, 0);
            }
        }

        StressOrigin -= metres;
    }

    /// <summary>
    /// Ends every particle (the console's <c>cleanup</c>): the bursts queued so far are dropped, the stress test stops,
    /// and the next frame (<see cref="Drain"/>, <see cref="ParticleFrame.Clear"/>) empties the pool before it makes the
    /// bursts queued after this call.
    /// </summary>
    public void Clear()
    {
        _queued = 0;
        _queuedSpawns = 0;
        StressBursts = 0;
        _head = 0;
        _written = 0;
        _batchCount = 0;
        _clear = true;
    }

    /// <summary>
    /// Drops the bursts queued so far, as a frame without graphics does, which has no pool to make them in (a run with
    /// <c>Engine:Window:Mode=None</c>, a headless match): the queue never fills, and nothing is counted as dropped.
    /// Main thread.
    /// </summary>
    public void Discard()
    {
        _queued = 0;
        _queuedSpawns = 0;
    }

    /// <summary>
    /// Writes the queued bursts, and the stress test's, into <paramref name="destination"/> with their pool slots, and
    /// empties the queue. <paramref name="delta"/> is the step in seconds the frame's simulate pass ages particles by
    /// (<c>ParticleConstants.Delta</c>). <paramref name="worldDrawn"/> is false when the loading screen covers the
    /// world: the GPU then neither makes nor moves particles. Main thread, once per frame built.
    /// </summary>
    /// <remarks>
    /// The particles' clock advances by <paramref name="delta"/> only in frames that simulate, so a spawn batch dies when
    /// the GPU's ages say it does, whatever the wall clock did.
    /// </remarks>
    public ParticleFrame Drain(Span<GpuParticleBurst> destination, float delta, bool worldDrawn = true)
    {
        var clear = _clear;
        _clear = false;
        if (!Enabled)
        {
            // Nothing is simulated while off: the particles wait, and their batches with them.
            _queued = 0;
            _queuedSpawns = 0;
            return new ParticleFrame(0, 0, false, clear);
        }

        // A batch whose particles all died in the last frame that moved them is not simulated again.
        DropDeadBatches();
        AddStress();
        var count = Math.Min(_queued, destination.Length);
        var spawns = 0;
        var batchStart = 0L;
        var longest = 0f;
        for (var i = 0; i < count; i++)
        {
            ref readonly var burst = ref _queue[i];
            ref readonly var recipe = ref Recipes[burst.Recipe];
            // A detail recipe's burst makes the effects' share of its particles.
            var made = recipe.Detail ? _effects.Thin(burst.Count) : burst.Count;
            if (_head + made > Capacity)
            {
                _written += Capacity - _head;
                _head = 0;
            }

            if (i == 0)
            {
                batchStart = _written;
            }

            destination[i] = new GpuParticleBurst
            {
                Origin = burst.Origin,
                Prefix = (uint)spawns,
                Direction = burst.Direction,
                Count = (uint)made,
                End = burst.End,
                Radius = burst.Radius,
                Velocity = burst.Velocity,
                First = (uint)_head,
                Packed =
                    (uint)burst.Recipe
                    | ((uint)burst.Shape & 15) << 8
                    | (uint)Math.Clamp(burst.Attractor, 0, MaxAttractors - 1) << 12,
                Colour = burst.Tint == Vector4.Zero ? uint.MaxValue : GpuParticleRecipe.PackColour(burst.Tint),
                Seed = burst.Seed != 0 ? burst.Seed : NextSeed(),
            };
            _head = (_head + made) % Capacity;
            _written += made;
            spawns += made;
            longest = Math.Max(longest, recipe.LifetimeMax);
        }

        _queued = 0;
        _queuedSpawns = 0;

        // The GPU makes and moves particles only in a frame that draws the world and has some alive or new; only such a
        // frame ages them. Under the loading screen this frame's bursts are not made, so they need no batch.
        if (worldDrawn && (spawns > 0 || _batchCount > 0))
        {
            _particleSeconds += delta;
            if (spawns > 0)
            {
                // A new particle's age after a later frame is at least the clock then less the clock now. Once the clock
                // before a frame has passed the clock now plus its life, the GPU ended it in an earlier frame.
                AddBatch(batchStart, _particleSeconds + longest + DeathMargin(longest));
            }
        }

        var (liveFirst, liveCount) = LiveSlots();
        return new ParticleFrame(count, spawns, _batchCount > 0, clear, liveFirst, liveCount);
    }

    /// <summary>
    /// How much longer than its life a batch is kept, in seconds: one capped frame step, and a 128th of the life for the
    /// float rounding of the GPU's ages, which can fall behind the clock's sum over thousands of frames.
    /// </summary>
    internal static double DeathMargin(float lifetime) => GameClock.MaxDeltaSeconds + lifetime / 128.0;

    private void AddBatch(long start, double dies)
    {
        if (_batchCount == MaxBatches)
        {
            // The two oldest become one: from the older's start (the newer's slots follow it in the ring) until the
            // later death. It covers every slot either took, so the live slots stay a superset of the living.
            var next = (_oldestBatch + 1) % MaxBatches;
            ref var merged = ref _batches[next];
            merged.Start = _batches[_oldestBatch].Start;
            merged.Dies = Math.Max(merged.Dies, _batches[_oldestBatch].Dies);
            _oldestBatch = next;
            _batchCount--;
        }

        _batches[(_oldestBatch + _batchCount) % MaxBatches] = new SpawnBatch(start, dies);
        _batchCount++;
    }

    private void DropDeadBatches()
    {
        while (_batchCount > 0 && _batches[_oldestBatch].Dies <= _particleSeconds)
        {
            _oldestBatch = (_oldestBatch + 1) % MaxBatches;
            _batchCount--;
        }
    }

    // The ring from the oldest batch kept to the head. Every slot outside it was last written by a batch whose
    // particles have all died, or never, because batches are dropped only from the oldest end. A full lap or more
    // means living particles were overwritten, and the whole pool may hold some.
    private (int First, int Count) LiveSlots()
    {
        if (_batchCount == 0)
        {
            return (0, 0);
        }

        var start = _batches[_oldestBatch].Start;
        var length = _written - start;
        return length >= Capacity ? (0, Capacity) : ((int)(start % Capacity), (int)length);
    }

    private void AddStress()
    {
        if (StressBursts <= 0 || (uint)StressRecipe >= (uint)Recipes.Count)
        {
            return;
        }

        for (var i = 0; i < StressBursts; i++)
        {
            var seed = NextSeed();
            // A point on a 6 m square around the origin, from the seed's bits.
            var offset = new Vector3((seed & 0xFFFF) / 65535f - 0.5f, 0, (seed >> 16) / 65535f - 0.5f) * 6;
            Emit(
                new ParticleBurst
                {
                    Recipe = StressRecipe,
                    Count = StressCount,
                    Shape = ParticleShape.Sphere,
                    Origin = StressOrigin + offset,
                    Radius = 0.2f,
                    Direction = Vector3.UnitY,
                    Seed = seed,
                }
            );
        }
    }

    private uint NextSeed()
    {
        // A step of a 32-bit xorshift: never 0 from a seed that is not 0.
        var x = _seed;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _seed = x;
        return x;
    }

    private static void CheckAttractor(int slot)
    {
        if (slot is < 1 or >= MaxAttractors)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Attractors are 1 to 63.");
        }
    }

    /// <summary>
    /// The slots one frame's bursts took: from <see cref="Start"/> (a place along the ring, laps and all) to the next
    /// batch's start or the head, and the particles' clock in seconds by which all its particles are dead.
    /// </summary>
    private record struct SpawnBatch(long Start, double Dies);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Particles: more than {Bursts} bursts or {Spawns} particles waited for one frame; the rest are dropped"
    )]
    private partial void LogFull(int bursts, int spawns);
}
