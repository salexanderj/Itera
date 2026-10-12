using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Rendering.Voxels;

namespace Itera.MeshSample;

/// <summary>
/// The yard's physics corner, west of the start (<see cref="Systems.PhysicsDemoSystem"/>): an ice ramp whose rubber
/// balls roll down it, through a gate pane that stops players but not them (their layer, <see cref="BallsLayer"/>, does
/// not touch the gate's, <see cref="GateLayer"/>), and through a trigger zone (a chime as anything comes in, the player
/// too) against a row of stone blocks; a rubber pad that rubber balls bounce on, a clang at each landing as loud as it was
/// fast (they report their impacts); two balls that float (no gravity) and two that fall at 0.3 of it. The run's
/// materials, layers, and the balls' voxel materials are registered at its start (<see cref="Register"/>).
/// </summary>
public static class PhysicsDemo
{
    /// <summary>The ramp's material: friction 0.03, no bounce. Characters on it grip at 0.03 of their acceleration.</summary>
    public const string Ice = "ice";

    /// <summary>The pad's and the balls' material: friction 1, bounciness 0.9.</summary>
    public const string Rubber = "rubber";

    /// <summary>The gate pane's layer.</summary>
    public const string GateLayer = "gate";

    /// <summary>The demo's balls' layer, which does not touch the gate's.</summary>
    public const string BallsLayer = "balls";

    /// <summary>The voxel material of the rubber balls (rubber; drawn red).</summary>
    public const byte RubberBall = 20;

    /// <summary>The voxel material of the balls that float (default; drawn blue).</summary>
    public const byte FloatingBall = 21;

    /// <summary>The voxel material of the balls that fall slowly (rubber; drawn green).</summary>
    public const byte SlowBall = 22;

    /// <summary>The demo's balls' radius, centimetres.</summary>
    public const int BallRadius = 15;

    /// <summary>How often a rubber ball is let go at the ramp's top and over the pad, seconds.</summary>
    public const float ReleaseSeconds = 5;

    /// <summary>The most released balls kept: past them, the oldest goes.</summary>
    public const int MostReleased = 8;

    /// <summary>The share of gravity the slow balls fall by.</summary>
    public const float SlowGravity = 0.3f;

    /// <summary>The ice ramp's foot: its low west end; it rises 6 m east at 20 degrees, 3 m wide along +z.</summary>
    public static readonly WorldPoint RampPlace = new(-10, 0, 8);

    /// <summary>Where a ball is let go on the ramp, near its top.</summary>
    public static readonly WorldPoint RampRelease = new(-4.6, 2.3, 9.5);

    /// <summary>The gate pane's low corner: 0.1 m thick across x, 2.5 m tall, 4 m along z.</summary>
    public static readonly WorldPoint GatePlace = new(-12.05, 0, 7.5);

    /// <summary>The trigger zone's low corner; it is <see cref="ZoneSize"/>.</summary>
    public static readonly WorldPoint ZonePlace = new(-16, 0, 7.5);

    /// <summary>The trigger zone's size.</summary>
    public static readonly Vector3 ZoneSize = new(3, 2.5f, 4);

    /// <summary>The trigger zone's middle.</summary>
    public static WorldPoint ZoneMiddle => ZonePlace.Plus(ZoneSize.X / 2, ZoneSize.Y / 2, ZoneSize.Z / 2);

    /// <summary>The rubber pad's low corner: 3 m square, 15 cm thick.</summary>
    public static readonly WorldPoint PadPlace = new(-8, 0, 13);

    /// <summary>Where a ball is let go over the pad.</summary>
    public static readonly WorldPoint PadRelease = new(-6.5, 3, 14.5);

    /// <summary>Where the floating balls hang, to the start's left.</summary>
    public static IReadOnlyList<WorldPoint> FloatingPlaces { get; } = [new(-4, 1.4, 4.5), new(-3, 1.9, 3.5)];

    /// <summary>Where the slow balls start, over the pad.</summary>
    public static IReadOnlyList<WorldPoint> SlowPlaces { get; } = [new(-7.2, 5, 14), new(-5.8, 5, 15.2)];

    /// <summary>The back stop's blocks, 1 m cubes west of the zone.</summary>
    public static IReadOnlyList<WorldPoint> BackStop { get; } =
    [new(-19, 0, 7.5), new(-19, 0, 8.5), new(-19, 0, 9.5), new(-19, 0, 10.5)];

    /// <summary>
    /// Registers the demo's physics materials and layers (the balls' apart from the gate's), and the balls' voxel
    /// materials' physics materials. Once a run, at its start.
    /// </summary>
    public static void Register(PhysicsMaterials materials, PhysicsLayers layers)
    {
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(layers);
        materials.Register(Ice, new PhysicsMaterial(0.03f, 0));
        var rubber = materials.Register(Rubber, new PhysicsMaterial(1, 0.9f));
        materials.MapVoxel(RubberBall, rubber);
        materials.MapVoxel(SlowBall, rubber);
        var gate = layers.Register(GateLayer);
        var balls = layers.Register(BallsLayer);
        layers.SetTouches(balls, gate, false);
    }
}

/// <summary>
/// The sample's voxel colours: its models are the loose bodies alone, grey but for the physics corner's balls (rubber
/// red, floating blue, slow green).
/// </summary>
public sealed class SamplePalette : IVoxelPalette
{
    public VoxelLook Look(byte material) =>
        material switch
        {
            PhysicsDemo.RubberBall => VoxelLook.Plain(new Vector3(0.75f, 0.16f, 0.12f)),
            PhysicsDemo.FloatingBall => VoxelLook.Plain(new Vector3(0.2f, 0.45f, 0.85f)),
            PhysicsDemo.SlowBall => VoxelLook.Plain(new Vector3(0.3f, 0.7f, 0.3f)),
            _ => VoxelLook.Plain(new Vector3(0.5f)),
        };
}
