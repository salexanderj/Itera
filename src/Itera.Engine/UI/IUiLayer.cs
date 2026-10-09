using Itera.Modding;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// One part of the UI that <see cref="UISurface"/> draws: the console, the stats line. The run's layers are drawn in the
/// order they were registered, later ones on top: those below the match's layers (<see cref="IMatchUiLayer"/>, the HUD)
/// first, then the match's, then those with <see cref="AboveMatch"/>. Main thread.
/// </summary>
public interface IUiLayer
{
    /// <summary>True for a run's layer drawn over the match's layers (the console); false (the default) under them.</summary>
    bool AboveMatch => false;

    /// <summary>True while the layer shows anything.</summary>
    bool Visible { get; }

    /// <summary>True when the layer's look changed since it was last drawn.</summary>
    bool Dirty { get; }

    /// <summary>Called once per frame before anything is drawn, for changes that come with time (a blinking cursor).</summary>
    void Update(in FrameTime time);

    /// <summary>
    /// The pixels the layer may cover on a surface of <paramref name="width"/> x <paramref name="height"/>. They hold
    /// everything it draws: <see cref="UISurface"/> clips it there.
    /// </summary>
    SKRectI Bounds(int width, int height);

    /// <summary>
    /// Draws the layer onto a canvas of <paramref name="width"/> x <paramref name="height"/>, clipped to its bounds and to
    /// the area being drawn again, which is cleared and holds the layers under it; clears <see cref="Dirty"/>. Called
    /// when it or a layer it meets changed, so it may be drawn while not dirty.
    /// </summary>
    void Draw(SKCanvas canvas, int width, int height);
}
