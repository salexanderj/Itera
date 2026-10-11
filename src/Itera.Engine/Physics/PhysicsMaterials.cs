using BepuPhysics.Constraints;

namespace Itera.Engine.Physics;

/// <summary>A physics material: how a surface grips (<see cref="Friction"/>) and how it bounces (<see cref="Bounciness"/>).</summary>
/// <param name="Friction">
/// The coefficient of friction, 0 (ice-like) to 4; two surfaces grip by the square root of the product of theirs (1 and 1
/// grip as 1, the engine's default).
/// </param>
/// <param name="Bounciness">
/// The share of its approach speed a pair keeps as it bounces apart, 0 (none, the default) to 1; two surfaces bounce as
/// the bouncier. Bepu has no coefficient of restitution, so the engine sets the bounce of each new impact after the step
/// (<c>PhysicsContacts</c>): from at least 0.5 m/s; slower contacts (resting, rolling) are left as Bepu solves them.
/// </param>
public readonly record struct PhysicsMaterial(float Friction, float Bounciness);

/// <summary>
/// A material registered in <see cref="PhysicsMaterials"/>, by its number; the default is the engine's own. Its number
/// is a field, so the ECS inspector shows it where a component keeps one.
/// </summary>
public readonly record struct PhysicsMaterialId
{
    /// <summary>Its place in the registry.</summary>
    public readonly byte Value;

    public PhysicsMaterialId(byte value) => Value = value;

    /// <summary>The engine's material: friction 1, no bounce.</summary>
    public static PhysicsMaterialId Default => default;
}

/// <summary>
/// The run's physics materials, by name (<see cref="PhysicsMaterial"/>): registered once a run, at the game's
/// <c>IRunStartup</c> (or a mod's), before the first match, which seals them; at most <see cref="MaxMaterials"/>.
/// Material 0 is <c>default</c>, the engine's (friction 1, no bounce), which every body and static has unless given
/// another. Voxel materials take theirs by <see cref="MapVoxel"/> (default unless mapped): the voxel colliders build a
/// static for each physics material a cell's voxels have, and a ball takes its voxel material's. Main thread.
/// </summary>
public sealed class PhysicsMaterials
{
    /// <summary>The most materials a run registers, the default among them.</summary>
    public const int MaxMaterials = 64;

    /// <summary>The highest friction a material may have.</summary>
    public const float MostFriction = 4;

    /// <summary>The default material's name.</summary>
    public const string DefaultName = "default";

    /// <summary>The most physics materials the voxels have, the default among them (each a static in a cell).</summary>
    public const int MostVoxelMaterials = 8;

    private readonly List<(string Name, PhysicsMaterial Material)> _materials = [(DefaultName, new(1, 0))];
    private readonly Dictionary<string, PhysicsMaterialId> _byName = new(StringComparer.Ordinal)
    {
        [DefaultName] = PhysicsMaterialId.Default,
    };

    private readonly byte[] _voxels = new byte[256];
    private ContactMaterial[]? _contacts;

    /// <summary>How many materials there are, the default among them.</summary>
    public int Count => _materials.Count;

    /// <summary>True once a match has begun: no material is registered from then on.</summary>
    public bool IsSealed => _contacts is not null;

    /// <summary>The material <paramref name="id"/> names.</summary>
    public PhysicsMaterial this[PhysicsMaterialId id] => _materials[id.Value].Material;

    /// <summary>
    /// Registers the material <paramref name="name"/> and returns its number. Throws for a name registered already, a
    /// full registry, a friction not 0 to <see cref="MostFriction"/>, a bounciness not 0 to 1, or once sealed.
    /// </summary>
    public PhysicsMaterialId Register(string name, PhysicsMaterial material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (IsSealed)
        {
            throw new InvalidOperationException(
                $"Physics material {name}: materials are registered before the first match (in an IRunStartup)."
            );
        }

        if (!(material.Friction is >= 0 and <= MostFriction) || !(material.Bounciness is >= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(material),
                material,
                $"A friction lies between 0 and {MostFriction}, a bounciness between 0 and 1."
            );
        }

        if (_byName.ContainsKey(name))
        {
            throw new ArgumentException($"A physics material named {name} is registered already.", nameof(name));
        }

        if (_materials.Count == MaxMaterials)
        {
            throw new InvalidOperationException($"At most {MaxMaterials} physics materials are registered.");
        }

        var id = new PhysicsMaterialId((byte)_materials.Count);
        _materials.Add((name, material));
        _byName.Add(name, id);
        return id;
    }

