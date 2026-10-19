using System.Globalization;
using Itera.Engine;
using Itera.Engine.Commands;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Replication;
using Itera.Engine.Saves;
using Itera.Engine.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Tests;

/// <summary>
/// The game's generated reference (<c>docs/reference/game-*.md</c>) is what the code makes now: its options and the
/// engine's options its settings change, every console command, the systems in their run order, every tunable, the
/// replicated kinds, and the save sections, read from its composition and a match it plays.
/// </summary>
public sealed class ReferenceTests
{
    [Fact]
    public void The_games_reference_is_what_the_code_makes_now()
    {
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
        // The match's: its systems', its sections', and the ECS world's.
        using var match = new HeadlessRun(provider).Play(seed: 1);

        RepositoryDocs.AssertGenerated(
            RepositoryDocs.Generated("docs/reference/game-options.md", Options()),
            RepositoryDocs.Generated(
                "docs/reference/game-commands.md",
                ReferenceDocs.File(
                    "Console commands",
                    "Every command the running game has: the engine's, the game's, and its match's, with its arguments, where it may run, and its help. The console's `help` prints the same.",
                    ReferenceDocs.CommandsTable(provider.GetRequiredService<ConsoleCommands>().All)
                )
            ),
            RepositoryDocs.Generated(
                "docs/reference/game-systems.md",
                ReferenceDocs.File(
                    "Systems",
                    "The match's systems in their run order, as the game registers them (each registration's comment says why it sits there); with `Engine:Benchmark:Enabled` the benchmark's systems join them.",
                    "## Fixed, each step\n\n"
                        + ReferenceDocs.SystemsTable(
                            match.Services.GetServices<IFixedUpdateSystem>().Select(system => system.GetType())
                        ),
                    "## Frame, each frame drawn\n\n"
                        + ReferenceDocs.SystemsTable(
                            match.Services.GetServices<IFrameUpdateSystem>().Select(system => system.GetType())
                        )
                )
            ),
            RepositoryDocs.Generated(
                "docs/reference/game-tunables.md",
                ReferenceDocs.File(
                    "Tunables",
                    "Every tunable the run registers, by name: its default, its range, whether a host's value holds for its clients (match) or each game keeps its own (local), and what it sets. The console's `tune` lists and sets them.",
                    ReferenceDocs.TunablesTable(provider.GetRequiredService<Tunables>())
                )
            ),
            RepositoryDocs.Generated(
                "docs/reference/game-replication.md",
                ReferenceDocs.File(
                    "Replicated kinds",
                    "Every kind of entity a host sends its clients, by its number: whom it reaches, how fast its priority grows, its fields in order, and the class that reads and writes it.",
                    ReferenceDocs.KindsTable(provider.GetRequiredService<ReplicatedKinds>())
                )
            ),
            RepositoryDocs.Generated(
                "docs/reference/game-saves.md",
                ReferenceDocs.File(
                    "Save sections",
                    "Every section of the game's own state a save holds beside the world's edits: its name, its version, and the class that writes and reads it.",
                    ReferenceDocs.SavesTable(match.Services.GetServices<ISaveSection>())
                )
            )
        );
    }

    // The game's options by section, and the engine's options its settings file gives other values than the engine's.
    private static string Options()
    {
        var sections = new (string Section, Type Options, object Validator)[]
        {
            (PlayerOptions.SectionName, typeof(PlayerOptions), new PlayerOptionsValidator()),
            (WorldOptions.SectionName, typeof(WorldOptions), new WorldOptionsValidator()),
            (MinimapOptions.SectionName, typeof(MinimapOptions), new MinimapOptionsValidator()),
        };
        var tables = sections
            .Select(section =>
                $"## `{section.Section}`\n\n"
                + ReferenceDocs.OptionsTable(
                    section.Section,
                    section.Options,
                    options => Failures(section.Validator, options)
                )
            )
            .ToList();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();
        var engine = new EngineOptions();
        var changes = new List<(string, string, string)>();
        foreach (var (key, value) in configuration.GetSection("Engine").AsEnumerable(makePathsRelative: true))
        {
            var (found, defaults) = Default(engine, key);
            if (value is not null && found && !Same(defaults, value))
            {
                changes.Add(("Engine:" + key, ReferenceDocs.Value(defaults), $"`{value}`"));
            }
        }

        tables.Add(
            "## The engine's options the game's `appsettings.json` changes\n\n"
                + ReferenceDocs.ChangedOptionsTable(changes.OrderBy(change => change.Item1, StringComparer.Ordinal))
        );
        return ReferenceDocs.File(
            "The game's options",
            "The game's own options under `Game`, each with its default, the least and most its validator takes, and what it does; then the engine's options its `appsettings.json` ships at other values (the engine's are in `engine-options.md`).",
            [.. tables]
        );
    }

    // Whether a settings file's text is an option's default, read as the option's type (`1.0` is `1`).
    private static bool Same(object? defaults, string text)
    {
        if (defaults is null)
        {
            return text.Length == 0;
        }

        var type = defaults.GetType();
        try
        {
            var value = type.IsEnum
                ? Enum.Parse(type, text, ignoreCase: true)
                : Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
            return Equals(value, defaults);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or ArgumentException)
        {
            return false;
        }
    }

    private static IEnumerable<string> Failures(object validator, object options)
    {
        var result = (ValidateOptionsResult)
            validator.GetType().GetMethod("Validate")!.Invoke(validator, [null, options])!;
        return result.Failures ?? [];
    }

    // An option's default in code, found by its key's properties.
    private static (bool Found, object? Value) Default(object root, string key)
    {
        object? owner = root;
        foreach (var name in key.Split(':'))
        {
            var property = owner?.GetType().GetProperty(name);
            if (property is null)
            {
                return (false, null);
            }

            owner = property.GetValue(owner);
        }

        return (true, owner);
    }
}
