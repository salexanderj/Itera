namespace Itera.TestSupport;

/// <summary>
/// Test classes that must run alone, after every parallel class has finished: those that read the whole process, such as
/// a garbage collection that must find a match's objects gone, or timings that other tests' load would stretch. Every
/// other class runs in parallel; Arch is made safe for that before any test runs (<see cref="ArchStartup"/>). This file
/// is compiled into each test project; a collection belongs to one assembly.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollectionDefinition
{
    public const string Name = "Serial";
}
