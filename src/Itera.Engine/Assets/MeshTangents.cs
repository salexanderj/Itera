using System.Numerics;

namespace Itera.Engine.Assets;

/// <summary>
/// Makes the tangents of a mesh whose file has none, for normal-mapped materials. It follows Lengyel's "Computing
/// Tangent Space Basis Vectors for an Arbitrary Mesh" (2001), not MikkTSpace: the results differ at mirrored UV seams.
/// A file's own tangents are used when it has them.
/// </summary>
internal static class MeshTangents
{
    /// <summary>A UV triangle with a determinant below this has no usable direction and adds nothing.</summary>
    private const float MinimumDeterminant = 1e-12f;

    /// <summary>A projected tangent shorter than this counts as missing.</summary>
    private const float MinimumLength = 1e-6f;

    /// <summary>
    /// Writes glTF tangents into <paramref name="tangents"/>: xyz the unit tangent, w the handedness, +1 or -1. The
    /// tangent points the way u increases and lies in the plane of the vertex's normal. The bitangent that glTF shaders
    /// build, <c>cross(normal, tangent.xyz) * tangent.w</c>, points the way v increases. Each triangle adds its
    /// directions to its three vertices without normalising them, so larger triangles weigh more. A triangle with
    /// equal or collinear UVs adds nothing; a vertex left with no direction gets any unit vector perpendicular to its
    /// normal. The normals must be unit length.
    /// </summary>
    /// <param name="positions">Vertex positions.</param>
    /// <param name="normals">Unit vertex normals.</param>
    /// <param name="uvs">Vertex texture coordinates.</param>
    /// <param name="indices">Three vertex indices for each triangle.</param>
    /// <param name="tangents">One tangent for each vertex.</param>
    /// <exception cref="ArgumentException">
    /// The spans differ in length, the index count is not a multiple of 3, or an index is out of range.
    /// </exception>
    public static void Generate(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> uvs,
        ReadOnlySpan<uint> indices,
        Span<Vector4> tangents
    )
    {
        var count = positions.Length;
        if (normals.Length != count || uvs.Length != count || tangents.Length != count)
        {
            throw new ArgumentException("Positions, normals, UVs, and tangents must have the same length.");
        }

        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException("The index count must be a multiple of 3.", nameof(indices));
        }

        foreach (var index in indices)
        {
            if (index >= (uint)count)
            {
                throw new ArgumentException($"Index {index} is out of range for {count} vertices.", nameof(indices));
            }
        }

        // Until the last loop, the tangents' xyz hold the sums of the u directions.
        tangents.Clear();
        var vSums = new Vector3[count];
        for (var i = 0; i < indices.Length; i += 3)
        {
            var i0 = (int)indices[i];
            var i1 = (int)indices[i + 1];
            var i2 = (int)indices[i + 2];
            var e1 = positions[i1] - positions[i0];
            var e2 = positions[i2] - positions[i0];
            var duv1 = uvs[i1] - uvs[i0];
            var duv2 = uvs[i2] - uvs[i0];
            var determinant = duv1.X * duv2.Y - duv2.X * duv1.Y;
            if (!float.IsFinite(determinant) || MathF.Abs(determinant) < MinimumDeterminant)
            {
                continue;
            }

            var uDirection = (e1 * duv2.Y - e2 * duv1.Y) / determinant;
            var vDirection = (e2 * duv1.X - e1 * duv2.X) / determinant;
            if (!IsFinite(uDirection) || !IsFinite(vDirection))
            {
                continue;
            }

            tangents[i0] += new Vector4(uDirection, 0f);
            tangents[i1] += new Vector4(uDirection, 0f);
            tangents[i2] += new Vector4(uDirection, 0f);
            vSums[i0] += vDirection;
            vSums[i1] += vDirection;
            vSums[i2] += vDirection;
        }

        for (var i = 0; i < count; i++)
        {
            var normal = normals[i];
            var uSum = new Vector3(tangents[i].X, tangents[i].Y, tangents[i].Z);
            var tangent = uSum - normal * Vector3.Dot(normal, uSum);
            var length = tangent.Length();
            tangent = length > MinimumLength ? tangent / length : Perpendicular(normal);
            var handedness = Vector3.Dot(Vector3.Cross(normal, tangent), vSums[i]) < 0f ? -1f : 1f;
            tangents[i] = new Vector4(tangent, handedness);
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>A unit vector perpendicular to <paramref name="normal"/>, made with the axis the normal is least aligned with.</summary>
    private static Vector3 Perpendicular(Vector3 normal)
    {
        var x = MathF.Abs(normal.X);
        var y = MathF.Abs(normal.Y);
        var z = MathF.Abs(normal.Z);
        var axis =
            x <= y && x <= z ? Vector3.UnitX
            : y <= z ? Vector3.UnitY
            : Vector3.UnitZ;
        var perpendicular = Vector3.Cross(normal, axis);
        var length = perpendicular.Length();
        return length > MinimumLength ? perpendicular / length : Vector3.UnitX;
    }
}
