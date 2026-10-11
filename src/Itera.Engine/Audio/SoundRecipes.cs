namespace Itera.Engine.Audio;

/// <summary>
/// The table of <see cref="SoundRecipe"/>s, by name and id. The engine and the game register their recipes at start
/// (the game at the run's "game" step, <c>IRunStartup</c>); a recipe cannot be replaced. Registration is on the main thread; the
/// table only grows, and <see cref="Count"/> is published after the recipe is stored, so the audio thread reads it
/// without a lock.
/// </summary>
public sealed class SoundRecipes
{
    /// <summary>The most recipes there can be (sounds read from files count too).</summary>
    public const int MaxRecipes = 256;

    /// <summary>The most files a file one-shot may have (its variations).</summary>
    public const int MaxFiles = 16;

    private readonly SoundRecipe[] _recipes = new SoundRecipe[MaxRecipes];
    private readonly string[] _names = new string[MaxRecipes];
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private int _count;
    private int _prepared;

    /// <summary>How many recipes are registered; their ids are 0 to this less one.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// How many of them are prepared at start, into buffers: one-shot recipes (rendered) and sounds from files (decoded);
    /// streams are made live and not counted.
    /// </summary>
    public int PreparedCount => Volatile.Read(ref _prepared);

    /// <summary>Adds a recipe named <paramref name="name"/> and returns its id. Throws for a name already used or a full table.</summary>
    public int Register(string name, SoundRecipe recipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(recipe);
        Validate(name, recipe);
        if (_ids.ContainsKey(name))
        {
            throw new InvalidOperationException($"Sound recipe '{name}' is registered already.");
        }

        if (_count == MaxRecipes)
        {
            throw new InvalidOperationException($"No room for sound recipe '{name}': {MaxRecipes} are registered.");
        }

        var id = _count;
        _recipes[id] = recipe;
        _names[id] = name;
        _ids.Add(name, id);
        if (IsPrepared(recipe))
        {
            Volatile.Write(ref _prepared, _prepared + 1);
        }

        Volatile.Write(ref _count, id + 1);
        return id;
    }

    /// <summary>The recipe <paramref name="id"/>.</summary>
    public SoundRecipe this[int id] => _recipes[id];

    /// <summary>The id of the recipe named <paramref name="name"/> (main thread).</summary>
    public bool TryFind(string name, out int id) => _ids.TryGetValue(name, out id);

    /// <summary>The name of the recipe <paramref name="id"/>.</summary>
    public string NameOf(int id) => _names[id];

    /// <summary>True for a recipe the game sustains (<c>AudioService.Sustain</c>) rather than plays once.</summary>
    public bool IsSustained(int id) => _recipes[id] is StreamRecipe;

    /// <summary>What the recipe <paramref name="id"/> is, in a word or two: one-shot, stream, file one-shot.</summary>
    public string KindOf(int id) =>
        _recipes[id] switch
        {
            StreamRecipe => "stream",
            FileOneShotRecipe => "file one-shot",
            _ => "one-shot",
        };

    /// <summary>True for a recipe prepared into buffers at start: a one-shot recipe, or a sound from files.</summary>
    internal static bool IsPrepared(SoundRecipe recipe) => recipe is OneShotRecipe or FileOneShotRecipe;

    private static void Validate(string name, SoundRecipe recipe)
    {
        var ok = recipe.ReferenceMetres is > 0 and <= 1000 && recipe.Gain is >= 0 and <= 4;
        if (recipe is OneShotRecipe oneShot)
        {
            ok &=
                oneShot.Seconds is > 0 and <= 10
                && oneShot.Levels is >= 1 and <= 16
                && oneShot.VariationsPerLevel is >= 1 and <= 16;
        }
        else if (recipe is FileOneShotRecipe files)
        {
            ok &= files.Paths.Count is >= 1 and <= MaxFiles;
        }
        else
        {
            ok &= recipe is StreamRecipe;
        }

        if (!ok)
        {
            throw new ArgumentException(
                $"Sound recipe '{name}': the reference distance must be above 0 and at most 1000 m, the gain 0 to 4, a"
                    + " one-shot's length above 0 and at most 10 s with 1 to 16 levels and variations, a file one-shot's"
                    + $" files 1 to {MaxFiles}, and the kind a one-shot, a stream, or a file one-shot.",
                nameof(recipe)
            );
        }
    }
}
