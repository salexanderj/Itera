using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Itera.TestSupport;

/// <summary>
/// The repository's docs as the documentation tests read them, by the rules of the <c>docs</c> skill
/// (<c>.claude/skills/docs/SKILL.md</c>): the files every session loads at its start, the rules files that opening a file
/// loads, a doc's sentences and dates, the paths and code names it gives, and the generated files in
/// <c>docs/reference/</c>. The engine's tests check the framework's docs with it, and a game's tests the game's.
/// </summary>
internal static partial class RepositoryDocs
{
    /// <summary>The characters every session loads at its start: <c>CLAUDE.md</c> and the files it imports.</summary>
    public const int SessionStartLimit = 25_000;

    /// <summary>The lines of a file every session loads, as Claude Code advises.</summary>
    public const int LineLimit = 200;

    /// <summary>The characters of the rules files that opening one file loads, together.</summary>
    public const int OpenedFileLimit = 32_000;

    /// <summary>The characters of a skill's main file (<c>SKILL.md</c>); its lists and recipes go in files beside it.</summary>
    public const int SkillLimit = 16_000;

    /// <summary>The words of one sentence.</summary>
    public const int SentenceLimit = 60;

    /// <summary>Set to 1, the generated files' tests write them instead of comparing them.</summary>
    public const string WriteGenerated = "ITERA_WRITE_REFERENCE";

    private const RegexOptions Options = RegexOptions.CultureInvariant;

    // Marks a library's root namespace among the code names (`@Arch`), so a doc may name its packages and parts.
    private const string LibraryMark = "@";

    private static readonly Regex s_date = new(@"\b(?:19|20)\d\d-\d\d-\d\d\b", Options);
    private static readonly Regex s_code = new("`([^`\n]+)`", Options);
    private static readonly Regex s_word = new("[A-Za-z_][A-Za-z0-9_]*", Options);
    private static readonly Regex s_sentenceEnd = new("(?<=[.!?])\\s+(?=[A-Z0-9(\"'*_\\[])", Options);
    private static readonly Regex s_listItem = new(@"^(?:[-*+]|\d+\.)\s", Options);
    private static readonly Regex s_tableRule = new(@"^\|[\s:|-]+\|?$", Options);
    private static readonly Regex s_link = new(@"\[([^\]]*)\]\([^)]*\)", Options);
    private static readonly Regex s_import = new(@"(?<=^|\s)@([\w./-]+\.md)\b", Options);

    // File kinds read for code names, besides C#: build files, data, shaders, and scripts.
    private static readonly string[] s_codeKinds =
    [
        ".cs",
        ".csproj",
        ".props",
        ".targets",
        ".json",
        ".glsl",
        ".vert",
        ".frag",
        ".comp",
        ".py",
        ".sh",
        ".env",
        ".yml",
        ".editorconfig",
        ".sln",
    ];

    // File kinds a doc names by file name alone (`FrameRenderer.cs`), which must be some file's name.
    private static readonly string[] s_namedKinds =
    [
        ".cs",
        ".md",
        ".glsl",
        ".vert",
        ".frag",
        ".comp",
        ".py",
        ".sh",
        ".csproj",
        ".props",
    ];

    // Folders a game's or the engine's paths are often given from (`Systems/NpcBrains.cs`).
    private static readonly string[] s_pathBases =
    [
        "src/Game",
        "src/Itera.Engine",
        "src/Itera.Modding",
        "tests/Game.Tests",
        "tests/Itera.Engine.Tests",
        "samples/Itera.MeshSample",
        "tools",
    ];

    /// <summary>The repository's root: the nearest folder up from the tests that holds <c>.claude</c>.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>
    /// Every file of the repository, as a path from its root with <c>/</c>, in ordinal order: not builds' output, git's
    /// folder, or agents' worktrees.
    /// </summary>
    public static IReadOnlyList<string> Files { get; } = ListFiles();

    /// <summary>Every rules file (<c>.claude/rules/</c>, at any depth).</summary>
    public static IReadOnlyList<string> RulesFiles { get; } =
    [.. Files.Where(file => file.StartsWith(".claude/rules/", StringComparison.Ordinal) && IsMarkdown(file))];

    private static readonly Lazy<HashSet<string>> s_folders = new(Folders);
    private static readonly Lazy<HashSet<string>> s_files = new(() => Files!.ToHashSet(StringComparer.Ordinal));

