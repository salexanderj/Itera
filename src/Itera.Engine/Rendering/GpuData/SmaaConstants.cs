using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The push constants of SMAA's three passes (<c>smaa-common.glsl</c>, <c>layout(scalar)</c>; <see cref="SmaaPass"/>).
/// Field order and types match the GLSL block; <c>SmaaPassTests</c> checks them.
/// </summary>
public struct SmaaConstants
{
    /// <summary>SMAA's <c>SMAA_RT_METRICS</c>: 1 over the drawn width, 1 over the drawn height, the width, the height.</summary>
    public Vector4 RtMetrics;

    /// <summary>What the blend pass shows (<see cref="SmaaPicture"/>): the anti-aliased picture, its edges, or its weights.</summary>
    public uint Picture;
}
