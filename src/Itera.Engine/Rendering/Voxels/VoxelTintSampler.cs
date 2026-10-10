using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.Voxels;

/// <summary>
/// Reads the tint map (<see cref="IVoxelTintMap"/>) at a point, as <c>meshTintAt</c> in <c>mesh-data.glsl</c> does: the
/// four texels around it blended, and texels past the edge repeating the edge. Models take their tint from it where
/// they stand, so a character or a loose body is tinted as the ground under it is. The texels are read
/// once, when it is made. Any thread; allocates nothing after construction.
/// </summary>
public sealed class VoxelTintSampler
{
    private const int Size = IVoxelTintMap.Size;

    private readonly byte[] _texels = new byte[Size * Size];
    private readonly int _shift;
    private readonly int _originX;
    private readonly int _originZ;

    public VoxelTintSampler(IVoxelTintMap map)
    {
        map.Write(_texels);
        _shift = map.TexelShift;
        _originX = map.OriginX;
        _originZ = map.OriginZ;
    }

    /// <summary>
    /// The tint, 0 to 1, at the absolute point <paramref name="point"/> (its x and z; a local position with the match's
    /// origin, <c>WorldOrigin.ToWorld</c>).
    /// </summary>
    public float At(in WorldPoint point)
    {
        // Far enough outside any map that every point converts to an int of texels; the edge repeats there.
        const double most = 1e6;
        var x = Math.Clamp(point.X, -most, most) * VoxelConstants.CentimetresPerMetre;
        var z = Math.Clamp(point.Z, -most, most) * VoxelConstants.CentimetresPerMetre;
        var texel = (double)(1 << _shift);
        var px = (x - _originX) / texel - 0.5;
        var pz = (z - _originZ) / texel - 0.5;
        int tx = (int)Math.Floor(px),
            tz = (int)Math.Floor(pz);
        float fx = (float)(px - tx),
            fz = (float)(pz - tz);
        var near = Lerp(Texel(tx, tz), Texel(tx + 1, tz), fx);
        var far = Lerp(Texel(tx, tz + 1), Texel(tx + 1, tz + 1), fx);
        return Lerp(near, far, fz);
    }

    private float Texel(int x, int z) => _texels[Math.Clamp(z, 0, Size - 1) * Size + Math.Clamp(x, 0, Size - 1)] / 255f;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
