using Itera.Engine.Assets;
using Itera.Engine.Audio;
using Itera.Engine.Matches;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Systems;

namespace Itera.MeshSample;

/// <summary>
/// The sample's run-wide registries, at the engine's <c>game</c> step: the movement keys, the engine's stock particle
/// effects, the starter's fox (read from <c>Models/Fox/</c>), the yard's meshes built in code (<see cref="Yard"/>), and the
/// yard camera's image view shown on its screen (<see cref="YardCamera"/>), the sounds and music read from its
/// files (<see cref="SampleSounds"/>), and the physics corner's materials and layers (<see cref="PhysicsDemo"/>).
/// </summary>
public sealed class SampleRunStartup(
    MovementKeys movement,
    StockEffects effects,
    MeshAssets assets,
    RenderViews views,
    YardCamera camera,
    AudioService audio,
    PhysicsMaterials materials,
    PhysicsLayers layers
) : IRunStartup
{
    /// <summary>The fox's name among the run's mesh assets.</summary>
    public const string Fox = "fox";

    public void Start()
    {
        _ = (movement, effects);
        assets.Declare(Fox, Path.Combine("Models", "Fox", "Fox.gltf"), metresPerUnit: 0.01f);
        Yard.Add(assets);
        camera.Declare(views);
        SampleSounds.Register(audio);
        PhysicsDemo.Register(materials, layers);
    }
}
