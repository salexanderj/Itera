using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Ecs;
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
    /// <summary>The atmosphere at <paramref name="positionMetres"/>, which may lie far outside the world.</summary>
    Atmosphere At(Vector3 positionMetres);

    /// <summary>The look at <paramref name="positionMetres"/>: the engine's (<see cref="RenderLook.Default"/>) unless the game gives its own.</summary>
    RenderLook LookAt(Vector3 positionMetres) => RenderLook.Default;
}

/// <summary>
/// Sets the frame's sky, sunlight, and air, and its look, from an <see cref="IAtmosphereSource"/> at the camera (frame
/// group, after <see cref="CameraExtractSystem"/>, whose camera it reads from <see cref="RenderView"/>). The source
/// decides both, blending included. Reads and writes no components.
/// </summary>
public sealed class AtmosphereSystem : BaseSystem<World, FrameTime>, IFrameUpdateSystem
{
    private readonly RenderView _view;
    private readonly IAtmosphereSource _source;

    public AtmosphereSystem(World world, RenderView view, IAtmosphereSource source)
        : base(world)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(source);
        _view = view;
        _source = source;
    }

    public override void Update(in FrameTime frame)
    {
        var position = _view.Camera.Position;
        var atmosphere = _source.At(position);
        _view.SetAtmosphere(in atmosphere);
        var look = _source.LookAt(position);
        _view.SetLook(in look);
    }
}
