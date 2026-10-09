using System.Numerics;

namespace Itera.Tools.Textures;

/// <summary>
/// The anisotropic Kuwahara filter (Kyprianidis, Kang, and Döllner, "Image and Video Abstraction by Anisotropic Kuwahara
/// Filtering", 2009; the polynomial sector weights of Kyprianidis, "Anisotropic Kuwahara Filtering with Polynomial
/// Weighting Functions", 2011), which flattens a photograph into strokes that follow its shapes, for a painted look: each
/// texel's direction and how strongly the picture is directed there come from the smoothed structure tensor of its
/// gradients; an ellipse round the texel, stretched along that direction as far as the picture is directed, is cut into
/// eight sectors; and the texel takes the sectors' mean colours, each weighted by how little its colours vary. Samples
/// wrap at the edges, so a tiling texture stays seamless. Works on sRGB values from 0 to 1.
/// </summary>
internal static class Kuwahara
{
    /// <summary>The structure tensor's smoothing, in texels (the paper's σ_c).</summary>
    private const float TensorSigma = 2f;

    /// <summary>The sector weights' angle where they fall to zero (the polynomial weights' zero crossing).</summary>
    private const float ZeroCrossing = 0.58f;

    /// <summary>
    /// Filters <paramref name="colour"/>, <paramref name="size"/> texels square, with an ellipse up to
    /// <paramref name="radius"/> texels across its shorter side where the picture has no direction; <paramref name="sharpness"/>
    /// (the paper's q) and <paramref name="hardness"/> set how strongly the most even sector wins; <paramref name="alpha"/>
    /// how far the ellipse stretches (1 as the paper: up to twice the radius).
    /// </summary>
    public static Vector3[] Filter(
        Vector3[] colour,
        int size,
        float radius,
        float sharpness = 8f,
        float hardness = 8f,
        float alpha = 1f
    )
    {
        var (orientation, anisotropy) = Structure(colour, size);
        var output = new Vector3[colour.Length];
        var zeta = 2f / radius;
        var sinZero = MathF.Sin(ZeroCrossing);
        var eta = (zeta + MathF.Cos(ZeroCrossing)) / (sinZero * sinZero);
        Parallel.For(
            0,
            size,
            y =>
            {
                Span<Vector4> means = stackalloc Vector4[8];
                Span<Vector3> squares = stackalloc Vector3[8];
                Span<float> weights = stackalloc float[8];
                for (var x = 0; x < size; x++)
                {
                    var i = y * size + x;
                    var a = radius * Math.Clamp((alpha + anisotropy[i]) / alpha, 0.1f, 2f);
                    var b = radius * Math.Clamp(alpha / (alpha + anisotropy[i]), 0.1f, 2f);
                    var cos = MathF.Cos(orientation[i]);
                    var sin = MathF.Sin(orientation[i]);
                    var maxX = (int)MathF.Sqrt(a * a * cos * cos + b * b * sin * sin);
                    var maxY = (int)MathF.Sqrt(a * a * sin * sin + b * b * cos * cos);
                    means.Clear();
                    squares.Clear();
                    for (var dy = -maxY; dy <= maxY; dy++)
                    {
                        var row = Wrap(y + dy, size) * size;
                        for (var dx = -maxX; dx <= maxX; dx++)
                        {
                            // The offset in the ellipse's frame, scaled into a disc of radius one half.
                            var vx = 0.5f * (cos * dx + sin * dy) / a;
                            var vy = 0.5f * (-sin * dx + cos * dy) / b;
                            if (vx * vx + vy * vy > 0.25f)
                            {
                                continue;
                            }

                            var c = colour[row + Wrap(x + dx, size)];
                            var sum = SectorWeights(vx, vy, zeta, eta, weights, 0);
                            var rx = MathF.Sqrt(0.5f) * (vx - vy);
                            var ry = MathF.Sqrt(0.5f) * (vx + vy);
                            sum += SectorWeights(rx, ry, zeta, eta, weights, 1);
                            var g = MathF.Exp(-3.125f * (rx * rx + ry * ry)) / sum;
                            for (var k = 0; k < 8; k++)
                            {
                                var w = weights[k] * g;
                                means[k] += new Vector4(c * w, w);
                                squares[k] += c * c * w;
                            }
                        }
                    }

                    var total = Vector4.Zero;
                    for (var k = 0; k < 8; k++)
                    {
                        if (means[k].W <= 0f)
                        {
                            continue;
                        }

                        var mean = new Vector3(means[k].X, means[k].Y, means[k].Z) / means[k].W;
                        var variance = Vector3.Abs(squares[k] / means[k].W - mean * mean);
                        var sigma = variance.X + variance.Y + variance.Z;
                        var w = 1f / (1f + MathF.Pow(hardness * 1000f * sigma, 0.5f * sharpness));
                        total += new Vector4(mean * w, w);
                    }

                    output[i] = total.W > 0f ? new Vector3(total.X, total.Y, total.Z) / total.W : colour[i];
                }
            }
        );
        return output;
    }

