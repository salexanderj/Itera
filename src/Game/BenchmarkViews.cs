using Game.WorldGeneration;
using Itera.Engine.Maths;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;

namespace Game;

/// <summary>
/// The benchmark's views of the world (<c>Engine:Benchmark</c>, <c>tools/benchmark/</c>): the start pose level, a little
/// lower, down at the ground, and up at the sky; the world's middle from 40 m over it; and the hills from 200 m and from
/// 3,500 m out along +x (in the broad relief, far from the middle's origin) at a person's eye height, looking back, each
/// moved on along +x to a spot no object stands at (<see cref="GameForests.ClearSpot"/>); and a forest's inside at a
/// person's eye height and from 40 m over it (<see cref="GameForests.ForestSpot"/>). Each eye height is over the ground
/// (or a boulder) there, so the views hold for every seed. A game changes them as its world grows; runs of different views do not compare.
/// </summary>
public static class BenchmarkViews
{
    /// <summary>How far over the ground the view from above stands, in metres.</summary>
    public const float HighMetres = 40;

    /// <summary>How far along +x the far view stands, in metres.</summary>
    public const float FarMetres = 200;

    /// <summary>How far along +x the distant view stands, in metres.</summary>
    public const float DistantMetres = 3_500;

    /// <summary>A person's eye height over the ground, in metres.</summary>
    public const float EyeMetres = 1.6f;

    /// <summary>
    /// The view named <paramref name="name"/> (any case) among <paramref name="views"/>, as <c>tp</c> takes a place: eye,
    /// yaw, and pitch; null for a name none has.
    /// </summary>
    public static (WorldPoint Eye, float Yaw, float Pitch)? Place(BenchmarkView[] views, string name)
    {
        ArgumentNullException.ThrowIfNull(views);
        foreach (var view in views)
        {
            if (string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return (view.Position, view.Yaw, view.Pitch);
            }
        }

        return null;
    }

    /// <summary>The views of the world of <paramref name="seed"/> from <paramref name="start"/>.</summary>
    public static BenchmarkView[] For(StartPose start, ulong seed) =>
        [
            new("start", start.Eye, start.Yaw, 0),
            new("start-low", start.Eye, start.Yaw, -0.12f),
            new("down", start.Eye, start.Yaw, -0.6f),
            new("up", start.Eye, start.Yaw, 0.45f),
            new("high", new WorldPoint(0, Ground(0, 0, seed) + HighMetres, 0), start.Yaw, -0.3f),
            // Yaw +pi/2 looks along -x, back towards the middle.
            new("far", Clear(FarMetres, seed), MathF.PI / 2, -0.05f),
            new("distant", Clear(DistantMetres, seed), MathF.PI / 2, -0.05f),
            .. Forest(seed),
        ];

    /// <summary>A person's eye over the first spot from <paramref name="metres"/> out along +x with no object within reach.</summary>
    private static WorldPoint Clear(float metres, ulong seed)
    {
        var (x, z) = new GameForests().ClearSpot((int)(metres * VoxelConstants.CentimetresPerMetre), 0, seed);
        double metresX = x / (double)VoxelConstants.CentimetresPerMetre,
            metresZ = z / (double)VoxelConstants.CentimetresPerMetre;
        return new WorldPoint(metresX, Ground(metresX, metresZ, seed) + EyeMetres, metresZ);
    }

    /// <summary>
    /// The views in a forest (<see cref="GameForests.ForestSpot"/>): at a person's eye height among the trees, and from
    /// <see cref="HighMetres"/> over them looking down into the forest, both along -z.
    /// </summary>
    private static BenchmarkView[] Forest(ulong seed)
    {
        var (x, z) = new GameForests().ForestSpot(seed);
        double metresX = x / (double)VoxelConstants.CentimetresPerMetre,
            metresZ = z / (double)VoxelConstants.CentimetresPerMetre;
        var ground = Ground(metresX, metresZ, seed);
        return
        [
            new("forest", new WorldPoint(metresX, ground + EyeMetres, metresZ), 0, -0.05f),
            new("forest-high", new WorldPoint(metresX, ground + HighMetres, metresZ), 0, -0.8f),
        ];
    }

    private static double Ground(double x, double z, ulong seed) =>
        GameGenerator.SurfaceHeight(
            (int)Math.Round(x * VoxelConstants.CentimetresPerMetre),
            (int)Math.Round(z * VoxelConstants.CentimetresPerMetre),
            seed
        ) / (double)VoxelConstants.CentimetresPerMetre;
}
