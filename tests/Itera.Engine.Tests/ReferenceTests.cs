namespace Itera.Engine.Tests;

/// <summary>
/// The engine's generated reference (<c>docs/reference/engine-*.md</c>) is what the code makes now: every option under
/// <c>Engine</c>, with its default, the values its validator takes, and its doc comment; and every rules file of the
/// framework, with what it covers and the paths that load it. A game's reference tests list
/// its own options, its console commands, systems, tunables, replicated kinds, and save sections.
/// </summary>
public sealed class ReferenceTests
{
    [Fact]
    public void The_engines_rules_files_are_listed_as_they_are()
    {
        RepositoryDocs.AssertGenerated(
            RepositoryDocs.Generated(
                "docs/reference/engine-rules.md",
                ReferenceDocs.File(
                    "The engine's rules files",
                    "Every rules file of the framework (`.claude/rules/`), what it covers, and the paths whose files load it. Read an area's file before planning, discussing, or reviewing work in it; a game's own are in its `GAME.md`.",
                    ReferenceDocs.RulesTable(
                        RepositoryDocs.RulesFiles.Where(rules =>
                            !Path.GetFileName(rules).StartsWith("game-", StringComparison.Ordinal)
                        )
                    )
                )
            )
        );
    }

    [Fact]
    public void The_engines_options_are_listed_as_the_code_has_them()
    {
        var validator = new EngineOptionsValidator([]);
        RepositoryDocs.AssertGenerated(
            RepositoryDocs.Generated(
                "docs/reference/engine-options.md",
                ReferenceDocs.File(
                    "The engine's options",
                    "Every option under `Engine`: its default, the least and most its validator takes (each tried alone from the defaults; its text names any values between that it refuses), and what it does. A game's `appsettings.json` may ship other values (its `game-options.md`).",
                    ReferenceDocs.OptionsTable(
                        "Engine",
                        typeof(EngineOptions),
                        options => validator.Validate(null, (EngineOptions)options).Failures ?? []
                    )
                )
            )
        );
    }
}