    /// <summary>
    /// The four polynomial sector weights of the point (vx, vy) of the unit disc's half, into every other entry of
    /// <paramref name="weights"/> from <paramref name="first"/>; returns their sum.
    /// </summary>
    private static float SectorWeights(float vx, float vy, float zeta, float eta, Span<float> weights, int first)
    {
        var vxx = zeta - eta * vx * vx;
        var vyy = zeta - eta * vy * vy;
        var z0 = MathF.Max(0f, vy + vxx);
        var z1 = MathF.Max(0f, -vx + vyy);
        var z2 = MathF.Max(0f, -vy + vxx);
        var z3 = MathF.Max(0f, vx + vyy);
        weights[first] = z0 * z0;
        weights[first + 2] = z1 * z1;
        weights[first + 4] = z2 * z2;
        weights[first + 6] = z3 * z3;
        return weights[first] + weights[first + 2] + weights[first + 4] + weights[first + 6];
    }

    /// <summary>
    /// Each texel's direction along which the picture changes least (radians) and how strongly it is directed (0 to 1),
    /// from the structure tensor of its Sobel gradients summed over the channels, smoothed by a Gaussian.
    /// </summary>
    private static (float[] Orientation, float[] Anisotropy) Structure(Vector3[] colour, int size)
    {
        var count = size * size;
        var e = new float[count];
        var f = new float[count];
        var g = new float[count];
        Parallel.For(
            0,
            size,
            y =>
            {
                var up = Wrap(y - 1, size) * size;
                var row = y * size;
                var down = Wrap(y + 1, size) * size;
                for (var x = 0; x < size; x++)
                {
                    var left = Wrap(x - 1, size);
                    var right = Wrap(x + 1, size);
                    var gx =
                        (colour[up + right] + 2 * colour[row + right] + colour[down + right])
                        - (colour[up + left] + 2 * colour[row + left] + colour[down + left]);
                    var gy =
                        (colour[down + left] + 2 * colour[down + x] + colour[down + right])
                        - (colour[up + left] + 2 * colour[up + x] + colour[up + right]);
                    gx /= 4f;
                    gy /= 4f;
                    e[row + x] = Vector3.Dot(gx, gx);
                    f[row + x] = Vector3.Dot(gx, gy);
                    g[row + x] = Vector3.Dot(gy, gy);
                }
            }
        );

        e = Blur(e, size);
        f = Blur(f, size);
        g = Blur(g, size);
        var orientation = new float[count];
        var anisotropy = new float[count];
        for (var i = 0; i < count; i++)
        {
            var root = MathF.Sqrt((e[i] - g[i]) * (e[i] - g[i]) + 4 * f[i] * f[i]);
            var major = 0.5f * (e[i] + g[i] + root);
            var minor = 0.5f * (e[i] + g[i] - root);
            var tx = major - e[i];
            var ty = -f[i];
            orientation[i] = tx * tx + ty * ty > 0f ? MathF.Atan2(ty, tx) : MathF.PI / 2;
            anisotropy[i] = major + minor > 0f ? (major - minor) / (major + minor) : 0f;
        }

        return (orientation, anisotropy);
    }

    /// <summary>A separable Gaussian blur of <see cref="TensorSigma"/>, wrapping at the edges.</summary>
    private static float[] Blur(float[] values, int size)
    {
        var reach = (int)MathF.Ceiling(3 * TensorSigma);
        var kernel = new float[2 * reach + 1];
        var sum = 0f;
        for (var k = -reach; k <= reach; k++)
        {
            kernel[k + reach] = MathF.Exp(-k * k / (2 * TensorSigma * TensorSigma));
            sum += kernel[k + reach];
        }

        for (var k = 0; k < kernel.Length; k++)
        {
            kernel[k] /= sum;
        }

        var across = new float[values.Length];
        Parallel.For(
            0,
            size,
            y =>
            {
                for (var x = 0; x < size; x++)
                {
                    var total = 0f;
                    for (var k = -reach; k <= reach; k++)
                    {
                        total += kernel[k + reach] * values[y * size + Wrap(x + k, size)];
                    }

                    across[y * size + x] = total;
                }
            }
        );
        var down = new float[values.Length];
        Parallel.For(
            0,
            size,
            y =>
            {
                for (var x = 0; x < size; x++)
                {
                    var total = 0f;
                    for (var k = -reach; k <= reach; k++)
                    {
                        total += kernel[k + reach] * across[Wrap(y + k, size) * size + x];
                    }

                    down[y * size + x] = total;
                }
            }
        );
        return down;
    }

    private static int Wrap(int value, int size) => ((value % size) + size) % size;
}
