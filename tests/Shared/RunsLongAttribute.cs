namespace Itera.TestSupport;

/// <summary>
/// Marks a test class that takes many seconds, so <see cref="LongFirstCollectionOrderer"/> starts it before the
/// others: started late, it runs on alone at the end of the run. Mark a class whose tests take more than about 5 s in
/// all in a run of the whole suite (<c>tools/tests/timings.py</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RunsLongAttribute : Attribute;
