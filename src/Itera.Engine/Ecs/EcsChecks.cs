using System.Diagnostics;
using Arch.Core;

namespace Itera.Engine.Ecs;

/// <summary>
/// Precondition checks for Arch calls that Arch does not check itself. The Arch package is a Release
/// build: a stale entity handle, an Add of a component the entity already has, or a Get of a missing
/// component corrupts the world or reads the wrong memory without an exception. Each check runs only
/// when the calling assembly is built in Debug, and throws <see cref="InvalidOperationException"/>.
/// </summary>
public static class EcsChecks
{
    /// <summary>Throws when the entity is not alive in the world (stale, destroyed, or default).</summary>
    [Conditional("DEBUG")]
    public static void RequireAlive(World world, Entity entity)
    {
        if (!world.IsAlive(entity))
        {
            throw new InvalidOperationException(
                $"{entity} is not alive in world {world.Id}. The handle is stale, destroyed, or was never created."
            );
        }
    }

    /// <summary>Throws when the entity is not alive or does not have <typeparamref name="T"/>.</summary>
    [Conditional("DEBUG")]
    public static void RequireHas<T>(World world, Entity entity)
    {
        RequireAlive(world, entity);
        if (!world.Has<T>(entity))
        {
            throw new InvalidOperationException($"{entity} does not have a {typeof(T).Name} component.");
        }
    }

    /// <summary>Throws when the entity is not alive or already has <typeparamref name="T"/>.</summary>
    [Conditional("DEBUG")]
    public static void RequireMissing<T>(World world, Entity entity)
    {
        RequireAlive(world, entity);
        if (world.Has<T>(entity))
        {
            throw new InvalidOperationException($"{entity} already has a {typeof(T).Name} component.");
        }
    }

    /// <summary>
    /// Throws when the description does not guarantee every component type a query reads. Arch reads a
    /// component's array without checking that the matched archetype has it, so each type in the
    /// lambda, <c>IForEach</c>, or chunk loop must appear in <c>WithAll</c> or <c>WithExclusive</c>.
    /// </summary>
    [Conditional("DEBUG")]
    public static void RequireAll<T0>(in QueryDescription description)
    {
        RequireGuaranteed(description, Component<T0>.ComponentType);
    }

    /// <inheritdoc cref="RequireAll{T0}(in QueryDescription)"/>
    [Conditional("DEBUG")]
    public static void RequireAll<T0, T1>(in QueryDescription description)
    {
        RequireGuaranteed(description, Component<T0>.ComponentType);
        RequireGuaranteed(description, Component<T1>.ComponentType);
    }

    /// <inheritdoc cref="RequireAll{T0}(in QueryDescription)"/>
    [Conditional("DEBUG")]
    public static void RequireAll<T0, T1, T2>(in QueryDescription description)
    {
        RequireGuaranteed(description, Component<T0>.ComponentType);
        RequireGuaranteed(description, Component<T1>.ComponentType);
        RequireGuaranteed(description, Component<T2>.ComponentType);
    }

    /// <inheritdoc cref="RequireAll{T0}(in QueryDescription)"/>
    [Conditional("DEBUG")]
    public static void RequireAll<T0, T1, T2, T3>(in QueryDescription description)
    {
        RequireGuaranteed(description, Component<T0>.ComponentType);
        RequireGuaranteed(description, Component<T1>.ComponentType);
        RequireGuaranteed(description, Component<T2>.ComponentType);
        RequireGuaranteed(description, Component<T3>.ComponentType);
    }

    private static void RequireGuaranteed(in QueryDescription description, ComponentType type)
    {
        if (Contains(description.All, type) || Contains(description.Exclusive, type))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The query reads {type.Type.Name} but its description does not require it with WithAll or WithExclusive."
        );
    }

    private static bool Contains(Signature signature, ComponentType type)
    {
        foreach (var candidate in signature.Components)
        {
            if (candidate == type)
            {
                return true;
            }
        }

        return false;
    }
}
