using System.Reflection;
using Itera.Engine.Commands;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Tests;

/// <summary>
/// CLAUDE.md's checklists as tests, for the game: every console command the running game has (the engine's, its
/// systems', and the game's) is in the run-game skill's list, every numeric game option is checked by its validator, and
/// every game system has a test class with an allocation test.
/// </summary>
public sealed class ChecklistTests
{
    [Fact]
    public void Every_console_command_is_in_the_run_game_skill()
    {
        var skill = File.ReadAllText(Path.Combine(RepositoryRoot(), ".claude", "skills", "run-game", "SKILL.md"));
        using var provider = GameComposition.Build(
            new Dictionary<string, string?> { ["Engine:Audio:Enabled"] = "false", ["Engine:Steam:Enabled"] = "false" }
        );
        // The run's commands, as GameLoop registers them at its UI step.
        provider.GetRequiredService<EngineCommands>().Register();
        provider.GetRequiredService<TimeCommands>().Register();
        provider.GetRequiredService<MatchCommands>().Register();
        provider.GetRequiredService<SettingsCommands>().Register();
        provider.GetRequiredService<ParticleCommands>().Register();
        provider.GetRequiredService<SoundCommands>().Register();
        // The match's: its systems' and the ECS world's.
        using var match = new HeadlessRun(provider).Play(seed: 1);

        var commands = provider.GetRequiredService<ConsoleCommands>().All.Select(command => command.Name).ToList();
        var missing = commands.Where(name => !skill.Contains($"`{name}`", StringComparison.Ordinal)).ToList();

        Assert.True(commands.Count > 30, $"only {commands.Count} commands");
        Assert.True(missing.Count == 0, "Not in .claude/skills/run-game/SKILL.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_numeric_game_option_has_a_range()
    {
        var pairs = new (object Options, object Validator, string Section)[]
        {
            (new PlayerOptions(), new PlayerOptionsValidator(), PlayerOptions.SectionName),
            (new WorldOptions(), new WorldOptionsValidator(), WorldOptions.SectionName),
            (new MinimapOptions(), new MinimapOptionsValidator(), MinimapOptions.SectionName),
        };
        var unranged = new List<string>();
        foreach (var (defaults, validator, section) in pairs)
        {
            foreach (var property in defaults.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var kind = property.PropertyType;
                object[] values =
                    kind == typeof(float) ? [float.MaxValue, -float.MaxValue, float.NaN]
                    : kind == typeof(double) ? [double.MaxValue, -double.MaxValue, double.NaN]
                    : kind == typeof(int) ? [int.MaxValue, int.MinValue]
                    : [];
                foreach (var value in values)
                {
                    var options = Activator.CreateInstance(defaults.GetType())!;
                    property.SetValue(options, value);
                    var result = (ValidateOptionsResult)
                        validator.GetType().GetMethod("Validate")!.Invoke(validator, [null, options])!;
                    var key = section + ":" + property.Name;
                    if (!(result.Failures ?? []).Any(failure => failure.StartsWith(key, StringComparison.Ordinal)))
                    {
                        unranged.Add($"{key} = {value}");
                    }
                }
            }
        }

        Assert.True(unranged.Count == 0, "No range check for: " + string.Join(", ", unranged));
    }

    [Fact]
    public void Every_game_system_has_a_test_class_with_an_allocation_test()
    {
        var tests = typeof(ChecklistTests).Assembly.GetTypes().ToLookup(type => type.Name, StringComparer.Ordinal);
        var missing = new List<string>();
        var systems = typeof(GameRunStartup)
            .Assembly.GetTypes()
            .Where(type =>
                type is { IsClass: true, IsAbstract: false }
                && (
                    typeof(IFixedUpdateSystem).IsAssignableFrom(type)
                    || typeof(IFrameUpdateSystem).IsAssignableFrom(type)
                )
            )
            .ToList();
        foreach (var system in systems)
        {
            var classes = tests[system.Name + "Tests"].ToList();
            if (
                !classes.Any(test =>
                    test.GetMethods().Any(method => method.Name.Contains("alloc", StringComparison.OrdinalIgnoreCase))
                )
            )
            {
                missing.Add(system.Name);
            }
        }

        Assert.NotEmpty(systems);
        Assert.True(missing.Count == 0, "No NameTests class with an allocation test: " + string.Join(", ", missing));
    }

    /// <summary>The repository's root: the nearest folder up from the tests that holds <c>.claude</c>.</summary>
    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, ".claude")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("No .claude folder above the tests.");
    }
}
