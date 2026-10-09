using Arch.Core;

namespace Itera.Engine.Ecs;

/// <summary>
/// Single-entity world operations that run <see cref="EcsChecks"/> first. In Release builds they are
/// the plain Arch calls. Use them for entity handles that did not come from the current query: stored
/// handles, handles from another system, and anything received from outside the ECS.
/// </summary>
public static class CheckedWorldExtensions
{
    /// <summary>Adds a component the entity must not have yet.</summary>
    public static void AddChecked<T>(this World world, Entity entity, in T component)
    {
        EcsChecks.RequireMissing<T>(world, entity);
        world.Add(entity, in component);
    }

    /// <summary>Removes a component the entity must have.</summary>
    public static void RemoveChecked<T>(this World world, Entity entity)
    {
        EcsChecks.RequireHas<T>(world, entity);
        world.Remove<T>(entity);
    }

    /// <summary>Returns a reference to a component the entity must have.</summary>
    public static ref T GetChecked<T>(this World world, Entity entity)
    {
        EcsChecks.RequireHas<T>(world, entity);
        return ref world.Get<T>(entity);
    }

    /// <summary>Overwrites a component the entity must have.</summary>
    public static void SetChecked<T>(this World world, Entity entity, in T component)
    {
        EcsChecks.RequireHas<T>(world, entity);
        world.Set(entity, in component);
    }

    /// <summary>Destroys an entity that must be alive. Arch's own Destroy on a stale handle destroys whichever entity reuses its id.</summary>
    public static void DestroyChecked(this World world, Entity entity)
    {
        EcsChecks.RequireAlive(world, entity);
        world.Destroy(entity);
    }
}
