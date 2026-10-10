using Game.WorldGeneration;
using Itera.Engine.Maths;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;

namespace Game;

/// <summary>
/// The benchmark's views of the world (<c>Engine:Benchmark</c>, <c>tools/benchmark/</c>): the start pose level, a little
/// lower, down at the ground, and up at the sky; the world's middle from 40 m over it; and the hills from 200 m out
/// along +x at a person's eye height, looking back. Each eye height is over the ground (or a boulder) there, so the views
/// hold for every seed. A game changes them as its world grows; runs of different views do not compare.
/// </summary>
public static class BenchmarkViews
{
    /// <summary>How far over the ground the view from above stands, in metres.</summary>
    public const float HighMetres = 40;

    /// <summary>How far along +x the far view stands, in metres.</summary>
    public const float FarMetres = 200;

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
            new("far", new WorldPoint(FarMetres, Ground(FarMetres, 0, seed) + EyeMetres, 0), MathF.PI / 2, -0.05f),
        ];

    private static double Ground(double x, double z, ulong seed) =>
        GameGenerator.SurfaceHeight(
            (int)(x * VoxelConstants.CentimetresPerMetre),
            (int)(z * VoxelConstants.CentimetresPerMetre),
            seed
        ) / (double)VoxelConstants.CentimetresPerMetre;
}
