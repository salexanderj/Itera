using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>
/// The voxels of an object kind (a tree, a bush, a rock) as a game builds them before registering the kind
/// (<see cref="VoxelObjectKinds.Register"/>): a box of voxels <see cref="UnitCentimetres"/> across, each with a material
/// and a density (<see cref="VoxelDensity"/>: how much of it is solid), and the point the kind stands on
/// (<see cref="AnchorCentimetres"/>), which a placement puts on its spot. The fills write densities from the shape's
/// distance to each voxel's centre, so a smooth shape meshes without voxel steps, and join shapes by the larger density
/// (a voxel takes the material of the shape that fills it most). Positions are centimetres from the box's corner.
/// Every peer must build the same voxels: fill with integer maths, + - * / and square roots, as generators do
/// (<see cref="Maths.DeterministicMath"/>), never <see cref="Math.Sin(double)"/>. Built on one thread; allocates its
/// arrays once.
/// </summary>
public sealed class VoxelObjectShape
{
    /// <summary>The most voxels a shape's box may have along an axis.</summary>
    public const int MostVoxelsAcross = 1024;

    /// <summary>The most voxels a shape's box may hold.</summary>
    public const long MostVoxels = 64L << 20;

    private readonly byte[] _materials;
    private readonly byte[] _densities;

    /// <param name="unitCentimetres">The edge of its voxels: 1 to <see cref="VoxelModels.MostUnitCentimetres"/>.</param>
    /// <param name="size">Its box in voxels: 1 to <see cref="MostVoxelsAcross"/> along each axis, at most <see cref="MostVoxels"/>.</param>
    /// <param name="anchorCentimetres">
    /// The point placed on a placement's spot, in centimetres from the box's corner: for a tree, its trunk's foot.
    /// </param>
    public VoxelObjectShape(int unitCentimetres, VoxelCoord size, VoxelCoord anchorCentimetres)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(unitCentimetres, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(unitCentimetres, VoxelModels.MostUnitCentimetres);
        if (
            size.X is < 1 or > MostVoxelsAcross
            || size.Y is < 1 or > MostVoxelsAcross
            || size.Z is < 1 or > MostVoxelsAcross
            || (long)size.X * size.Y * size.Z > MostVoxels
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                $"A shape is 1 to {MostVoxelsAcross} voxels along each axis and at most {MostVoxels} voxels."
            );
        }

