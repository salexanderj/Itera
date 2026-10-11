namespace Itera.Engine.Physics;

/// <summary>
/// A collision layer registered in <see cref="PhysicsLayers"/>, by its number; the default is layer 0. Its number is a
/// field, so the ECS inspector shows it where a component keeps one.
/// </summary>
public readonly record struct PhysicsLayerId
{
    /// <summary>Its place in the registry, 0 to 31.</summary>
    public readonly byte Value;

    public PhysicsLayerId(byte value) => Value = value;

    /// <summary>The layer every body and static is in unless given another.</summary>
    public static PhysicsLayerId Default => default;
}

/// <summary>
/// The run's collision layers, by name: registered once a run, at the game's <c>IRunStartup</c> (or a mod's), before the
/// first match, which seals the names; at most <see cref="MaxLayers"/>, layer 0 <c>default</c>. Which layers touch is a
/// table of pairs, either way round (<see cref="SetTouches"/>; every pair touches unless set apart), and which layer the
/// characters' stand-ins are in (<see cref="CharacterLayer"/>) and the voxels' statics (<see cref="VoxelLayer"/>); each
/// match starts from them as they are at its start, and changes its own table during play with
/// <see cref="PhysicsWorld.SetTouches"/>. The engine's own rules (stand-ins, cut bodies, voxel levels) come first: layers
/// only take pairs away. Main thread.
/// </summary>
public sealed class PhysicsLayers
{
    /// <summary>The most layers a run registers, the default among them (one bit each in a layer's row).</summary>
    public const int MaxLayers = 32;

    /// <summary>The default layer's name.</summary>
    public const string DefaultName = "default";

    private readonly List<string> _names = [DefaultName];
    private readonly Dictionary<string, PhysicsLayerId> _byName = new(StringComparer.Ordinal)
    {
        [DefaultName] = PhysicsLayerId.Default,
    };

    // Row a holds bit b when layers a and b touch; kept the same both ways.
    private readonly uint[] _touches = Enumerable.Repeat(uint.MaxValue, MaxLayers).ToArray();

    /// <summary>How many layers there are, the default among them.</summary>
    public int Count => _names.Count;

    /// <summary>True once a match has begun: no layer is registered from then on.</summary>
    public bool IsSealed { get; private set; }

    /// <summary>The layer the characters' stand-ins are in (the default unless set), read at each match's start.</summary>
    public PhysicsLayerId CharacterLayer { get; set; }

    /// <summary>The layer the voxels' statics are in (the default unless set), read at each match's start.</summary>
    public PhysicsLayerId VoxelLayer { get; set; }

    /// <summary>
    /// Registers the layer <paramref name="name"/> and returns its number; it touches every layer until set apart.
    /// Throws for a name registered already, a full registry, or once sealed.
    /// </summary>
    public PhysicsLayerId Register(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (IsSealed)
        {
            throw new InvalidOperationException(
                $"Physics layer {name}: layers are registered before the first match (in an IRunStartup)."
            );
        }

        if (_byName.ContainsKey(name))
        {
            throw new ArgumentException($"A physics layer named {name} is registered already.", nameof(name));
        }

        if (_names.Count == MaxLayers)
        {
            throw new InvalidOperationException($"At most {MaxLayers} physics layers are registered.");
        }

        var id = new PhysicsLayerId((byte)_names.Count);
        _names.Add(name);
        _byName.Add(name, id);
        return id;
    }

    /// <summary>The layer named <paramref name="name"/>, if one is registered.</summary>
    public bool TryFind(string name, out PhysicsLayerId id) => _byName.TryGetValue(name, out id);

    /// <summary>The name of layer <paramref name="id"/>.</summary>
    public string NameOf(PhysicsLayerId id) => _names[id.Value];

    /// <summary>
    /// Sets whether layers <paramref name="a"/> and <paramref name="b"/> touch (either way round; a layer with itself
    /// too), for the matches that start from now on. Throws for a layer not registered.
    /// </summary>
    public void SetTouches(PhysicsLayerId a, PhysicsLayerId b, bool touch)
    {
        Check(a);
        Check(b);
        Set(_touches, a, b, touch);
    }

    /// <summary>True when layers <paramref name="a"/> and <paramref name="b"/> touch, in the run's table.</summary>
    public bool Touches(PhysicsLayerId a, PhysicsLayerId b) => Touch(_touches, a, b);

    /// <summary>Seals the names (a match begins) and returns a copy of the table for the match.</summary>
    internal uint[] Seal()
    {
        IsSealed = true;
        return (uint[])_touches.Clone();
    }

    /// <summary>Sets a pair in <paramref name="table"/>, both ways.</summary>
    internal static void Set(uint[] table, PhysicsLayerId a, PhysicsLayerId b, bool touch)
    {
        if (touch)
        {
            table[a.Value] |= 1u << b.Value;
            table[b.Value] |= 1u << a.Value;
        }
        else
        {
            table[a.Value] &= ~(1u << b.Value);
            table[b.Value] &= ~(1u << a.Value);
        }
    }

    /// <summary>True when a pair touches in <paramref name="table"/>.</summary>
    internal static bool Touch(uint[] table, PhysicsLayerId a, PhysicsLayerId b) =>
        (table[a.Value] & (1u << b.Value)) != 0;

    private void Check(PhysicsLayerId layer)
    {
        if (layer.Value >= _names.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), layer.Value, "No such physics layer.");
        }
    }
}
