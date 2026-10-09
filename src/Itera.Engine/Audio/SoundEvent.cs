using System.Numerics;

namespace Itera.Engine.Audio;

/// <summary>One play of a <see cref="OneShotRecipe"/>, queued with <see cref="AudioService.Play"/>.</summary>
public struct SoundEvent
{
    /// <summary>The recipe's id in <see cref="SoundRecipes"/>.</summary>
    public int Recipe;

    /// <summary>Where it sounds, world metres.</summary>
    public Vector3 Position;

    /// <summary>0 to 1: picks the rendered level nearest it (how hard, how big).</summary>
    public float Level;

    /// <summary>The event's gain, times the recipe's; 0 means 1.</summary>
    public float Gain;

    /// <summary>Playback speed: above 1 is higher and shorter; 0 means 1.</summary>
    public float Pitch;

    /// <summary>
    /// How much farther than the recipe's <see cref="SoundRecipe.ReferenceMetres"/> the sound carries (a large body's step,
    /// a large dig): it multiplies that distance. 0 means 1.
    /// </summary>
    public float Reach;

    /// <summary>
    /// True plays it where the listener is, wherever the listener moves, at its full gain: a sound of the menu, not of
    /// the world. <see cref="Position"/> and <see cref="Reach"/> are then ignored.
    /// </summary>
    public bool AtListener;
}

/// <summary>
/// A hold on a <see cref="StreamRecipe"/>'s sound, given with <see cref="AudioService.Sustain"/>: a sound plays while
/// the game sustains it (each frame or fixed step), for <see cref="AudioService.HoldSeconds"/> after the last, and then
/// fades out. The same <see cref="Key"/> and recipe keep the same sound going; the game picks the key (a player's slot,
/// an entity's id).
/// </summary>
public struct SoundLoop
{
    /// <summary>The recipe's id in <see cref="SoundRecipes"/>.</summary>
    public int Recipe;

    /// <summary>Which of the recipe's sounds this is.</summary>
    public int Key;

    /// <summary>Where it sounds, world metres.</summary>
    public Vector3 Position;

    /// <summary>0 to 1: the generator's level.</summary>
    public float Level;

    /// <summary>The sound's gain, times the recipe's; 0 means 1.</summary>
    public float Gain;
}
