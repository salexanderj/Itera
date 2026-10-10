using System.Numerics;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// What casts into a shadow cascade: a bounding sphere that meets the cascade's box, and lies on the inner side of each
/// plane of the cascade's slice of the view (its side planes and its near and far depths) that faces the sun. A sphere
/// shades a point of the slice only if it lies towards the sun from it, and moving towards the sun never leaves the inner
/// side of a plane that faces the sun; the planes that face away are dropped, so the test keeps a little more than it
/// must, never less. <see cref="MeshView"/> sets one per cascade each frame.
/// </summary>
public sealed class CascadeBounds
{
    private const int MostPlanes = 6;

    // Inward normals (xyz) and the dot product a point on the plane has with them (w), relative to the camera.
    private readonly Vector4[] _planes = new Vector4[MostPlanes];
    private Matrix4x4 _viewProjection;
    private Vector3 _origin;
    private float _acrossPerMetre;
    private float _alongPerMetre;
    private int _planeCount;

    /// <summary>
    /// Takes the cascade's fit, <paramref name="mapTexels"/> wide, and its slice of the view, between
    /// <paramref name="nearMetres"/> and <paramref name="farMetres"/> of view depth, bounded by the frustum's
    /// <paramref name="sidePlanes"/> (inward normals, and the dot product a point on each has with it, relative to
    /// <paramref name="position"/>).
    /// </summary>
    internal void Set(
        in CascadeFit fit,
        int mapTexels,
        Vector3 position,
        Vector3 forward,
        ReadOnlySpan<Vector4> sidePlanes,
        float nearMetres,
        float farMetres,
        Vector3 sun
    )
    {
        _viewProjection = fit.ViewProjection;
        _origin = position;
        // The square is mapTexels texels wide, 2 in clip units; z's scale is the length of its column.
        _acrossPerMetre = 2 / (fit.TexelMetres * mapTexels);
        var m = fit.ViewProjection;
        _alongPerMetre = new Vector3(m.M13, m.M23, m.M33).Length();
        _planeCount = 0;
        foreach (var plane in sidePlanes)
        {
            Keep(new Vector3(plane.X, plane.Y, plane.Z), plane.W, sun);
        }

        Keep(forward, nearMetres, sun);
        Keep(-forward, -farMetres, sun);
    }

    /// <summary>True when a sphere of <paramref name="radius"/> at <paramref name="centre"/> may cast into the cascade.</summary>
    public bool Holds(Vector3 centre, float radius)
    {
        var clip = Vector3.Transform(centre, _viewProjection);
        var across = 1 + radius * _acrossPerMetre;
        var along = radius * _alongPerMetre;
        if (MathF.Abs(clip.X) > across || MathF.Abs(clip.Y) > across || clip.Z < -along || clip.Z > 1 + along)
        {
            return false;
        }

        var offset = centre - _origin;
        for (var i = 0; i < _planeCount; i++)
        {
            var plane = _planes[i];
            if (Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), offset) - plane.W < -radius)
            {
                return false;
            }
        }

        return true;
    }

    private void Keep(Vector3 normal, float distance, Vector3 sun)
    {
        if (Vector3.Dot(normal, sun) >= 0)
        {
            _planes[_planeCount++] = new Vector4(normal, distance);
        }
    }
}
