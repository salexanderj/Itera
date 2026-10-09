using Xunit.Abstractions;

namespace Itera.TestSupport;

/// <summary>
/// Starts the test classes marked <see cref="RunsLongAttribute"/> first, then the rest by name. Every class is a
/// collection of its own and they run in parallel, so a long class started late finishes the run alone. Each project
/// that compiles this names it in its own <c>TestCollectionOrderer</c> attribute.
/// </summary>
public sealed class LongFirstCollectionOrderer : ITestCollectionOrderer
{
    private const string ClassPrefix = "Test collection for ";

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections
            .OrderBy(collection => RunsLong(collection) ? 0 : 1)
            .ThenBy(collection => collection.DisplayName, StringComparer.Ordinal);

    private static bool RunsLong(ITestCollection collection)
    {
        if (!collection.DisplayName.StartsWith(ClassPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var type = collection.TestAssembly.Assembly.GetType(collection.DisplayName[ClassPrefix.Length..]);
        return type?.GetCustomAttributes(typeof(RunsLongAttribute).AssemblyQualifiedName).Any() == true;
    }
}
