using System.Text.RegularExpressions;

namespace Itera.Engine.Tests;

/// <summary>
/// The framework's docs kept by the <c>docs</c> skill's rules (<c>.claude/skills/docs/SKILL.md</c>), as tests:
/// <c>CLAUDE.md</c> under its size, opening any of the framework's files loads at most
/// <see cref="RepositoryDocs.OpenedFileLimit"/> characters of rules, each rules file loads with paths that name files, each
/// skill's main file under its size, no dates in its docs, no sentence over <see cref="RepositoryDocs.SentenceLimit"/>
/// words, and every path and code name a doc gives is there. A game's tests check its own docs the same way.
/// </summary>
public sealed class DocumentationTests
{
    /// <summary>The characters of <c>CLAUDE.md</c>, the framework's share of what a session loads at its start.</summary>
    private const int ClaudeLimit = 15_000;

    private static readonly Lazy<HashSet<string>> s_codeNames = new(() =>
        RepositoryDocs.CodeNames(typeof(GameLoop).Assembly, typeof(object).Assembly, typeof(Regex).Assembly)
    );

    /// <summary>The framework's docs: <c>CLAUDE.md</c>, <c>ARCHITECTURE.md</c>, and its rules files, skills, and agents.</summary>
    private static List<string> Docs =>
        [
            .. RepositoryDocs.Files.Where(file =>
                file is "CLAUDE.md" or "ARCHITECTURE.md"
                || (RepositoryDocs.RulesFiles.Contains(file) && !IsGames(file))
                || (
                    RepositoryDocs.IsMarkdown(file)
                    && (
                        file.StartsWith(".claude/skills/", StringComparison.Ordinal)
                        || file.StartsWith(".claude/agents/", StringComparison.Ordinal)
                    )
                )
            ),
        ];

    [Fact]
    public void CLAUDE_md_and_each_skills_main_file_stay_under_their_sizes() => RepositoryDocs.AssertNone(Sizes());

    [Fact]
    public void Opening_a_file_of_the_framework_loads_at_most_32_KB_of_rules() => RepositoryDocs.AssertNone(Loads());

    [Fact]
    public void Every_rules_file_loads_with_paths_that_name_files() => RepositoryDocs.AssertNone(RulesPaths());

    [Fact]
    public void The_docs_give_no_dates() => RepositoryDocs.AssertNone(Dates());

    [Fact]
    public void No_sentence_runs_over_60_words() => RepositoryDocs.AssertNone(LongSentences());

    [Fact]
    public void Every_path_and_code_name_a_doc_gives_is_there() => RepositoryDocs.AssertNone(Names());

    private static bool IsGames(string file) => Path.GetFileName(file).StartsWith("game-", StringComparison.Ordinal);

    private static IEnumerable<(string, string)> Sizes() =>
        RepositoryDocs.Size("CLAUDE.md", ClaudeLimit, RepositoryDocs.LineLimit).Concat(RepositoryDocs.SkillSizes(Docs));

    private static IEnumerable<(string, string)> Loads() => RepositoryDocs.Loads(IsFrameworks);

    private static IEnumerable<(string, string)> RulesPaths() =>
        RepositoryDocs.RulesPaths(RepositoryDocs.RulesFiles.Where(rules => !IsGames(rules)));

    private static IEnumerable<(string, string)> Dates() => RepositoryDocs.DatesGiven(Docs);

    private static IEnumerable<(string, string)> LongSentences() => RepositoryDocs.LongSentences(Docs);

    private static IEnumerable<(string, string)> Names() => RepositoryDocs.NamesNotThere(Docs, s_codeNames.Value);

    // The framework's files: the engine's, the modding contract's, the samples', the tools', and their tests.
    private static bool IsFrameworks(string file) =>
        (
            file.StartsWith("src/", StringComparison.Ordinal)
            || file.StartsWith("tests/", StringComparison.Ordinal)
            || file.StartsWith("tools/", StringComparison.Ordinal)
            || file.StartsWith("samples/", StringComparison.Ordinal)
        )
        && !file.StartsWith("src/Game/", StringComparison.Ordinal)
        && !file.StartsWith("tests/Game.Tests/", StringComparison.Ordinal);
}
