using System.Diagnostics;
using System.Numerics;
using Arch.Core;
using Itera.Engine.Audio;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Maths;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Matches;

/// <summary>
/// Moves the match's origin (<see cref="WorldOrigin"/>) and, with it, everything the engine keeps in local positions, so
/// that nothing moves in the world: every Bepu body and static (<see cref="PhysicsWorld.Shift"/>), the engine's
/// components (<see cref="ShiftedComponents"/>), the world's streaming centres, the retired models, the point lights'
/// flashes, the particles' queued bursts and attractors, the sounds queued, held, waiting, and playing, and the frame
/// view's camera; then the game's <see cref="IOriginShiftHandler"/>s, in
/// their registration order. A local position far from the new origin rounds to its floats there. It moves only between
/// steps and frames or in the first fixed system's update, so every system of a step or a frame sees one origin (it
/// throws elsewhere). Mods are not told. Scoped to the match; main thread; allocates nothing but its Debug line.
/// </summary>
public sealed partial class OriginShifts
{
    /// <summary>The components' fields the engine moves, as "Type.Field": every local position they keep.</summary>
    public static readonly IReadOnlyList<string> ShiftedComponents =
    [
        "CameraPose.Position",
        "CharacterPosition.Feet",
        "CharacterPosition.PreviousFeet",
        "Climber.Target",
        "MeshPlacement.Position",
        "MeshPlacement.PreviousPosition",
        "BodyPose.Position",
        "BodyPose.PreviousPosition",
    ];

    /// <summary>The farthest the origin may go from the map's middle on any axis, in metres: the edit format's reach.</summary>
    public const double MostMetres = 167_772.16;

    private static readonly QueryDescription s_cameras = new QueryDescription().WithAll<CameraPose>();
    private static readonly QueryDescription s_characters = new QueryDescription().WithAll<CharacterPosition>();
    private static readonly QueryDescription s_climbers = new QueryDescription().WithAll<Climber>();
    private static readonly QueryDescription s_meshes = new QueryDescription().WithAll<MeshPlacement>();
    private static readonly QueryDescription s_bodies = new QueryDescription().WithAll<BodyPose>();

    private readonly PhysicsWorld _physics;
    private readonly EcsWorld _ecs;
    private readonly VoxelModels _models;
    private readonly PointLights _lights;
    private readonly ParticleEmitter _particles;
    private readonly IOriginShiftHandler[] _handlers;
    private readonly ILogger<OriginShifts> _logger;
    private readonly VoxelWorld? _voxels;
    private readonly AudioService? _audio;
    private readonly RenderView? _view;

    /// <param name="origin">The match's origin.</param>
    /// <param name="physics">The match's Bepu simulation.</param>
    /// <param name="ecs">The match's entities.</param>
    /// <param name="models">The match's voxel models (their retired corners).</param>
    /// <param name="lights">The run's point lights (their flashes).</param>
    /// <param name="particles">The run's particle emitter (its queued bursts and attractors).</param>
    /// <param name="handlers">The game's handlers.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="voxels">The match's voxel world; none without terrain.</param>
    /// <param name="audio">The run's sound; none in a test without it.</param>
    /// <param name="view">The run's frame view, whose camera moves too; none in a test without it.</param>
    public OriginShifts(
        WorldOrigin origin,
        PhysicsWorld physics,
        EcsWorld ecs,
        VoxelModels models,
        PointLights lights,
        ParticleEmitter particles,
        IEnumerable<IOriginShiftHandler> handlers,
        ILogger<OriginShifts> logger,
        VoxelWorld? voxels = null,
        AudioService? audio = null,
        RenderView? view = null
    )
    {
        Origin = origin;
        _physics = physics;
        _ecs = ecs;
        _models = models;
        _lights = lights;
        _particles = particles;
        _handlers = [.. handlers];
        _logger = logger;
        _voxels = voxels;
        _audio = audio;
        _view = view;
    }

    /// <summary>The match's origin.</summary>
    public WorldOrigin Origin { get; }

