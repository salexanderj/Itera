using System.Numerics;

namespace Itera.Engine.Assets;

/// <summary>
/// A joint's transform relative to its parent (glTF's translation, rotation, and scale; the translation in metres): scale
/// first, then rotation, then translation, as a <see cref="Matrix4x4"/> used as <c>v * M</c>.
/// </summary>
public readonly record struct JointTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    /// <summary>No change: no move, no turn, a scale of one.</summary>
    public static JointTransform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>The transform as a matrix: scale, then rotation, then translation.</summary>
    public Matrix4x4 ToMatrix()
    {
        var matrix = Matrix4x4.CreateFromQuaternion(Rotation);
        matrix.M11 *= Scale.X;
        matrix.M12 *= Scale.X;
        matrix.M13 *= Scale.X;
        matrix.M21 *= Scale.Y;
        matrix.M22 *= Scale.Y;
        matrix.M23 *= Scale.Y;
        matrix.M31 *= Scale.Z;
        matrix.M32 *= Scale.Z;
        matrix.M33 *= Scale.Z;
        matrix.M41 = Translation.X;
        matrix.M42 = Translation.Y;
        matrix.M43 = Translation.Z;
        return matrix;
    }
}