        UnitCentimetres = unitCentimetres;
        Size = size;
        AnchorCentimetres = anchorCentimetres;
        var count = (long)size.X * size.Y * size.Z;
        _materials = new byte[count];
        _densities = new byte[count];
    }

    /// <summary>The edge of its voxels, in centimetres.</summary>
    public int UnitCentimetres { get; }

    /// <summary>Its box, in voxels.</summary>
    public VoxelCoord Size { get; }

    /// <summary>The point placed on a placement's spot, in centimetres from the box's corner.</summary>
    public VoxelCoord AnchorCentimetres { get; }

    /// <summary>The material of voxel (x, y, z); air outside the box.</summary>
    public byte MaterialAt(int x, int y, int z) => Holds(x, y, z) ? _materials[Index(x, y, z)] : VoxelConstants.Air;

    /// <summary>The density of voxel (x, y, z); 0 outside the box.</summary>
    public byte DensityAt(int x, int y, int z) => Holds(x, y, z) ? _densities[Index(x, y, z)] : VoxelDensity.Empty;

    /// <summary>True when (x, y, z) is a voxel of the box.</summary>
    public bool Holds(int x, int y, int z) =>
        (uint)x < (uint)Size.X && (uint)y < (uint)Size.Y && (uint)z < (uint)Size.Z;

    /// <summary>
    /// Sets voxel (x, y, z) to <paramref name="material"/> at <paramref name="density"/>, the density moved into the half
    /// the material allows (<see cref="VoxelDensity.Fit"/>: 128 or more for a solid material, under 128 for air).
    /// </summary>
    public void Set(int x, int y, int z, byte material, byte density)
    {
        CheckVoxel(x, y, z);
        var index = Index(x, y, z);
        _materials[index] = material;
        _densities[index] = VoxelDensity.Fit(material, density);
    }

    /// <summary>Empties voxel (x, y, z): air of no density (a hole in a crown).</summary>
    public void Clear(int x, int y, int z) => Set(x, y, z, VoxelConstants.Air, VoxelDensity.Empty);

    /// <summary>Joins an upright ellipsoid about (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) with these radii, all in centimetres.</summary>
    public void FillEllipsoid(
        double x,
        double y,
        double z,
        double radiusX,
        double radiusY,
        double radiusZ,
        byte material
    )
    {
        CheckSolid(material);
        if (!(radiusX > 0 && radiusY > 0 && radiusZ > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(radiusX), "An ellipsoid's radii are above 0.");
        }

        var reach = Math.Max(radiusX, Math.Max(radiusY, radiusZ)) + UnitCentimetres;
        Fill(
            x - reach,
            y - reach,
            z - reach,
            x + reach,
            y + reach,
            z + reach,
            material,
            (px, py, pz) =>
            {
                // The distance to the surface along the gradient of the ellipsoid's radius: exact on a sphere, close near any
                // ellipsoid's surface, where the density is decided.
                double ux = (px - x) / radiusX,
                    uy = (py - y) / radiusY,
                    uz = (pz - z) / radiusZ;
                var s = Math.Sqrt(ux * ux + uy * uy + uz * uz);
                if (s == 0)
                {
                    return -Math.Min(radiusX, Math.Min(radiusY, radiusZ));
                }

                double gx = ux / radiusX,
                    gy = uy / radiusY,
                    gz = uz / radiusZ;
                return (s - 1) * s / Math.Sqrt(gx * gx + gy * gy + gz * gz);
            }
        );
    }

    /// <summary>
    /// Joins a capsule: every point within <paramref name="radius"/> of the segment from (x0, y0, z0) to (x1, y1, z1), all
    /// in centimetres (a trunk, a branch).
    /// </summary>
    public void FillCapsule(
        double x0,
        double y0,
        double z0,
        double x1,
        double y1,
        double z1,
        double radius,
        byte material
    )
    {
        CheckSolid(material);
        if (!(radius > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "A capsule's radius is above 0.");
        }

        double dx = x1 - x0,
            dy = y1 - y0,
            dz = z1 - z0;
        var length = dx * dx + dy * dy + dz * dz;
        var reach = radius + UnitCentimetres;
        Fill(
            Math.Min(x0, x1) - reach,
            Math.Min(y0, y1) - reach,
            Math.Min(z0, z1) - reach,
            Math.Max(x0, x1) + reach,
            Math.Max(y0, y1) + reach,
            Math.Max(z0, z1) + reach,
            material,
            (px, py, pz) =>
            {
                var t = length > 0 ? Math.Clamp(((px - x0) * dx + (py - y0) * dy + (pz - z0) * dz) / length, 0, 1) : 0;
                double ex = px - (x0 + t * dx),
                    ey = py - (y0 + t * dy),
                    ez = pz - (z0 + t * dz);
                return Math.Sqrt(ex * ex + ey * ey + ez * ez) - radius;
            }
        );
    }

    /// <summary>Joins the box from (x0, y0, z0) to (x1, y1, z1), in centimetres.</summary>
    public void FillBox(double x0, double y0, double z0, double x1, double y1, double z1, byte material)
    {
        CheckSolid(material);
        if (!(x1 > x0 && y1 > y0 && z1 > z0))
        {
            throw new ArgumentException("A box's far corner lies past its near one on every axis.", nameof(x1));
        }

        double cx = (x0 + x1) / 2,
            cy = (y0 + y1) / 2,
            cz = (z0 + z1) / 2;
        double hx = (x1 - x0) / 2,
            hy = (y1 - y0) / 2,
            hz = (z1 - z0) / 2;
        var reach = UnitCentimetres;
        Fill(
            x0 - reach,
            y0 - reach,
            z0 - reach,
            x1 + reach,
            y1 + reach,
            z1 + reach,
            material,
            (px, py, pz) =>
            {
                double qx = Math.Abs(px - cx) - hx,
                    qy = Math.Abs(py - cy) - hy,
                    qz = Math.Abs(pz - cz) - hz;
                double ox = Math.Max(qx, 0),
                    oy = Math.Max(qy, 0),
                    oz = Math.Max(qz, 0);
                return Math.Sqrt(ox * ox + oy * oy + oz * oz) + Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0);
            }
        );
    }

    /// <summary>The voxels' materials, index <c>x + size.X * (y + size.Y * z)</c>.</summary>
    internal ReadOnlySpan<byte> Materials => _materials;

    /// <summary>The voxels' densities, at the materials' indices.</summary>
    internal ReadOnlySpan<byte> Densities => _densities;

    /// <summary>
    /// Writes a shape whose signed distance (centimetres, below 0 inside) <paramref name="distance"/> gives at each voxel
    /// centre within the box from (x0, y0, z0) to (x1, y1, z1): the voxel's share inside is taken as one half less the
    /// distance in voxels, and the larger density wins.
    /// </summary>
    private void Fill(
        double x0,
        double y0,
        double z0,
        double x1,
        double y1,
        double z1,
        byte material,
        Func<double, double, double, double> distance
    )
    {
        var unit = (double)UnitCentimetres;
        var (fx, lx) = Range(x0, x1, Size.X);
        var (fy, ly) = Range(y0, y1, Size.Y);
        var (fz, lz) = Range(z0, z1, Size.Z);
        for (var z = fz; z < lz; z++)
        {
            var pz = (z + 0.5) * unit;
            for (var y = fy; y < ly; y++)
            {
                var py = (y + 0.5) * unit;
                for (var x = fx; x < lx; x++)
                {
                    var share = Math.Clamp(0.5 - distance((x + 0.5) * unit, py, pz) / unit, 0, 1);
                    var density = (byte)Math.Round(share * VoxelDensity.Full);
                    var index = Index(x, y, z);
                    if (density <= _densities[index])
                    {
                        continue;
                    }

                    _densities[index] = density;
                    _materials[index] = density >= VoxelDensity.Solid ? material : VoxelConstants.Air;
                }
            }
        }
    }

    // The voxels whose centres may lie between the two centimetres, within the box.
    private (int First, int End) Range(double from, double to, int count) =>
        (
            Math.Clamp((int)Math.Floor(from / UnitCentimetres), 0, count),
            Math.Clamp((int)Math.Ceiling(to / UnitCentimetres) + 1, 0, count)
        );

    private long Index(int x, int y, int z) => x + (long)Size.X * (y + (long)Size.Y * z);

    private void CheckVoxel(int x, int y, int z)
    {
        if (!Holds(x, y, z))
        {
            throw new ArgumentOutOfRangeException(nameof(x), (x, y, z), "The voxel lies outside the shape's box.");
        }
    }

    private static void CheckSolid(byte material)
    {
        if (material == VoxelConstants.Air)
        {
            throw new ArgumentException("A fill of air fills nothing; Clear empties a voxel.", nameof(material));
        }
    }
}
