using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Rendering;

namespace Itera.MeshSample;

/// <summary>
/// The yard camera, run-wide: an image view (<see cref="RenderViews"/>, <see cref="Width"/> by <see cref="Height"/>,
/// drawn at most every <see cref="EveryFrames"/> frames) from a fixed camera high over the yard's south-west corner,
/// looking in among the blocks, shown on the yard's screen (<see cref="Yard.ScreenMaterial"/>); declared at the run's start
/// (<see cref="Declare"/>), asked for each frame by <see cref="Systems.YardCameraSystem"/>.
/// </summary>
public sealed class YardCamera
{
    /// <summary>The image view's name.</summary>
    public const string ViewName = "yard camera";

    /// <summary>The view's image, 16:9 as the screen's picture.</summary>
    public const int Width = 384;

    public const int Height = 216;

    /// <summary>The view is drawn at most once in this many frames.</summary>
    public const int EveryFrames = 2;

    /// <summary>Where the camera stands, in metres: 14 m west and 14 m south of the ground's middle, 9 m up.</summary>
    public static WorldPoint Position { get; } = new(-14, 9, 14);

    /// <summary>What it looks at, an absolute point.</summary>
    public static WorldPoint Target { get; } = new(0, 0, -3);

    /// <summary>
    /// The camera: at <see cref="Position"/>, looking at (0, 0, -3), among the blocks, with a vertical field of view of 50
    /// degrees.
    /// </summary>
    public static CameraView CameraIn(WorldOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return Looking(origin.ToLocal(Position), origin.ToLocal(Target));
    }

    /// <summary>The image view's index, or -1 before <see cref="Declare"/>.</summary>
    public int View { get; private set; } = -1;

    /// <summary>Declares the view and shows it on the screen. At the run's <c>game</c> step, once.</summary>
    public void Declare(RenderViews views)
    {
        ArgumentNullException.ThrowIfNull(views);
        View = views.Declare(ViewName, Width, Height, EveryFrames);
        views.ShowOnMaterial(View, Yard.Screen, Yard.ScreenMaterial);
    }

    /// <summary>A perspective camera at <paramref name="from"/> looking at <paramref name="at"/>, level (no roll).</summary>
    private static CameraView Looking(Vector3 from, Vector3 at)
    {
        var look = at - from;
        // Yaw 0 looks along -z and turns left with yaw; pitch tilts it up (as CameraPose).
        var yaw = MathF.Atan2(-look.X, -look.Z);
        var pitch = MathF.Atan2(look.Y, MathF.Sqrt((look.X * look.X) + (look.Z * look.Z)));
        return new CameraView(from, Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0), 50 * MathF.PI / 180, 0.1f);
    }
}
