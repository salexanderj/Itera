using System.Numerics;

namespace Itera.Engine.Rendering;

/// <summary>One sun shadow cascade placed by <see cref="ShadowCascades.Fit"/>.</summary>
/// <param name="ViewProjection">
/// World point (System.Numerics row-vector convention: <c>Vector4.Transform(new Vector4(p, 1), m)</c>) to the cascade's
/// clip space: x and y in [-1, 1] across the square, z in [0, 1] from the end towards the sun to the far side, w = 1.
/// </param>
/// <param name="TexelMetres">The width of one shadow-map texel in metres.</param>
public readonly record struct CascadeFit(Matrix4x4 ViewProjection, float TexelMetres);

/// <summary>
/// Places the square, orthographic shadow maps of the sun's cascades, once per frame on the main thread. Pure maths:
/// no Vulkan, no allocation.
/// </summary>
public static class ShadowCascades
{
    /// <summary>The square's size is a multiple of this many metres, so a turning camera does not change it.</summary>
    private const double RadiusStepMetres = 1.0 / 16.0;

    /// <summary>The sun counts as straight up or down within one degree of it (the cosine of one degree).</summary>
    private static readonly double NearVerticalDot = Math.Cos(Math.PI / 180.0);

    /// <summary>
    /// Fits one cascade around the slice of the view frustum between two view depths. The square is the slice's
    /// bounding sphere, so its size does not change when the camera only turns. Its centre snaps to whole texels on the
    /// light's two image axes (which depend on the sun alone), so a fixed world point keeps its place within its texel
    /// as the camera moves.
    /// </summary>
    /// <param name="position">The camera's position.</param>
    /// <param name="forward">The view direction, a unit vector.</param>
    /// <param name="right">
    /// Scaled so that <c>forward ± right ± up</c> point at the image's four corners at a distance of 1 along forward.
    /// </param>
    /// <param name="up">Scaled like <paramref name="right"/>.</param>
    /// <param name="nearMetres">The slice's near view depth.</param>
    /// <param name="farMetres">The slice's far view depth.</param>
    /// <param name="sunDirection">A unit vector from the ground towards the sun.</param>
    /// <param name="mapTexels">The shadow map's width and height in texels.</param>
    /// <param name="casterMetres">How far towards the sun beyond the slice the box reaches.</param>
    /// <remarks>
    /// The radius is the farthest corner's distance from the slice's centre, grown by one part in
    /// <c>mapTexels - 1</c> so that the snap of up to half a texel cannot push a corner out of the square, then rounded
    /// up to a multiple of 1/16 m.
    /// </remarks>
    public static CascadeFit Fit(
        Vector3 position,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float nearMetres,
        float farMetres,
        Vector3 sunDirection,
        int mapTexels,
        float casterMetres
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(mapTexels, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(nearMetres);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(farMetres, nearMetres);

        // The slice's 8 corners. Doubles keep large world positions exact enough for the texel fractions.
        double cx = 0;
        double cy = 0;
        double cz = 0;
        for (var i = 0; i < 8; i++)
        {
            Corner(i, in position, in forward, in right, in up, nearMetres, farMetres, out var x, out var y, out var z);
            cx += x;
            cy += y;
            cz += z;
        }

        cx /= 8;
        cy /= 8;
        cz /= 8;

        var radiusSquared = 0.0;
        for (var i = 0; i < 8; i++)
        {
            Corner(i, in position, in forward, in right, in up, nearMetres, farMetres, out var x, out var y, out var z);
            var dx = x - cx;
            var dy = y - cy;
            var dz = z - cz;
            radiusSquared = Math.Max(radiusSquared, (dx * dx) + (dy * dy) + (dz * dz));
        }

        var grown = Math.Sqrt(radiusSquared) * mapTexels / (mapTexels - 1.0);
        var radius = Math.Max(Math.Ceiling(grown / RadiusStepMetres) * RadiusStepMetres, RadiusStepMetres);
        var texel = 2 * radius / mapTexels;

        // The light's axes: s towards the sun, a and b across the image, right-handed (a cross b = s).
        double sx = sunDirection.X;
        double sy = sunDirection.Y;
        double sz = sunDirection.Z;
        var sunLength = Math.Sqrt((sx * sx) + (sy * sy) + (sz * sz));
        if (sunLength < 1e-9)
        {
            sx = 0;
            sy = 1;
            sz = 0;
        }
        else
        {
            sx /= sunLength;
            sy /= sunLength;
            sz /= sunLength;
        }

        // The reference axis is world up, or a fixed axis when the sun is within a degree of straight up or down.
        double rx = 0;
        double ry = 1;
        double rz = 0;
        if (Math.Abs(sy) > NearVerticalDot)
        {
            ry = 0;
            rz = 1;
        }

        var ax = (ry * sz) - (rz * sy);
        var ay = (rz * sx) - (rx * sz);
        var az = (rx * sy) - (ry * sx);
        var aLength = Math.Sqrt((ax * ax) + (ay * ay) + (az * az));
        ax /= aLength;
        ay /= aLength;
        az /= aLength;
        var bx = (sy * az) - (sz * ay);
        var by = (sz * ax) - (sx * az);
        var bz = (sx * ay) - (sy * ax);

        // Snap the centre's place on the image axes to whole texels.
        var centreA = (cx * ax) + (cy * ay) + (cz * az);
        var centreB = (cx * bx) + (cy * by) + (cz * bz);
        var centreS = (cx * sx) + (cy * sy) + (cz * sz);
        var snapA = Math.Round(centreA / texel) * texel;
        var snapB = Math.Round(centreB / texel) * texel;

        // z is 0 at the sphere's sun side extended by the casters' reach, and 1 at its far side.
        var depth = (2 * radius) + casterMetres;
        var zStart = centreS + radius + casterMetres;

        var m = new Matrix4x4(
            (float)(ax / radius),
            (float)(bx / radius),
            (float)(-sx / depth),
            0,
            (float)(ay / radius),
            (float)(by / radius),
            (float)(-sy / depth),
            0,
            (float)(az / radius),
            (float)(bz / radius),
            (float)(-sz / depth),
            0,
            (float)(-snapA / radius),
            (float)(-snapB / radius),
            (float)(zStart / depth),
            1
        );
        return new CascadeFit(m, (float)texel);
    }

    /// <summary>Corner <paramref name="index"/> of the slice: bit 0 is right, bit 1 is up, bit 2 is near or far.</summary>
    private static void Corner(
        int index,
        in Vector3 position,
        in Vector3 forward,
        in Vector3 right,
        in Vector3 up,
        float nearMetres,
        float farMetres,
        out double x,
        out double y,
        out double z
    )
    {
        double d = (index & 4) == 0 ? nearMetres : farMetres;
        double sr = (index & 1) == 0 ? -1 : 1;
        double su = (index & 2) == 0 ? -1 : 1;
        x = position.X + (d * (forward.X + (sr * right.X) + (su * up.X)));
        y = position.Y + (d * (forward.Y + (sr * right.Y) + (su * up.Y)));
        z = position.Z + (d * (forward.Z + (sr * right.Z) + (su * up.Z)));
    }
}