    /// <summary>A file's text.</summary>
    public static string Read(string file) => File.ReadAllText(Path.Combine(Root, file));

    /// <summary>Whether a path names a markdown file.</summary>
    public static bool IsMarkdown(string file) => file.EndsWith(".md", StringComparison.Ordinal);

    /// <summary>The globs of a rules file's front matter (<c>paths:</c>, one <c>- "glob"</c> a line); none loads it at every start.</summary>
    public static IReadOnlyList<string> Globs(string rulesFile)
    {
        var lines = Read(rulesFile).Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || lines[0] != "---")
        {
            return [];
        }

        var end = lines.IndexOf("---", 1);
        return
        [
            .. lines[1..(end < 0 ? 1 : end)]
                .Select(line => Regex.Match(line, "^\\s*-\\s*\"(.+)\"\\s*$", Options))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value),
        ];
    }

    /// <summary>A glob's choices in braces written out (<c>{a,b}</c>), so each can be checked to name a file.</summary>
    public static IEnumerable<string> Expanded(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var open = pattern.IndexOf('{', StringComparison.Ordinal);
        if (open < 0)
        {
            return [pattern];
        }

        var close = pattern.IndexOf('}', open);
        return pattern[(open + 1)..close]
            .Split(',')
            .SelectMany(choice => Expanded(pattern[..open] + choice + pattern[(close + 1)..]));
    }

    /// <summary>
    /// A glob as Claude Code reads a rules file's paths: <c>**</c> any folders, <c>*</c> and <c>?</c> within one name,
    /// <c>{a,b}</c> either.
    /// </summary>
    public static Regex Glob(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var text = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                text.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
                continue;
            }

            text.Append(
                pattern[i] switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    '{' => "(?:",
                    '}' => ")",
                    ',' => "|",
                    _ => Regex.Escape(pattern[i..(i + 1)]),
                }
            );
        }

        return new Regex(text.Append('$').ToString(), Options);
    }

    /// <summary>The rules files that opening <paramref name="file"/> loads, and their characters together.</summary>
    public static (int Characters, IReadOnlyList<string> Rules) Loaded(string file)
    {
        var rules = s_rulesGlobs.Value.Where(rule => rule.Globs.Any(glob => glob.IsMatch(file))).ToList();
        return (rules.Sum(rule => rule.Characters), [.. rules.Select(rule => rule.File)]);
    }

    /// <summary>
    /// The files every session loads at its start: <c>CLAUDE.md</c>, the files it imports (<c>@path</c>, four hops deep,
    /// from the importing file's folder), and the rules files with no paths.
    /// </summary>
    public static IReadOnlyList<string> SessionStart()
    {
        var files = new List<string>();
        void Add(string file, int hops)
        {
            if (files.Contains(file) || !File.Exists(Path.Combine(Root, file)))
            {
                return;
            }

            files.Add(file);
            if (hops == 4)
            {
                return;
            }

            var folder = Path.GetDirectoryName(file) ?? "";
            foreach (var unit in Prose(Read(file)))
            {
                foreach (Match match in s_import.Matches(StripCode(unit)))
                {
                    Add(Normal(Path.Combine(folder, match.Groups[1].Value)), hops + 1);
                }
            }
        }

        Add("CLAUDE.md", 0);
        files.AddRange(RulesFiles.Where(rules => Globs(rules).Count == 0));
        return files;
    }

    /// <summary>
    /// A doc's sentences: the text of its paragraphs, list items, and table cells (not its front matter, code blocks,
    /// comments, or headings), each code span one word, split where a sentence ends.
    /// </summary>
    public static IEnumerable<string> Sentences(string markdown)
    {
        foreach (var unit in Prose(markdown))
        {
            var text = s_link.Replace(StripCode(unit), "$1");
            text = text.Replace("e.g.", "eg", StringComparison.Ordinal).Replace("i.e.", "ie", StringComparison.Ordinal);
            foreach (var sentence in s_sentenceEnd.Split(text))
            {
                yield return sentence.Trim();
            }
        }
    }

    /// <summary>A sentence's words, split at white space.</summary>
    public static int Words(string sentence) =>
        sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>The dates a text gives (<c>2026-10-19</c>).</summary>
    public static IEnumerable<string> Dates(string text) => s_date.Matches(text).Select(match => match.Value);

    /// <summary>
    /// The names <paramref name="doc"/> gives in code spans that are nothing: a path from the repository's root, from
    /// the doc's folder, or (for a code file) from a project's folder, that names no file, folder, or file a glob matches;
    /// a code file's name that no file has; or a code name (<c>MapGenerator</c>, <c>Places.Chest</c>) whose parts are in
    /// none of <paramref name="code"/> (<see cref="CodeNames"/>). A code name of one part with one capital
    /// (<c>Places</c>) is a word as often as a name, so it is not checked; nor are names with an underscore, placeholders
    /// (<c>NAME</c>, <c>&lt;area&gt;</c>), Vulkan's C names (<c>VkViewport</c>), a library's names by its namespace
    /// (<c>Arch.Relationships</c>), or places in another project's source (<c>Chunk.cs:425</c>,
    /// <c>src/Arch/Core/Entity.cs</c>).
    /// </summary>
    public static IEnumerable<string> UnknownNames(string doc, ISet<string> code)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(code);
        var folder = Normal(Path.GetDirectoryName(doc) ?? "");
        foreach (var unit in Prose(Read(doc)))
        {
            foreach (Match match in s_code.Matches(unit))
            {
                var name = match.Groups[1].Value.Trim();
                var known = KnownPath(name, folder) ?? KnownCode(name, code);
                if (known == false)
                {
                    yield return name;
                }
            }
        }
    }

    /// <summary>
    /// Every name in the repository's code and build files (C# without its comments), and the public names (types,
    /// members, enum values) of <paramref name="assemblies"/> and the assemblies they reference, so a doc may name a
    /// library's API.
    /// </summary>
    public static HashSet<string> CodeNames(params Assembly[] assemblies)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var file in Files.Where(file => s_codeKinds.Any(kind => file.EndsWith(kind, StringComparison.Ordinal)))
        )
        {
            var text = Read(file);
            if (file.EndsWith(".cs", StringComparison.Ordinal))
            {
                text = WithoutComments(text);
            }

            names.UnionWith(s_word.Matches(text).Select(match => match.Value));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Assembly>(assemblies);
        while (pending.Count > 0)
        {
            var assembly = pending.Pop();
            if (!seen.Add(assembly.FullName ?? assembly.GetName().Name ?? ""))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try
                {
                    pending.Push(Assembly.Load(reference));
                }
                catch (Exception exception) when (exception is FileNotFoundException or FileLoadException)
                {
                    // Not shipped with the tests: its names cannot be known.
                }
            }

            Type[] types;
            try
            {
                types = assembly.GetExportedTypes();
            }
            catch (Exception exception) when (exception is NotSupportedException or TypeLoadException)
            {
                continue;
            }

            var root = (assembly.GetName().Name ?? "").Split('.')[0];
            if (root is not ("Itera" or "Game"))
            {
                names.Add(LibraryMark + root);
            }

            foreach (var type in types)
            {
                names.Add(type.Name.Split('`')[0]);
                foreach (
                    var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                )
                {
                    names.Add(member.Name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// A generated file (<c>docs/reference/</c>) against what its generator makes now: null when they are the same, else
    /// the problem. With <see cref="WriteGenerated"/> at 1 it writes the file instead.
    /// </summary>
    public static string? Generated(string file, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = Path.Combine(Root, file);
        content = content.ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable(WriteGenerated) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return null;
        }

        var written = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
        return written == content ? null : $"{file} is not what the code makes now";
    }

    /// <summary>Fails for each generated file not what the code makes now, saying how to write them again.</summary>
    public static void AssertGenerated(params string?[] problems)
    {
        var stale = problems.OfType<string>().ToList();
        Assert.True(
            stale.Count == 0,
            string.Join("; ", stale)
                + $": run `{WriteGenerated}=1 dotnet test --filter ReferenceTests`, look over the diff, and commit it."
        );
    }

    /// <summary>
    /// Fails for every problem in a doc not in <paramref name="notYetCleaned"/>, naming each; the problems of those docs
    /// are left for their clean-up.
    /// </summary>
    public static void AssertNone(
        IEnumerable<(string Doc, string Problem)> problems,
        IReadOnlySet<string> notYetCleaned
    )
    {
        ArgumentNullException.ThrowIfNull(notYetCleaned);
        var left = problems.Where(problem => !notYetCleaned.Contains(problem.Doc)).ToList();
        Assert.True(
            left.Count == 0,
            $"{left.Count} problems (the docs skill says where each kind of fact goes): "
                + string.Join("; ", left.Take(100).Select(problem => $"{problem.Doc}: {problem.Problem}"))
        );
    }

    /// <summary>
    /// Fails for a doc in <paramref name="notYetCleaned"/> that breaks no check any more, so the list only shrinks.
    /// </summary>
    public static void AssertStillUncleaned(
        IEnumerable<(string Doc, string Problem)> problems,
        IReadOnlySet<string> notYetCleaned
    )
    {
        ArgumentNullException.ThrowIfNull(notYetCleaned);
        var breaking = problems.Select(problem => problem.Doc).ToHashSet(StringComparer.Ordinal);
        var clean = notYetCleaned.Where(doc => !breaking.Contains(doc)).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            clean.Count == 0,
            "Clean now, so take them off the list of docs not yet cleaned: " + string.Join(", ", clean)
        );
    }

    private static readonly Lazy<List<(string File, Regex[] Globs, int Characters)>> s_rulesGlobs = new(() =>
        [
            .. RulesFiles.Select(rules =>
                (rules, Globs(rules).SelectMany(Expanded).Select(Glob).ToArray(), Read(rules).Length)
            ),
        ]
    );

    // The paragraphs, list items, and table cells of a doc, each on one line.
    private static List<string> Prose(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var text = markdown.ReplaceLineEndings("\n");
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            text = end < 0 ? text : text[(end + 4)..];
        }

        text = Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline | Options);
        var units = new List<string>();
        var current = new List<string>();
        var fenced = false;
        void Flush()
        {
            if (current.Count > 0)
            {
                units.Add(string.Join(' ', current));
                current.Clear();
            }
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            if (line.Length == 0 || line.StartsWith('#'))
            {
                Flush();
                continue;
            }

            if (line.StartsWith('|'))
            {
                Flush();
                if (!s_tableRule.IsMatch(line))
                {
                    units.AddRange(Cells(line));
                }

                continue;
            }

            if (s_listItem.IsMatch(line))
            {
                Flush();
            }

            current.Add(line);
        }

        Flush();
        return units;
    }

    // A table row's cells, split at the bars outside code spans.
    private static IEnumerable<string> Cells(string row)
    {
        var cell = new StringBuilder();
        var inCode = false;
        foreach (var c in row)
        {
            if (c == '`')
            {
                inCode = !inCode;
            }

            if (c == '|' && !inCode)
            {
                if (cell.ToString().Trim().Length > 0)
                {
                    yield return cell.ToString().Trim();
                }

                cell.Clear();
                continue;
            }

            cell.Append(c);
        }

        if (cell.ToString().Trim().Length > 0)
        {
            yield return cell.ToString().Trim();
        }
    }

    private static string StripCode(string text) => s_code.Replace(text, "C");

    // True for a path that names something, false for one that names nothing, null for text that is no path here.
    private static bool? KnownPath(string name, string folder)
    {
        if (Regex.IsMatch(name, @":\d+", Options))
        {
            return null;
        }

        name = name.TrimEnd('/');
        if (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        var hasFolder = name.Contains('/', StringComparison.Ordinal);
        var kind = s_namedKinds.FirstOrDefault(kind => name.EndsWith(kind, StringComparison.Ordinal));
        if (
            name.Length == 0
            || (name.StartsWith('.') && !hasFolder)
            || name.StartsWith('/')
            || name.Contains("/bin/", StringComparison.Ordinal)
            || name.Contains("/obj/", StringComparison.Ordinal)
            || name.Contains("://", StringComparison.Ordinal)
            || name.IndexOfAny([' ', '<', '>', '~', '$', '%', '=', ':', '"', '\'', '(', ')', ',', '[', ']']) >= 0
            || (!hasFolder && kind is null)
        )
        {
            return null;
        }

        if (!hasFolder || name.StartsWith("../", StringComparison.Ordinal))
        {
            var file = name.Split('/')[^1];
            if (file.Contains('*', StringComparison.Ordinal))
            {
                var glob = Glob(file);
                return Files.Any(path => glob.IsMatch(Path.GetFileName(path)));
            }

            return Files.Any(path => Path.GetFileName(path) == file) || Placeholder(file);
        }

        var parts = name.Split('/');
        var fromRoot = s_folders.Value.Contains(parts[0]) || s_files.Value.Contains(parts[0]);
        if (
            fromRoot
            && parts[0] is "src" or "tests" or "samples"
            && !s_folders.Value.Contains(parts[0] + "/" + parts[1])
        )
        {
            // Another project's source (`src/Arch/Core/Entity.cs`): no project of this one.
            return null;
        }

        var fromDoc = folder.Length > 0 && s_folders.Value.Contains(folder + "/" + parts[0]);
        List<string> tried =
            fromDoc ? [folder + "/" + name]
            : fromRoot ? [name]
            : kind is not null ? [.. s_pathBases.Select(folder => folder + "/" + name)]
            : [];
        if (tried.Count == 0)
        {
            return null;
        }

        if (name.IndexOfAny(['*', '?', '{']) >= 0)
        {
            var globs = tried.SelectMany(Expanded).Select(Glob).ToList();
            return Files.Concat(s_folders.Value).Any(path => globs.Any(glob => glob.IsMatch(path)));
        }

        return tried.Any(path =>
                s_files.Value.Contains(path) || s_folders.Value.Contains(path) || s_files.Value.Contains(path + ".cs")
            ) || Placeholder(name);
    }

    // A name written as a placeholder (`Data/NAME.json`, `Saves/castle.save`'s `NAME`): a part in capitals.
    private static bool Placeholder(string name) =>
        name.Split('/')
            .Select(part => part.Split('.')[0])
            .Any(part => part.Length > 1 && part.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '-'));

    // True for a code name found, false for one found nowhere, null for text that is not a code name to check.
    private static bool? KnownCode(string name, ISet<string> code)
    {
        if (name.StartsWith('<') || Regex.IsMatch(name, "<[^<>]*>[A-Za-z]", Options))
        {
            // A placeholder (`<Name>SystemTests`), not a generic name (`List<T>`).
            return null;
        }

        name = Regex.Replace(name, "<.*>|\\(.*\\)$", "", Options);
        if (!Regex.IsMatch(name, @"^[A-Z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*$", Options))
        {
            return null;
        }

        var parts = name.Split('.');
        if (
            name.StartsWith("Vk", StringComparison.Ordinal) && name.Length > 2 && char.IsUpper(name[2])
            || parts.Length > 1 && code.Contains(LibraryMark + parts[0])
            || parts.Length > 1 && parts[^1].Length <= 4 && parts[^1].All(char.IsLower)
        )
        {
            // Vulkan's C names, a library's names by its namespace, and files of another project (`MVKExtensions.def`).
            return null;
        }

        var checkedParts = parts
            .Where(part =>
                char.IsUpper(part[0]) && part.Any(char.IsLower) && (parts.Length > 1 || part.Count(char.IsUpper) >= 2)
            )
            .ToList();
        return checkedParts.Count == 0 ? null : checkedParts.All(code.Contains);
    }

    // C# text without its comments: `//` to the line's end outside a string, and `/* */`.
    private static string WithoutComments(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var at = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (at >= 0 && lines[i][..at].Count(c => c == '"') % 2 == 0)
            {
                lines[i] = lines[i][..at];
            }
        }

        return Regex.Replace(string.Join('\n', lines), @"/\*.*?\*/", "", RegexOptions.Singleline | Options);
    }

    private static string Normal(string path) => path.Replace('\\', '/');

    private static HashSet<string> Folders()
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            var at = file.LastIndexOf('/');
            while (at > 0)
            {
                folders.Add(file[..at]);
                at = file.LastIndexOf('/', at - 1);
            }
        }

        return folders;
    }

    private static string FindRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, ".claude")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("No .claude folder above the tests.");
    }

    private static List<string> ListFiles()
    {
        string[] skipped =
        [
            "bin",
            "obj",
            ".git",
            "TestResults",
            "worktrees",
            "__pycache__",
            ".vs",
            ".idea",
            "node_modules",
        ];
        var files = new List<string>();
        var pending = new Stack<string>([Root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                files.Add(Normal(Path.GetRelativePath(Root, file)));
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!skipped.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
