namespace Itera.Engine.UI;

/// <summary>
/// A layer of one match (the game's HUD), made in the match's scope when the match starts and dropped when it ends
/// (<see cref="UISurface.SetMatchLayers"/>). Drawn over the run's layers but those above the match
/// (<see cref="IUiLayer.AboveMatch"/>), in the order the match's layers were registered.
/// </summary>
public interface IMatchUiLayer : IUiLayer;
