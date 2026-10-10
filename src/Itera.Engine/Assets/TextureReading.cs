namespace Itera.Engine.Assets;

/// <summary>How a model's import reads its textures.</summary>
internal enum TextureReading
{
    /// <summary>Not at all, and its materials name none: a run without graphics.</summary>
    None,

    /// <summary>Each image decoded and given every level at load: what <c>tools/Textures</c> compresses.</summary>
    Decode,

    /// <summary>
    /// From the model's compressed textures where they match its images (<see cref="CompressedTextures"/>), the rest
    /// decoded: a run with graphics.
    /// </summary>
    PreferCompressed,
}
