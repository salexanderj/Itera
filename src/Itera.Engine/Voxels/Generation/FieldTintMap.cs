using Itera.Engine.Rendering.Voxels;

namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// A tint map (<see cref="IVoxelTintMap"/>) whose texels come from a field of x and z: a texel holds the field at its
/// centre, 0 to 1 as 0 to 255. The renderer blends the texels around a hit, so materials shift between their colour and
/// their tint colour across the world close to the generator's own choices, if the generator reads the same field.
/// </summary>
/// <remarks>
/// The field is called only in <see cref="Write"/>, when the renderer starts, so a field that reads the world's seed
/// may take it from state that is settled by then: in a session the seed is known only after the first start-up step.
/// </remarks>
public sealed class FieldTintMap : IVoxelTintMap
{
    private readonly Func<int, int, double> _field;

    /// <summary>
    /// Makes the map. <paramref name="field"/> takes (x, z) in centimetres and gives 0 to 1; values outside that range
    /// are clamped. The map's corner lies at (<paramref name="originX"/>, <paramref name="originZ"/>) centimetres, and a
    /// texel is <c>1 &lt;&lt; texelShift</c> centimetres across (at least 2, so a texel has a centre on a whole
    /// centimetre); 256 texels of 512 cm cover 1310 m.
    /// </summary>
    public FieldTintMap(Func<int, int, double> field, int originX, int originZ, int texelShift = 9)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfLessThan(texelShift, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(texelShift, 20);
        _field = field;
        OriginX = originX;
        OriginZ = originZ;
        TexelShift = texelShift;
    }

    public int TexelShift { get; }

    public int OriginX { get; }

    public int OriginZ { get; }

    public void Write(Span<byte> texels)
    {
        const int size = IVoxelTintMap.Size;
        var texel = 1 << TexelShift;
        for (var z = 0; z < size; z++)
        {
            var centreZ = OriginZ + z * texel + texel / 2;
            for (var x = 0; x < size; x++)
            {
                var centreX = OriginX + x * texel + texel / 2;
                texels[z * size + x] = (byte)Math.Round(Math.Clamp(_field(centreX, centreZ), 0, 1) * 255);
            }
        }
    }
}
