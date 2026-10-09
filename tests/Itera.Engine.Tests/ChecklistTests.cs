using System.Reflection;
using Itera.Engine.Ecs;

namespace Itera.Engine.Tests;

/// <summary>
/// CLAUDE.md's checklists as tests, for the engine: every numeric option is checked by <see cref="EngineOptionsValidator"/>
/// (a value far out of range fails and names the key), and every system has a test class with an allocation test.
/// </summary>
public sealed class ChecklistTests
{
    /// <summary>Numeric options that take any value of their type on purpose.</summary>
    private static readonly HashSet<string> s_anyValue = new(StringComparer.Ordinal)
    {
        "Engine:Voxels:Seed",
        "Engine:Steam:AppId",
    };

    [Fact]
    public void Every_numeric_option_has_a_range()
    {
        var validator = new EngineOptionsValidator([]);
        var unranged = new List<string>();
        foreach (var (key, path) in NumericOptions("Engine", typeof(EngineOptions), []))
        {
            if (s_anyValue.Contains(key))
            {
                continue;
            }

            foreach (var value in OutOfRange(path[^1].PropertyType))
            {
                var options = new EngineOptions();
                Set(options, path, value);
                var failures = validator.Validate(null, options).Failures ?? [];
                if (!failures.Any(failure => failure.StartsWith(key, StringComparison.Ordinal)))
                {
                    unranged.Add($"{key} = {value}");
                }
            }
        }

        Assert.True(unranged.Count == 0, "No range check for: " + string.Join(", ", unranged));
    }

    [Fact]
    public void Every_system_has_a_test_class_with_an_allocation_test()
    {
        Assert.Empty(MissingSystemTests(typeof(EcsWorld).Assembly, typeof(ChecklistTests).Assembly));
    }

    /// <summary>Each system of <paramref name="systems"/> without a <c>NameTests</c> class that has an allocation test.</summary>
    internal static List<string> MissingSystemTests(Assembly systems, Assembly tests)
    {
        var byName = tests.GetTypes().ToLookup(type => type.Name, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var system in Systems(systems))
        {
            var classes = byName[system.Name + "Tests"].ToList();
            if (classes.Count == 0)
            {
                missing.Add(system.Name + ": no " + system.Name + "Tests");
            }
            else if (
                !classes.Any(test =>
                    test.GetMethods().Any(method => method.Name.Contains("alloc", StringComparison.OrdinalIgnoreCase))
                )
            )
            {
                missing.Add(system.Name + "Tests: no allocation test");
            }
        }

        return missing;
    }

    /// <summary>The fixed and frame systems an assembly defines.</summary>
    internal static IEnumerable<Type> Systems(Assembly assembly) =>
        assembly
            .GetTypes()
            .Where(type =>
                type is { IsClass: true, IsAbstract: false }
                && (
                    typeof(IFixedUpdateSystem).IsAssignableFrom(type)
                    || typeof(IFrameUpdateSystem).IsAssignableFrom(type)
                )
            );

    /// <summary>Every numeric option's key and the properties that lead to it.</summary>
    private static IEnumerable<(string Key, PropertyInfo[] Path)> NumericOptions(
        string prefix,
        Type type,
        PropertyInfo[] path
    )
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            var key = prefix + ":" + property.Name;
            PropertyInfo[] here = [.. path, property];
            var kind = property.PropertyType;
            if (
                kind == typeof(int)
                || kind == typeof(float)
                || kind == typeof(double)
                || kind == typeof(uint)
                || kind == typeof(ulong)
            )
            {
                yield return (key, here);
            }
            else if (kind.IsClass && kind != typeof(string))
            {
                foreach (var inner in NumericOptions(key, kind, here))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>Values no option's range takes: the type's largest, and for decimals also NaN.</summary>
    private static object[] OutOfRange(Type type) =>
        type == typeof(int) ? [int.MaxValue, int.MinValue]
        : type == typeof(float) ? [float.MaxValue, -float.MaxValue, float.NaN]
        : type == typeof(double) ? [double.MaxValue, -double.MaxValue, double.NaN]
        : type == typeof(uint) ? [uint.MaxValue]
        : [ulong.MaxValue];

    private static void Set(object root, PropertyInfo[] path, object value)
    {
        var owner = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            owner = path[i].GetValue(owner)!;
        }

        path[^1].SetValue(owner, value);
    }
}
