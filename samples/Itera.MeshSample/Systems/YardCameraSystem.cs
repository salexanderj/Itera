using Arch.Core;
using Arch.System;
using Itera.Engine.Ecs;
using Itera.Engine.Maths;
using Itera.Engine.Rendering;
using Itera.Modding;

namespace Itera.MeshSample.Systems;

/// <summary>
/// Asks for the yard camera's image view every frame (frame group; <see cref="RenderViews.Draw"/>), from its fixed
/// camera (<see cref="YardCamera.CameraIn"/>), so the yard's screen shows it. Reads and writes no components.
/// </summary>
public sealed class YardCameraSystem(World world, YardCamera camera, RenderViews views, WorldOrigin origin)
    : BaseSystem<World, FrameTime>(world),
        IFrameUpdateSystem
{
    public override void Update(in FrameTime frame)
    {
        if (camera.View >= 0)
        {
            views.Draw(camera.View, YardCamera.CameraIn(origin));
        }
    }
}
