using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Ecs;
using Itera.Engine.Rendering;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Where the air, sky, and sunlight differ: the <see cref="Atmosphere"/> at a place in the world. A game implements it
/// (a map of regions, biomes, a height) and gives it to <see cref="AtmosphereSystem"/>. Called once a frame on the main
/// thread; it allocates nothing.
/// </summary>
public interface IAtmosphereSource
{
    /// <summary>The atmosphere at <paramref name="positionMetres"/>, which may lie far outside the world.</summary>
    Atmosphere At(Vector3 positionMetres);
}

/// <summary>
/// Sets the frame's sky, sunlight, and air from an <see cref="IAtmosphereSource"/> at the camera (frame group, after
/// <see cref="CameraExtractSystem"/>, whose camera it reads from <see cref="RenderView"/>). The source decides the look,
/// blending included. Reads and writes no components.
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
        var atmosphere = _source.At(_view.Camera.Position);
        _view.SetAtmosphere(in atmosphere);
    }
}