    /// <summary>
    /// Moves the origin to the whole metres nearest <paramref name="centre"/> and everything kept in local positions with
    /// it; false when the origin is there already.
    /// </summary>
    /// <exception cref="ArgumentException">The centre is not finite or lies past <see cref="MostMetres"/>.</exception>
    /// <exception cref="InvalidOperationException">A step or a frame is under way past its first fixed system.</exception>
    public bool ShiftTo(in WorldPoint centre)
    {
        if (
            !centre.IsFinite
            || Math.Abs(centre.X) > MostMetres
            || Math.Abs(centre.Y) > MostMetres
            || Math.Abs(centre.Z) > MostMetres
        )
        {
            throw new ArgumentException($"The origin cannot move to {centre}.", nameof(centre));
        }

        var to = new VoxelCoord(WholeMetres(centre.X), WholeMetres(centre.Y), WholeMetres(centre.Z));
        if (to == Origin.Centimetres)
        {
            return false;
        }

        if (!_ecs.OriginMayMove)
        {
            throw new InvalidOperationException(
                "The origin moves only between steps and frames, or in the first fixed system's update."
            );
        }

        var start = Stopwatch.GetTimestamp();
        var shift = new OriginShift(Origin.Centimetres, to);
        var metres = shift.Metres;
        Origin.MoveTo(to);
        var (bodies, statics) = _physics.IsInitialized ? _physics.Shift(metres) : (0, 0);
        ShiftComponents(_ecs.World, metres);
        _voxels?.ShiftCentres(metres);
        _models.ShiftRetired(metres);
        _lights.Shift(metres);
        _particles.Shift(metres);
        _audio?.Shift(metres);
        _view?.ShiftCamera(metres);
        foreach (var handler in _handlers)
        {
            handler.OnOriginShifted(in shift);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var at = Origin.Metres;
            LogShifted(at.X, at.Y, at.Z, metres.X, metres.Y, metres.Z, bodies, statics, milliseconds);
        }

        return true;
    }

    /// <summary>The whole metres nearest <paramref name="metres"/>, in centimetres.</summary>
    private static int WholeMetres(double metres) =>
        (int)Math.Round(metres, MidpointRounding.AwayFromZero) * VoxelConstants.CentimetresPerMetre;

    /// <summary>Moves the engine's components' local positions (<see cref="ShiftedComponents"/>) by -<paramref name="metres"/>.</summary>
    private static void ShiftComponents(World world, Vector3 metres)
    {
        var job = new Move { Metres = metres };
        world.InlineQuery<Move, CameraPose>(in s_cameras, ref job);
        world.InlineQuery<Move, CharacterPosition>(in s_characters, ref job);
        world.InlineQuery<Move, Climber>(in s_climbers, ref job);
        world.InlineQuery<Move, MeshPlacement>(in s_meshes, ref job);
        world.InlineQuery<Move, BodyPose>(in s_bodies, ref job);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Origin: moved to {X:F0},{Y:F0},{Z:F0} m (by {ByX:F0},{ByY:F0},{ByZ:F0}): {Bodies} bodies and {Statics} statics, {Milliseconds:F2} ms"
    )]
    private partial void LogShifted(
        double x,
        double y,
        double z,
        float byX,
        float byY,
        float byZ,
        int bodies,
        int statics,
        double milliseconds
    );

    /// <summary>Moves each local position it is given by -<see cref="Metres"/>, current and previous together.</summary>
    private struct Move
        : IForEach<CameraPose>,
            IForEach<CharacterPosition>,
            IForEach<Climber>,
            IForEach<MeshPlacement>,
            IForEach<BodyPose>
    {
        public Vector3 Metres;

        public readonly void Update(ref CameraPose pose) => pose.Position -= Metres;

        public readonly void Update(ref CharacterPosition position)
        {
            position.Feet -= Metres;
            position.PreviousFeet -= Metres;
        }

        public readonly void Update(ref Climber climber) => climber.Target -= Metres;

        public readonly void Update(ref MeshPlacement placement)
        {
            placement.Position -= Metres;
            placement.PreviousPosition -= Metres;
        }

        public readonly void Update(ref BodyPose pose)
        {
            pose.Position -= Metres;
            pose.PreviousPosition -= Metres;
        }
    }
}
