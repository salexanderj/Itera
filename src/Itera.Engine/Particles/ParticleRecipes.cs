namespace Itera.Engine.Particles;

/// <summary>
/// The table of <see cref="ParticleRecipe"/>s, by name and id. The game registers its recipes at start (from its
/// systems' <c>Initialize</c>); a recipe can be changed later with <see cref="Set"/> and takes effect from the next
/// frame, because the whole table goes to the GPU every frame. Main thread only; nothing allocates after registration.
/// </summary>
public sealed class ParticleRecipes
{
    /// <summary>The most recipes there can be.</summary>
    public const int MaxRecipes = 128;

    private readonly ParticleRecipe[] _recipes = new ParticleRecipe[MaxRecipes];
    private readonly string[] _names = new string[MaxRecipes];
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

    /// <summary>How many recipes are registered; their ids are 0 to this less one.</summary>
    public int Count { get; private set; }

    /// <summary>The longest <see cref="ParticleRecipe.LifetimeMax"/> of any recipe, in seconds.</summary>
    public float LongestLifetime { get; private set; }

    /// <summary>The recipes, by id.</summary>
    public ReadOnlySpan<ParticleRecipe> All => _recipes.AsSpan(0, Count);

    /// <summary>
    /// Adds a recipe named <paramref name="name"/> and returns its id; a name registered before is replaced, keeping its
    /// id. Throws when the table is full or a value is out of range.
    /// </summary>
    public int Register(string name, in ParticleRecipe recipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Validate(name, in recipe);
        if (_ids.TryGetValue(name, out var id))
        {
            Set(id, in recipe);
            return id;
        }

        if (Count == MaxRecipes)
        {
            throw new InvalidOperationException($"No room for particle recipe '{name}': {MaxRecipes} are registered.");
        }

        id = Count++;
        _ids.Add(name, id);
        _names[id] = name;
        Set(id, in recipe);
        return id;
    }

    /// <summary>Replaces the recipe <paramref name="id"/>.</summary>
    public void Set(int id, in ParticleRecipe recipe)
    {
        if ((uint)id >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "No such particle recipe.");
        }

        Validate(_names[id], in recipe);
        _recipes[id] = recipe;
        LongestLifetime = 0;
        for (var i = 0; i < Count; i++)
        {
            LongestLifetime = Math.Max(LongestLifetime, _recipes[i].LifetimeMax);
        }
    }

    /// <summary>The recipe <paramref name="id"/>.</summary>
    public ref readonly ParticleRecipe this[int id] => ref _recipes[id];

    /// <summary>The id of the recipe named <paramref name="name"/>.</summary>
    public bool TryFind(string name, out int id) => _ids.TryGetValue(name, out id);

    /// <summary>The name of the recipe <paramref name="id"/>.</summary>
    public string NameOf(int id) => _names[id];

    private static void Validate(string name, in ParticleRecipe recipe)
    {
        if (
            !(recipe.LifetimeMin > 0)
            || !(recipe.LifetimeMax >= recipe.LifetimeMin)
            || recipe.LifetimeMax > 600
            || !(recipe.SpeedMin >= 0)
            || !(recipe.SpeedMax >= recipe.SpeedMin)
            || !(recipe.SizeStart >= 0)
            || !(recipe.SizeEnd >= 0)
            || !(recipe.SpreadDegrees is >= 0 and <= 180)
            || recipe.Draw is not (ParticleDraw.Cube or ParticleDraw.Sprite)
            || recipe.Collision
                is not (
                    ParticleCollision.None
                    or ParticleCollision.Bounce
                    or ParticleCollision.Stick
                    or ParticleCollision.Die
                )
            || recipe.Stretch is not (ParticleStretch.None or ParticleStretch.Velocity or ParticleStretch.Segment)
        )
        {
            throw new ArgumentException(
                $"Particle recipe '{name}': lives must be above 0 and at most 600 s, speeds and sizes 0 or more, the"
                    + " ranges in order, the spread 0 to 180 degrees, and the kinds known ones.",
                nameof(recipe)
            );
        }
    }
}
