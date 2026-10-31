using System.Reflection;
using System.Runtime.CompilerServices;
using Arch.Core;

namespace Itera.TestSupport;

/// <summary>
/// Makes Arch safe for test classes that run in parallel, before any test runs. Arch 2.1.0 has two static parts that are
/// not thread-safe (checked against its source, 2026-10-04):
/// <list type="bullet">
/// <item>The component registry (<c>ComponentRegistry</c>), a dictionary with no lock that <c>Component&lt;T&gt;</c>
/// adds a type to on its first use: two classes first using two new types at once could corrupt it. So every value type
/// of the engine's and the game's assemblies and of the test assembly is registered here, once, on one thread; a test
/// then only reads the registry.</item>
/// <item><see cref="World.Worlds"/>, which <see cref="World.Create(int, int, int, int)"/> locks while it may replace it
/// with a larger array, so a second thread could lock the new array while the first still works on the old. So the array
/// is given room for <see cref="WorldSlots"/> worlds here and never grows.</item>
/// </list>
/// Until 2026-10-04 every test class that used Arch ran in one serial collection instead, and that collection was the
/// suite's whole wall time.
/// </summary>
internal static class ArchStartup
{
    /// <summary>The worlds that may exist at once, far more than the suite makes.</summary>
    public const int WorldSlots = 1 << 16;

    [ModuleInitializer]
    internal static void Start()
    {
        RoomForWorlds();
        var test = typeof(ArchStartup).Assembly;
        var assemblies = test.GetReferencedAssemblies()
            .Where(name =>
                name.Name is { } text && (text.StartsWith("Itera.", StringComparison.Ordinal) || text == "Game")
            )
            .Select(Assembly.Load)
            .Append(test);
        foreach (var assembly in assemblies)
        {
            foreach (var type in TypesOf(assembly))
            {
                if (IsComponentCandidate(type))
                {
                    RuntimeHelpers.RunClassConstructor(typeof(Component<>).MakeGenericType(type).TypeHandle);
                }
            }
        }
    }

    /// <summary>A struct that Arch can store: not an enum, a primitive, a ref struct, or an open generic.</summary>
    private static bool IsComponentCandidate(Type type) =>
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

    /// <summary>Gives <see cref="World.Worlds"/> room for <see cref="WorldSlots"/> worlds, so it never grows while tests run.</summary>
    private static void RoomForWorlds()
    {
        var setter =
            typeof(World)
                .GetProperty(nameof(World.Worlds), BindingFlags.Public | BindingFlags.Static)
                ?.GetSetMethod(true)
            ?? throw new InvalidOperationException(
                "Arch's World.Worlds has no setter: re-check ArchStartup against Arch."
            );
        var worlds = World.Worlds;
        if (worlds.Length < WorldSlots)
        {
            Array.Resize(ref worlds, WorldSlots);
            setter.Invoke(null, [worlds]);
        }
    }
}
