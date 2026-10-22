using System.Text.RegularExpressions;

namespace Game.Tests;

/// <summary>
/// The game's docs kept by the <c>docs</c> skill's rules (<c>.claude/skills/docs/SKILL.md</c>), as tests: what every
/// session loads at its start under <see cref="RepositoryDocs.SessionStartLimit"/> characters and <c>GAME.md</c> under
/// its size, opening any of the game's files loads at most <see cref="RepositoryDocs.OpenedFileLimit"/> characters of
/// rules, each of the game's rules files (<c>.claude/rules/game-*.md</c>) loads with paths that name files and gives no
/// dates, no sentence of <c>GAME.md</c> or a rules file runs over <see cref="RepositoryDocs.SentenceLimit"/> words, and
/// every path and code name they give is there. The engine's tests check the framework's docs the same way.
/// </summary>
public sealed class DocumentationTests
{
    /// <summary>The characters of <c>GAME.md</c>, the game's share of what a session loads at its start.</summary>
    private const int GameMdLimit = 10_000;

    private static readonly Lazy<HashSet<string>> s_codeNames = new(() =>
        RepositoryDocs.CodeNames(
            typeof(GameServiceCollectionExtensions).Assembly,
            typeof(object).Assembly,
            typeof(Regex).Assembly
        )
    );

    /// <summary>The game's rules files.</summary>
    private static List<string> RulesFiles =>
        [
            .. RepositoryDocs.RulesFiles.Where(file =>
                Path.GetFileName(file).StartsWith("game-", StringComparison.Ordinal)
            ),
        ];

    /// <summary>The game's docs an agent loads: <c>GAME.md</c> and its rules files.</summary>
    private static List<string> Docs => ["GAME.md", .. RulesFiles];

    [Fact]
    public void What_a_session_loads_at_its_start_stays_under_its_size() => RepositoryDocs.AssertNone(Sizes());

    [Fact]
    public void Opening_a_file_of_the_game_loads_at_most_32_KB_of_rules() => RepositoryDocs.AssertNone(Loads());

    [Fact]
    public void Every_rules_file_loads_with_paths_that_name_files() => RepositoryDocs.AssertNone(RulesPaths());

    [Fact]
    public void The_rules_files_give_no_dates() => RepositoryDocs.AssertNone(Dates());

    [Fact]
    public void No_sentence_runs_over_60_words() => RepositoryDocs.AssertNone(LongSentences());

    [Fact]
    public void Every_path_and_code_name_a_doc_gives_is_there() => RepositoryDocs.AssertNone(Names());

    private static IEnumerable<(string, string)> Sizes() =>
        RepositoryDocs.SessionStartSize().Concat(RepositoryDocs.Size("GAME.md", GameMdLimit, RepositoryDocs.LineLimit));

    private static IEnumerable<(string, string)> Loads() =>
        RepositoryDocs.Loads(file =>
            file.StartsWith("src/Game/", StringComparison.Ordinal)
            || file.StartsWith("tests/Game.Tests/", StringComparison.Ordinal)
        );

    private static IEnumerable<(string, string)> RulesPaths() => RepositoryDocs.RulesPaths(RulesFiles);

    private static IEnumerable<(string, string)> Dates() => RepositoryDocs.DatesGiven(RulesFiles);

    private static IEnumerable<(string, string)> LongSentences() => RepositoryDocs.LongSentences(Docs);

    private static IEnumerable<(string, string)> Names() => RepositoryDocs.NamesNotThere(Docs, s_codeNames.Value);
}