    /// <summary>
    /// Gives voxel material <paramref name="voxelMaterial"/> the physics material <paramref name="material"/> (default to
    /// undo it). Throws for air, a material not registered, a mapping that would give the voxels more than
    /// <see cref="MostVoxelMaterials"/> physics materials, or once sealed.
    /// </summary>
    public void MapVoxel(byte voxelMaterial, PhysicsMaterialId material)
    {
        if (IsSealed)
        {
            throw new InvalidOperationException(
                $"Voxel material {voxelMaterial}: voxels are mapped before the first match (in an IRunStartup)."
            );
        }

        if (voxelMaterial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(voxelMaterial), voxelMaterial, "Air has no physics material.");
        }

        if (material.Value >= _materials.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(material), material.Value, "No such physics material.");
        }

        var was = _voxels[voxelMaterial];
        _voxels[voxelMaterial] = material.Value;
        if (VoxelMaterialCount() > MostVoxelMaterials)
        {
            _voxels[voxelMaterial] = was;
            throw new InvalidOperationException(
                $"The voxels have at most {MostVoxelMaterials} physics materials, the default among them."
            );
        }
    }

    /// <summary>The physics material of voxel material <paramref name="voxelMaterial"/>.</summary>
    public PhysicsMaterialId OfVoxel(byte voxelMaterial) => new(_voxels[voxelMaterial]);

    /// <summary>How many physics materials the voxels have, the default among them (unless every one is mapped).</summary>
    public int VoxelMaterialCount()
    {
        Span<bool> used = stackalloc bool[MaxMaterials];
        var count = 0;
        for (var voxel = 1; voxel < _voxels.Length; voxel++)
        {
            if (!used[_voxels[voxel]])
            {
                used[_voxels[voxel]] = true;
                count++;
            }
        }

        return count;
    }

    /// <summary>The material named <paramref name="name"/>, if one is registered.</summary>
    public bool TryFind(string name, out PhysicsMaterialId id) => _byName.TryGetValue(name, out id);

    /// <summary>The name of material <paramref name="id"/>.</summary>
    public string NameOf(PhysicsMaterialId id) => _materials[id.Value].Name;

    /// <summary>Seals the registry (a match begins) and returns each material's contact settings, by number.</summary>
    internal ContactMaterial[] Seal()
    {
        if (_contacts is null)
        {
            var contacts = new ContactMaterial[_materials.Count];
            for (var i = 0; i < contacts.Length; i++)
            {
                contacts[i] = ContactMaterial.Of(_materials[i].Material);
            }

            _contacts = contacts;
        }

        return _contacts;
    }
}

/// <summary>
/// A material as the narrow phase uses it: the square root of its friction (two multiply to the pair's) and its
/// bounciness (a pair bounces by the larger; <see cref="PhysicsContacts"/> sets the bounce). Every material's contacts
/// are the engine's springs (30 Hz, critically damped, 2 m/s), so a bouncy body does not sink into what it hits.
/// </summary>
internal readonly record struct ContactMaterial(float RootFriction, float Bounciness)
{
    /// <summary>The engine's contact springs: 30 Hz, critically damped.</summary>
    public static SpringSettings Spring => new(30, 1);

    /// <summary>The engine's most speed a contact pushes bodies apart at, metres a second.</summary>
    public const float MaximumRecoveryVelocity = 2;

    /// <summary>The contact settings of the engine's material alone, for a world with no registry.</summary>
    public static ContactMaterial[] DefaultTable => [Of(new PhysicsMaterial(1, 0))];

    /// <summary>A material's contact settings.</summary>
    public static ContactMaterial Of(PhysicsMaterial material) =>
        new(MathF.Sqrt(material.Friction), material.Bounciness);
}
