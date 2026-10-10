using Itera.Engine.Assets;

namespace Game;

/// <summary>
/// The game's models: glTF files in <c>Models/</c>, copied next to the executable (credits in
/// <c>Models/SOURCES.md</c>), declared at the run's start (<see cref="GameRunStartup"/>) and read at the engine's "mesh
/// assets" step. A system finds one's index by its name (<c>MeshAssets.Find</c>) once.
/// </summary>
public static class GameModels
{
    /// <summary>Khronos's Fox sample, in centimetres: 0.25 m wide, 0.79 m tall, 1.55 m long, its head along +z.</summary>
    public const string Fox = "fox";

    /// <summary>Declares every model of the game.</summary>
    public static void Declare(MeshAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        assets.Declare(Fox, Path.Combine("Models", "Fox", "Fox.gltf"), metresPerUnit: 0.01f);
    }
}
