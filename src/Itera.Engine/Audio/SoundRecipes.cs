namespace Itera.Engine.Audio;

/// <summary>
/// The table of <see cref="SoundRecipe"/>s, by name and id. The engine and the game register their recipes at start
/// (the game at the run's "game" step, <c>IRunStartup</c>); a recipe cannot be replaced. Registration is on the main thread; the
/// table only grows, and <see cref="Count"/> is published after the recipe is stored, so the audio thread reads it
/// without a lock.
/// </summary>
public sealed class SoundRecipes
{
    /// <summary>The most recipes there can be.</summary>
    public const int MaxRecipes = 128;

    private readonly SoundRecipe[] _recipes = new SoundRecipe[MaxRecipes];
    private readonly string[] _names = new string[MaxRecipes];
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private int _count;
    private int _oneShots;

    /// <summary>How many recipes are registered; their ids are 0 to this less one.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>How many of them are one-shots, which the audio thread renders; streams are not counted.</summary>
    public int OneShotCount => Volatile.Read(ref _oneShots);

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
        if (recipe is OneShotRecipe)
        {
            Volatile.Write(ref _oneShots, _oneShots + 1);
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
        else
        {
            ok &= recipe is StreamRecipe;
        }

        if (!ok)
        {
            throw new ArgumentException(
                $"Sound recipe '{name}': the reference distance must be above 0 and at most 1000 m, the gain 0 to 4, a"
                    + " one-shot's length above 0 and at most 10 s with 1 to 16 levels and variations, and the kind a"
                    + " one-shot or a stream.",
                nameof(recipe)
            );
        }
    }
}
