using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels.Generation;

/// <summary>
/// The rules of <see cref="HillsGenerator"/> worked out one voxel at a time, from the ground's heights at each point and
/// no cache: the slow reference the generator's fast fills are checked against.
/// </summary>
internal static class HillsReference
{
    private const int Size = VoxelConstants.ChunkSize;

    /// <summary>Works out a chunk's materials and densities into the two arrays of 128^3.</summary>
    public static void Chunk(VoxelGrid grid, ulong seed, ChunkKey chunk, byte[] voxels, byte[] densities, bool features)
    {
        var level = chunk.Level;
        var edge = grid.Edge(level);
        var origin = chunk.LevelOrigin;
        const int ringed = Size + 2;
        var tops = new int[ringed * ringed];
        var heights = new double[Size * Size];
        for (var z = 0; z < ringed; z++)
        {
            for (var x = 0; x < ringed; x++)
            {
                var height = HillsGenerator.Height(
                    grid.SampleAt(origin.X + x - 1, level),
                    grid.SampleAt(origin.Z + z - 1, level),
                    seed
                );
                tops[z * ringed + x] = (int)Math.Floor(height);
                if (x is >= 1 and <= Size && z is >= 1 and <= Size)
                {
                    heights[(z - 1) * Size + x - 1] = height;
                }
            }
        }

        var surfaceDepth = Math.Max(5, edge);
        for (var z = 0; z < Size; z++)
        {
            for (var x = 0; x < Size; x++)
            {
                var centre = (z + 1) * ringed + x + 1;
                var top = tops[centre];
                var lowest = Math.Min(
                    Math.Min(top, Math.Min(tops[centre - 1], tops[centre + 1])),
                    Math.Min(tops[centre - ringed], tops[centre + ringed])
                );
                var height = heights[z * Size + x];
                var surface = height < HillsGenerator.SandBelow ? HillsGenerator.Sand : HillsGenerator.Grass;
                for (var y = 0; y < Size; y++)
                {
                    var sampleY = grid.SampleAt(origin.Y + y, level);
                    byte material;
                    if (sampleY >= top)
                    {
                        material = VoxelConstants.Air;
                    }
                    else
                    {
                        var depth = lowest - 1 - sampleY;
                        material =
                            depth < surfaceDepth ? surface
                            : depth < 100 ? HillsGenerator.Dirt
                            : HillsGenerator.Stone;
                    }

                    var low = (double)((origin.Y + y) * edge);
                    var share = (height - low) / edge;
                    var density = (byte)(
                        share <= 0 ? 0
                        : share >= 1 ? 255
                        : Math.Floor(share * 255 + 0.5)
                    );
                    var index = x + Size * (y + Size * z);
                    voxels[index] = material;
                    densities[index] = VoxelDensity.Fit(material, density);
                }
            }
        }

        if (!features)
        {
            return;
        }

        var minX = grid.SampleAt(origin.X, level);
        var maxX = grid.SampleAt(origin.X + Size - 1, level);
        var minZ = grid.SampleAt(origin.Z, level);
        var maxZ = grid.SampleAt(origin.Z + Size - 1, level);
        foreach (var ball in HillsGenerator.BallsNear(seed, minX, maxX, minZ, maxZ))
        {
            for (var z = 0; z < Size; z++)
            {
                var sampleZ = grid.SampleAt(origin.Z + z, level);
                if (Math.Abs(sampleZ - ball.Z) > ball.Radius)
                {
                    continue;
                }

                for (var y = 0; y < Size; y++)
                {
                    var sampleY = grid.SampleAt(origin.Y + y, level);
                    if (Math.Abs(sampleY - ball.Y) > ball.Radius)
                    {
                        continue;
                    }

                    for (var x = 0; x < Size; x++)
                    {
                        if (ball.Contains(grid.SampleAt(origin.X + x, level), sampleY, sampleZ))
                        {
                            voxels[x + Size * (y + Size * z)] = ball.Material;
                        }
                    }
                }
            }
        }

        // Where a feature made a voxel solid or air against its ground density, it is whole.
        for (var i = 0; i < voxels.Length; i++)
        {
            if ((voxels[i] == VoxelConstants.Air) == (densities[i] >= VoxelDensity.Solid))
            {
                densities[i] = voxels[i] == VoxelConstants.Air ? (byte)0 : (byte)255;
            }
        }
    }
}
