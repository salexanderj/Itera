using System.Reflection;
using System.Runtime.CompilerServices;
using Arch.Core;

namespace Itera.Engine.Ecs;

/// <summary>
/// Registers with Arch every struct an assembly defines that could be a component, on the calling thread, before any
/// system runs (<see cref="EcsWorld.Initialize"/>, for the systems' assemblies). Arch registers a type at its first use,
/// in a static registry with no lock, so two threads that each used a new type at once could corrupt it: with every type
/// registered first, a pass on the step's workers (<c>StepWorkers</c>) registers none. Each assembly once a process.
/// </summary>
internal static class ComponentTypes
{
    private static readonly object s_lock = new();
    private static readonly HashSet<Assembly> s_registered = [];

    /// <summary>Registers the component types of each of <paramref name="assemblies"/> not registered yet.</summary>
    public static void Register(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        lock (s_lock)
        {
            foreach (var assembly in assemblies)
            {
                if (!s_registered.Add(assembly))
                {
                    continue;
                }

                foreach (var type in TypesOf(assembly))
                {
                    if (IsCandidate(type))
                    {
                        RuntimeHelpers.RunClassConstructor(typeof(Component<>).MakeGenericType(type).TypeHandle);
                    }
                }
            }
        }
    }

    /// <summary>A struct Arch can store: not an enum, a primitive, a ref struct, an open generic, or the compiler's own.</summary>
    private static bool IsCandidate(Type type) =>
        type.IsValueType
        && !type.IsEnum
        && !type.IsPrimitive
        && !type.IsByRefLike
        && !type.ContainsGenericParameters
        && !type.Name.StartsWith('<')
        && type.GetCustomAttribute<CompilerGeneratedAttribute>() is null;

    private static Type[] TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.OfType<Type>()];
        }
    }
}
