using Itera.Engine.Assets;
using Itera.Engine.Matches;
using Itera.Engine.Particles;
using Itera.Engine.Systems;

namespace Game;

/// <summary>
/// The game's run-wide registries, made once at the engine's <c>game</c> step, before the mods add theirs: the movement
/// keys first, so the Keybindings tab lists them before the game's own, the engine's stock particle effects
/// (<c>flare</c>, <c>sparks</c>, <c>embers</c>, <c>debris</c>, <c>smoke</c>, <c>dust-puff</c>, <c>grit</c>), and the
/// game's models (<see cref="GameModels"/>). A game adds its own keys, recipes, models, and registries here.
/// </summary>
public sealed class GameRunStartup(MovementKeys movement, StockEffects effects, MeshAssets assets) : IRunStartup
{
    public void Start()
    {
        // Resolving these made them; holding them here keeps the reason they are asked for in one place.
        _ = (movement, effects);
        GameModels.Declare(assets);
    }
}
