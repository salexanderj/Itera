namespace Itera.Engine.Rendering;

/// <summary>
/// The render scale (<c>Engine:Renderer:RenderScale</c>): the world, models, and debug lines are drawn at this share of
/// the screen's size on each axis, and FSR 1 upscales the picture to the screen (<see cref="UpscalePass"/>). At 1 they
/// are drawn straight onto the screen. The main thread and the render thread size the image with the same
/// <see cref="Apply"/>.
/// </summary>
public static class RenderScale
{
    /// <summary>The smallest render scale.</summary>
    public const float Min = 0.5f;

    /// <summary>The largest render scale: the screen's own size.</summary>
    public const float Max = 1;

    /// <summary>
    /// The drawn size for a screen <paramref name="size"/> pixels across at <paramref name="scale"/> (held to
    /// <see cref="Min"/> to <see cref="Max"/>), rounded to the nearest pixel: at least 1 pixel, and 0 only for an empty
    /// screen.
    /// </summary>
    public static uint Apply(uint size, float scale) =>
        size == 0 ? 0 : Math.Clamp((uint)MathF.Round(size * Math.Clamp(scale, Min, Max)), 1u, size);
}
