using Itera.Engine.Input;

namespace Itera.Engine.UI.Menu;

/// <summary>
/// The run's menu: the main menu while no match is played, and the menu over a match (Escape opens it when nothing of
/// the game's takes Escape first, <see cref="IEscapeHandler"/>). It is a UI layer drawn over the match's layers and
/// under the console, and <see cref="InputService"/> sends it what a menu takes (<see cref="IMenuInput"/>): while it is
/// open, the game gets no input, and an offline match waits when <c>Engine:Ui:MenuPausesOffline</c> is on. The engine's
/// is <see cref="MenuLayer"/>; a game swaps it for its own by registering an <see cref="IMenu"/> singleton after
/// <c>AddIteraEngine</c>. A menu of a game's own reaches the run through the engine's public services:
/// <c>MatchControl</c> and <c>MatchCommands</c> (start a match, leave it, its phase, why the last one ended),
/// <c>WindowService.RequestExit</c> (quit), <c>PlayerSettings</c>, <c>SteamFriendList</c> (the friends hosting; set
/// <c>Watching</c> while it lists them), <c>JoinRequests</c> (a friend's Steam join during a match), and
/// <c>SaveFiles</c>; it reads its pointer from <see cref="IFrameInput.MenuPointer"/>. Main thread.
/// </summary>
public interface IMenu : IUiLayer, IMenuInput
{
    /// <summary>Over the match's layers (the HUD); the console is drawn over it.</summary>
    bool IUiLayer.AboveMatch => true;
}
