using Itera.Engine.Audio;

namespace Itera.MeshSample;

/// <summary>
/// The sample's sounds, read from its files in <c>Sounds/</c> (made in code by <c>tools/sounds/make_sounds.py demo</c>;
/// <c>Sounds/SOURCES.md</c>), registered once a run at its startup: a clang as the player's aim comes onto a block (three
/// variations), the yard screen's hum (an intro, then a loop), a chime at the listener as the loading screen lifts
/// (stereo), and two music tracks, the menu's and the yard's.
/// </summary>
public static class SampleSounds
{
    /// <summary>A struck bar, one of three files, where the aim comes onto a block.</summary>
    public const string Clang = "clang";

    /// <summary>The yard screen's hum: a rise played once, then a loop of 2 s (the file's <c>smpl</c> chunk).</summary>
    public const string Hum = "hum";

    /// <summary>A bell chord, kept stereo, at the listener.</summary>
    public const string Chime = "chime";

    /// <summary>
    /// The menu's music: 16 s heard once (an intro of 4 s, then the chords once through), then a loop of the chords' 12 s
    /// (the file's LOOPSTART and LOOPLENGTH tags).
    /// </summary>
    public const string MenuMusic = "menu";

    /// <summary>The yard's music: 16 s, looping whole.</summary>
    public const string YardMusic = "yard";

    /// <summary>Registers the sounds and declares the music (once a run, before the audio thread starts).</summary>
    public static void Register(AudioService audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        audio.Recipes.Register(
            Clang,
            new FileOneShotRecipe(File("clang-1.wav"), File("clang-2.wav"), File("clang-3.wav"))
            {
                Gain = 0.5f,
                ReferenceMetres = 2,
            }
        );
        audio.Recipes.Register(Hum, new FileLoopRecipe(File("hum.wav")) { Gain = 0.5f, ReferenceMetres = 2 });
        audio.Recipes.Register(Chime, new FileOneShotRecipe(File("chime.wav")) { KeepStereo = true, Gain = 0.7f });
        audio.Music.Declare(MenuMusic, File("menu.ogg"));
        audio.Music.Declare(YardMusic, File("yard.ogg"));
    }

    private static string File(string name) => Path.Combine("Sounds", name);
}
