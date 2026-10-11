using Itera.Engine.Commands;
using Itera.Engine.Matches;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Matches;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Commands;

/// <summary>
/// The console's <c>physics</c> (<see cref="PhysicsCommands"/>): with no match it says so; in one it prints the bodies,
/// statics, solver, damping, and gravity, sets the solver and the damping for the match, and refuses values out of range;
/// it lists the run's physics materials with the voxel materials mapped to them.
/// </summary>
public sealed class PhysicsCommandsTests
{
    [Fact]
    public void With_no_match_it_says_so()
    {
        using var provider = EngineComposition.Build(terrain: false);
        var commands = provider.GetRequiredService<ConsoleCommands>();
        provider.GetRequiredService<PhysicsCommands>().Register();
        var output = new Output();

        commands.Execute("physics", output);

        Assert.Equal("no match: physics belongs to a match", Assert.Single(output.Errors));
    }

    [Fact]
    public void In_a_match_it_prints_and_sets_the_solver_and_damping()
    {
        using var provider = EngineComposition.Build(terrain: false);
        var commands = provider.GetRequiredService<ConsoleCommands>();
        provider.GetRequiredService<PhysicsCommands>().Register();
        using var match = new HeadlessRun(provider).Play(seed: 1);
        var physics = match.Match.Physics;
        var output = new Output();

        commands.Execute("physics", output);
        commands.Execute("physics solver 12 3", output);
        commands.Execute("physics damping 0.1 0.2", output);
        commands.Execute("physics solver 0 3", output);
        commands.Execute("physics damping 2 0", output);

        Assert.StartsWith("physics: ", output.Lines[0], StringComparison.Ordinal);
        Assert.Contains(
            "solver 8 iterations, 1 substep(s); damping 0.03 linear, 0.03 angular; gravity (0, -9.81, 0)",
            output.Lines[0],
            StringComparison.Ordinal
        );
        Assert.Contains("solver 12 iterations, 3 substep(s)", output.Lines[1], StringComparison.Ordinal);
        Assert.Contains("damping 0.1 linear, 0.2 angular", output.Lines[2], StringComparison.Ordinal);
        Assert.Equal(
            (12, 3, 0.1f, 0.2f),
            (physics.VelocityIterations, physics.Substeps, physics.LinearDamping, physics.AngularDamping)
        );
        Assert.Equal(2, output.Errors.Count(line => line.StartsWith("usage: ", StringComparison.Ordinal)));
    }

    [Fact]
    public void It_lists_the_materials_with_the_voxel_materials_mapped_to_them()
    {
        using var provider = EngineComposition.Build(terrain: false);
        var commands = provider.GetRequiredService<ConsoleCommands>();
        provider.GetRequiredService<PhysicsCommands>().Register();
        var materials = provider.GetRequiredService<PhysicsMaterials>();
        var ice = materials.Register("ice", new PhysicsMaterial(0.03f, 0));
        materials.Register("rubber", new PhysicsMaterial(1, 0.8f));
        materials.MapVoxel(7, ice);
        materials.MapVoxel(9, ice);
        var output = new Output();

        commands.Execute("physics materials", output);

        Assert.Equal(
            [
                "default: friction 1, bounciness 0",
                "ice: friction 0.03, bounciness 0; voxels 7, 9",
                "rubber: friction 1, bounciness 0.8",
            ],
            output.Lines
        );
    }

    private sealed class Output : IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public List<string> Errors { get; } = [];

        public void Print(string text) => Lines.Add(text);

        public void PrintError(string text) => Errors.Add(text);
    }
}
