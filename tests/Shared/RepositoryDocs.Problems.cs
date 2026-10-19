namespace Itera.TestSupport;

/// <summary>The checks the documentation tests run, each as a list of a doc and its problem.</summary>
internal static partial class RepositoryDocs
{
    /// <summary>A doc past <paramref name="characters"/>, or past <paramref name="lines"/> when given.</summary>
    public static IEnumerable<(string Doc, string Problem)> Size(string doc, int characters, int? lines = null)
    {
        var text = Read(doc);
        if (text.Length > characters)
        {
            yield return (doc, $"{text.Length} characters, past {characters}");
        }

        var count = text.TrimEnd('\n').Split('\n').Length;
        if (lines is { } most && count > most)
        {
            yield return (doc, $"{count} lines, past {most}");
        }
    }

    /// <summary>The files every session loads at its start, past <see cref="SessionStartLimit"/> together.</summary>
    public static IEnumerable<(string Doc, string Problem)> SessionStartSize()
    {
        var files = SessionStart();
        var characters = files.Sum(file => Read(file).Length);
        if (characters > SessionStartLimit)
        {
            yield return (
                files.MaxBy(file => Read(file).Length)!,
                $"every session loads {characters} characters ({string.Join(", ", files)}), past {SessionStartLimit}"
            );
        }
    }

    /// <summary>Each skill's main file among <paramref name="docs"/> past <see cref="SkillLimit"/>.</summary>
    public static IEnumerable<(string Doc, string Problem)> SkillSizes(IEnumerable<string> docs) =>
        docs.Where(doc => doc.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .SelectMany(skill => Size(skill, SkillLimit))
            .Select(problem => (problem.Doc, problem.Problem + ": move its lists and recipes to files beside it"));

    /// <summary>
    /// Each file <paramref name="owns"/> takes whose rules files come to more than <see cref="OpenedFileLimit"/>: a
    /// problem of each of them in <paramref name="notYetCleaned"/>, or of the largest when none is.
    /// </summary>
    public static IEnumerable<(string Doc, string Problem)> Loads(
        Func<string, bool> owns,
        IReadOnlySet<string> notYetCleaned
    )
    {
        ArgumentNullException.ThrowIfNull(owns);
        ArgumentNullException.ThrowIfNull(notYetCleaned);
        foreach (var file in Files.Where(owns))
        {
            var (characters, rules) = Loaded(file);
            if (characters <= OpenedFileLimit)
            {
                continue;
            }

            var problem = $"opening {file} loads {characters} characters of rules ({string.Join(", ", rules)})";
            var uncleaned = rules.Where(notYetCleaned.Contains).ToList();
            foreach (var doc in uncleaned.Count > 0 ? uncleaned : [rules.MaxBy(rule => Read(rule).Length)!])
            {
                yield return (doc, problem);
            }
        }
    }

    /// <summary>Each of <paramref name="rulesFiles"/> with no paths, or with a path that matches no file.</summary>
    public static IEnumerable<(string Doc, string Problem)> RulesPaths(IEnumerable<string> rulesFiles)
    {
        foreach (var rules in rulesFiles)
        {
            var globs = Globs(rules);
            if (globs.Count == 0)
            {
                yield return (rules, "no paths: it would load at every session's start");
            }

            foreach (var glob in globs.SelectMany(Expanded))
            {
                var pattern = Glob(glob);
                if (!Files.Any(pattern.IsMatch))
                {
                    yield return (rules, $"its path {glob} matches no file (renamed or removed?)");
                }
            }
        }
    }

    /// <summary>Each date <paramref name="docs"/> give: they state what is true now, and the record keeps when.</summary>
    public static IEnumerable<(string Doc, string Problem)> DatesGiven(IEnumerable<string> docs) =>
        docs.SelectMany(doc => Dates(Read(doc)).Select(date => (doc, $"gives the date {date}")));

    /// <summary>Each sentence of <paramref name="docs"/> over <see cref="SentenceLimit"/> words.</summary>
    public static IEnumerable<(string Doc, string Problem)> LongSentences(IEnumerable<string> docs) =>
        docs.SelectMany(doc =>
            Sentences(Read(doc))
                .Where(sentence => Words(sentence) > SentenceLimit)
                .Select(sentence =>
                    (doc, $"a sentence of {Words(sentence)} words: \"{sentence[..Math.Min(70, sentence.Length)]}...\"")
                )
        );

    /// <summary>Each path or code name <paramref name="docs"/> give that is nothing (<see cref="UnknownNames"/>).</summary>
    public static IEnumerable<(string Doc, string Problem)> NamesNotThere(
        IEnumerable<string> docs,
        ISet<string> code
    ) =>
        docs.SelectMany(doc =>
            UnknownNames(doc, code)
                .Distinct(StringComparer.Ordinal)
                .Select(name => (doc, $"`{name}` is nothing in the repository"))
        );
}
