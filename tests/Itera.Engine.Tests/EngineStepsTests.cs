using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests;

/// <summary>
/// The engine's start-up steps (<see cref="EngineSteps"/>): started in order and shut down in reverse; a
/// step that throws stops the start, and it and every step before it are shut down while the later ones are never
/// touched; a shutdown step that throws does not stop the others; graphics steps are skipped once the graphics are
/// abandoned; and a second shutdown does nothing.
/// </summary>
public sealed class EngineStepsTests
{
    [Fact]
    public void Steps_start_in_order_and_shut_down_in_reverse()
    {
        var calls = new List<string>();
        var phases = new List<string>();
        var steps = Steps(calls, "a", "b", "c");

        steps.Initialize(phases.Add);
        steps.Shutdown();

        Assert.Equal(["start a", "start b", "start c", "stop c", "stop b", "stop a"], calls);
        Assert.Equal(["a", "b", "c"], phases);
    }

    [Fact]
    public void A_step_that_fails_to_start_is_shut_down_with_those_before_it_and_the_rest_are_left()
    {
        var calls = new List<string>();
        var steps = Steps(calls, "a", "b!", "c");

        Assert.Throws<InvalidOperationException>(() => steps.Initialize(_ => { }));
        Assert.Equal(2, steps.Started);
        steps.Shutdown();

        Assert.Equal(["start a", "start b!", "stop b!", "stop a"], calls);
    }

    [Fact]
    public void A_shutdown_step_that_throws_does_not_stop_the_others()
    {
        var calls = new List<string>();
        var list = new List<EngineStep>
        {
            new("a", () => calls.Add("start a"), () => calls.Add("stop a")),
            new("b", () => calls.Add("start b"), () => throw new InvalidOperationException("stuck")),
            new("c", () => calls.Add("start c"), () => calls.Add("stop c")),
        };
        var steps = new EngineSteps(list, NullLogger.Instance);

        steps.Initialize(_ => { });
        steps.Shutdown();

        Assert.Equal(["start a", "start b", "start c", "stop c", "stop a"], calls);
    }

    [Fact]
    public void Graphics_steps_are_left_alone_once_the_graphics_are_abandoned_and_a_second_shutdown_does_nothing()
    {
        var calls = new List<string>();
        var list = new List<EngineStep>
        {
            new("window", () => { }, () => calls.Add("stop window"), Graphics: true),
            new("voxels", () => { }, () => calls.Add("stop voxels")),
            new("renderer", () => { }, () => calls.Add("stop renderer"), Graphics: true),
        };
        var steps = new EngineSteps(list, NullLogger.Instance);
        steps.Initialize(_ => { });

        steps.GraphicsAbandoned = true;
        steps.Shutdown();
        steps.Shutdown();

        Assert.Equal(["stop voxels"], calls);
        Assert.Equal(0, steps.Started);
    }

    /// <summary>Steps that record their calls; a name ending in "!" throws when it starts.</summary>
    private static EngineSteps Steps(List<string> calls, params string[] names) =>
        new(
            [
                .. names.Select(name => new EngineStep(
                    name,
                    () =>
                    {
                        calls.Add("start " + name);
                        if (name.EndsWith('!'))
                        {
                            throw new InvalidOperationException(name);
                        }
                    },
                    () => calls.Add("stop " + name)
                )),
            ],
            NullLogger.Instance
        );
}
