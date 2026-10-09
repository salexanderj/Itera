namespace Itera.TestSupport;

/// <summary>
/// Marks a test class that takes tens of seconds, so <see cref="LongFirstCollectionOrderer"/> starts it before the
/// others: started late, it ran on alone at the end of the run. Mark a class whose tests take more than about 20 s in
/// all when run alone in Debug.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RunsLongAttribute : Attribute;
