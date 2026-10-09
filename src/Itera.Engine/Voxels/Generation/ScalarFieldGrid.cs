using Itera.Engine.Maths;

namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// A 2D scalar field over the world's x and z (in centimetres) worked out on a grid fixed to the world, over a box, and
/// read with bilinear filtering: for choices made at every ground point of a column, where working the field out exactly
/// would cost several noises a point. The grid's points lie at whole multiples of the cell, so a point gets the same
/// value whatever box holds it, and every level of detail agrees. Not thread safe; use one for each worker.
/// </summary>
public sealed class ScalarFieldGrid
{
    private readonly int _cell;
    private readonly Func<int, int, double> _field;
    private double[] _values = new double[64];
    private int _firstX;
    private int _firstZ;
    private int _width;

    /// <summary>
    /// Makes a grid with points <paramref name="cell"/> centimetres apart over <paramref name="field"/>, which takes
    /// (x, z) in centimetres and must give the same value for the same point on every call.
    /// </summary>
    public ScalarFieldGrid(int cell, Func<int, int, double> field)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cell);
        ArgumentNullException.ThrowIfNull(field);
        _cell = cell;
        _field = field;
    }

    /// <summary>The spacing of the grid's points, in centimetres.</summary>
    public int Cell => _cell;

    /// <summary>
    /// Works out the grid points around the box of points from (<paramref name="minX"/>, <paramref name="minZ"/>) to
    /// (<paramref name="maxX"/>, <paramref name="maxZ"/>), in centimetres. It reuses its array when the box fits.
    /// </summary>
    public void Fill(int minX, int maxX, int minZ, int maxZ)
    {
        _firstX = DeterministicMath.FloorDivide(minX, _cell);
        _firstZ = DeterministicMath.FloorDivide(minZ, _cell);
        _width = DeterministicMath.FloorDivide(maxX, _cell) + 2 - _firstX;
        var depth = DeterministicMath.FloorDivide(maxZ, _cell) + 2 - _firstZ;
        if (_values.Length < _width * depth)
        {
            _values = new double[_width * depth];
        }

        for (var z = 0; z < depth; z++)
        {
            for (var x = 0; x < _width; x++)
            {
                _values[z * _width + x] = _field((_firstX + x) * _cell, (_firstZ + z) * _cell);
            }
        }
    }

    /// <summary>The field at (<paramref name="x"/>, <paramref name="z"/>) in centimetres, inside the box last filled.</summary>
    public double At(int x, int z)
    {
        var cellX = DeterministicMath.FloorDivide(x, _cell);
        var cellZ = DeterministicMath.FloorDivide(z, _cell);
        var fx = (x - cellX * _cell) / (double)_cell;
        var fz = (z - cellZ * _cell) / (double)_cell;
        var i = (cellZ - _firstZ) * _width + cellX - _firstX;
        var near = _values[i] + (_values[i + 1] - _values[i]) * fx;
        var far = _values[i + _width] + (_values[i + _width + 1] - _values[i + _width]) * fx;
        return near + (far - near) * fz;
    }
}
