using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Input;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Itera.Engine.Systems;
using Itera.Modding;

namespace Game.Systems;

/// <summary>
/// Draws the minimap (frame group, after the camera's systems): its key (<see cref="Minimap.Key"/>, M) turns its switch
/// on and off, unless the player's input is blocked; while it is on, and not in the benchmark, it asks for the minimap's
/// image view each frame (<see cref="RenderViews.Draw"/>) from an orthographic camera at the local player's eye looking
/// straight down, north up, <c>Game:Minimap:Metres</c> across, and tells the minimap's layer it shows and where the player
/// looks (<see cref="Minimap.Shown"/>, <see cref="Minimap.Yaw"/>). Reads the player's command, mode, position, and size;
/// writes no components.
/// </summary>
public sealed class MinimapSystem(
    World world,
    Minimap minimap,
    RenderViews views,
    PlayerSettings settings,
    IFrameInput input,
    PlayerInputBlock block,
    MinimapOptions options,
    bool benchmark
) : BaseSystem<World, FrameTime>(world), IFrameUpdateSystem
{
    public override void Initialize() =>
        EcsChecks.RequireAll<PlayerCommand, PlayerMode, CharacterPosition, CharacterSize>(in ToolAim.Players);

    public override void Update(in FrameTime frame)
    {
        minimap.Shown = false;
        if (minimap.View < 0)
        {
            return;
        }

        if (!block.Blocked && settings.Keys.WasPressed(in input.Frame, minimap.Key))
        {
            settings.SetSwitch(minimap.Switch, !settings.IsOn(minimap.Switch));
        }

        if (benchmark || !settings.IsOn(minimap.Switch))
        {
            return;
        }

        var player = ToolAim.FindPlayer(World);
        if (!player.Found)
        {
            return;
        }

        // The eye is the middle of what an orthographic view shows; its box reaches the view distance up and down.
        var camera = new CameraView(
            player.Eye,
            Minimap.LookingDown,
            0,
            float.NaN,
            CameraProjection.Orthographic,
            (float)options.Metres,
            float.NaN
        );
        views.Draw(minimap.View, in camera);
        minimap.Shown = true;
        minimap.Yaw = player.Yaw;
    }
}
