using Itera.Engine.Matches;
using Itera.Engine.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Tests;

/// <summary>
/// The game's composition and its run (<see cref="GameComposition.Build"/>, <see cref="HeadlessRun"/>), made once for
/// the tests of a class that takes it as an <c>IClassFixture</c> instead of once a test: they cost a test that reads the
/// run's registries, or plays a match on the test area, most of its time. A test that needs a match plays its own from
/// <see cref="Run"/>. xunit runs a class's tests one after another. Each starts with <see cref="Reset"/>, and restores any
/// other run-wide state it changes (a key's binding, a setting, sounds queued). A class that needs other options makes
/// a fixture of its own over them (a subclass).
/// </summary>
public class GameRunFixture : IDisposable
{
    /// <summary>The options most tests play with: one physics thread, so the steps repeat, and no audio or Steam.</summary>
    public static readonly IReadOnlyDictionary<string, string?> TestOptions = new Dictionary<string, string?>
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
        ["Engine:Voxels:WorkerThreads"] = "4",
    };

    public GameRunFixture()
        : this(TestOptions) { }

    /// <summary>A run of the game's composition over <paramref name="options"/>.</summary>
    protected GameRunFixture(IReadOnlyDictionary<string, string?> options)
    {
        Provider = GameComposition.Build(options);
        Run = new HeadlessRun(Provider);
    }

    /// <summary>The run's services.</summary>
    public ServiceProvider Provider { get; }

    /// <summary>The run, whose registries are made; it plays a test's matches.</summary>
    public HeadlessRun Run { get; }

    /// <summary>A service of the run.</summary>
    public T Get<T>()
        where T : notnull => Provider.GetRequiredService<T>();

    /// <summary>Every tunable back to its default, as each test starts.</summary>
    public void Reset()
    {
        var tunables = Provider.GetRequiredService<Tunables>();
        tunables.Reset(TuningScope.Local);
        tunables.Reset(TuningScope.Match);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Provider.Dispose();
        }
    }
}
