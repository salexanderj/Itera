using System.Numerics;

namespace Itera.MeshSample;

/// <summary>
/// The yard block the player aims at, which <see cref="Rendering.AimHighlightPass"/> draws a glowing rim round: set by
/// <see cref="Systems.AimHighlightSystem"/> each frame, read by the pass's write. Run-wide (the pass is); main thread.
/// </summary>
public sealed class AimHighlight
{
    /// <summary>True while a block is aimed at.</summary>
    public bool Active { get; private set; }

    /// <summary>The rim's box: a unit cube placed over the block, a little larger than it.</summary>
    public Matrix4x4 Transform { get; private set; }

    /// <summary>Shows the rim round the box <paramref name="transform"/> places.</summary>
    public void Show(in Matrix4x4 transform)
    {
        Transform = transform;
        Active = true;
    }

    /// <summary>Shows no rim.</summary>
    public void Hide() => Active = false;
}
