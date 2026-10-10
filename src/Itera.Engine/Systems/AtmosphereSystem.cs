using Arch.Core;
using Arch.System;
using Itera.Engine.Ecs;
using Itera.Engine.Maths;
using Itera.Engine.Rendering;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Where the air, sky, and sunlight differ: the <see cref="Atmosphere"/> at a place in the world, and the look the frame
/// is drawn with there (<see cref="RenderLook"/>). A game implements it (a map of regions, biomes, a height, a time of
/// day) and gives it to <see cref="AtmosphereSystem"/>. Called once a frame on the main thread; it allocates nothing.
/// </summary>
public interface IAtmosphereSource
{
    /// <summary>The atmosphere at the absolute point <paramref name="position"/>, which may lie far outside the world.</summary>
    Atmosphere At(in WorldPoint position);

    /// <summary>
    /// The look at the absolute point <paramref name="position"/>: the engine's (<see cref="RenderLook.Default"/>) unless
    /// the game gives its own.
    /// </summary>
    RenderLook LookAt(in WorldPoint position) => RenderLook.Default;
}

/// <summary>
/// Sets the frame's sky, sunlight, and air, and its look, from an <see cref="IAtmosphereSource"/> at the camera (frame
/// group, after <see cref="CameraExtractSystem"/>, whose camera it reads from <see cref="RenderView"/>). The source
/// decides both, blending included, at the camera's absolute place (its local position plus the match's origin). Reads
/// and writes no components.
/// </summary>
public sealed class AtmosphereSystem : BaseSystem<World, FrameTime>, IFrameUpdateSystem
{
    private readonly RenderView _view;
    private readonly IAtmosphereSource _source;
    private readonly WorldOrigin _origin;

    /// <param name="world">The match's ECS world.</param>
    /// <param name="view">The frame's view, whose camera is read and whose atmosphere and look are set.</param>
    /// <param name="source">The game's atmosphere.</param>
    /// <param name="origin">The match's origin (a test may leave it out: the map's middle).</param>
    public AtmosphereSystem(World world, RenderView view, IAtmosphereSource source, WorldOrigin? origin = null)
        : base(world)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(source);
        _view = view;
        _source = source;
        _origin = origin ?? new WorldOrigin();
    }

    public override void Update(in FrameTime frame)
    {
        var position = _origin.ToWorld(_view.Camera.Position);
        var atmosphere = _source.At(in position);
        _view.SetAtmosphere(in atmosphere);
        var look = _source.LookAt(in position);
        _view.SetLook(in look);
    }
}
