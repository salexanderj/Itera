using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Itera.Engine.Commands;
using Itera.Engine.Replication;
using Itera.Engine.Saves;
using Itera.Engine.Settings;

namespace Itera.TestSupport;

/// <summary>
/// The generated reference (<c>docs/reference/</c>): what the code holds, as markdown tables made from the code and its
/// doc comments. The engine's reference tests write its options; a game's write its options, console commands, systems,
/// tunables, replicated kinds, and save sections. <see cref="RepositoryDocs.Generated"/> compares each with its file.
/// </summary>
internal static class ReferenceDocs
{
    private const RegexOptions Options = RegexOptions.CultureInvariant;

    private static readonly Dictionary<Assembly, Dictionary<string, string>> s_summaries = [];

    /// <summary>A generated file: its title, a line on what it lists, and its tables.</summary>
    public static string File(string title, string about, params string[] tables)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(title).Append("\n\n");
        text.Append(about)
            .Append(
                " Generated from the code by the reference tests; do not edit it by hand (`docs/reference/README.md`).\n"
            );
        foreach (var table in tables)
        {
            text.Append('\n').Append(table);
        }

        return text.ToString();
    }

    /// <summary>
    /// Every option under <paramref name="prefix"/> that <paramref name="root"/> holds, at any depth: its key, its default
    /// (a new <paramref name="root"/>'s), the values <paramref name="failures"/> takes for a number (each tried alone from
    /// the defaults, so a rule between options is in its text, not here), its choices for an enum, and its doc comment.
    /// </summary>
    public static string OptionsTable(string prefix, Type root, Func<object, IEnumerable<string>> failures)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(failures);
        var table = new StringBuilder(
            "| Option | Default | Least to most | What it does |\n| --- | --- | --- | --- |\n"
        );
        foreach (var (key, path) in Leaves(prefix, root, []))
        {
            var defaults = Activator.CreateInstance(root)!;
            var value = Get(defaults, path);
            var takes = Takes(key, root, path, failures);
            table.Append(
                CultureInfo.InvariantCulture,
                $"| `{key}` | {Value(value)} | {takes} | {Cell(Summary(path[^1]))} |\n"
            );
        }

        return table.ToString();
    }

    /// <summary>The options a game's settings file gives other values than the engine's defaults.</summary>
    public static string ChangedOptionsTable(IEnumerable<(string Key, string Default, string Value)> changes) =>
        "| Option | The engine's default | The game's |\n| --- | --- | --- |\n"
        + string.Concat(changes.Select(change => $"| `{change.Key}` | {change.Default} | {change.Value} |\n"));

    /// <summary>Every console command: its name and arguments, where it may run, who registered it, and its help.</summary>
    public static string CommandsTable(IEnumerable<ConsoleCommand> commands)
    {
        var table = new StringBuilder("| Command | Runs | From | What it does |\n| --- | --- | --- | --- |\n");
        foreach (var command in commands.OrderBy(command => command.Name, StringComparer.Ordinal))
        {
            var usage = command.Usage.Length == 0 ? "" : " " + command.Usage;
            var from = command.Run.Method.DeclaringType?.Assembly.GetName().Name ?? command.Owner;
            table.Append(
                CultureInfo.InvariantCulture,
                $"| `{Code(command.Name + usage)}` | {Scope(command.Scope)} | {From(from)} | {Cell(command.Help)} |\n"
            );
        }

        return table.ToString();
    }

    /// <summary>The systems of one group, in their run order, each with where it comes from and its doc comment.</summary>
    public static string SystemsTable(IEnumerable<Type> systems)
    {
        var table = new StringBuilder("| | System | From | What it does |\n| --- | --- | --- | --- |\n");
        var order = 1;
        foreach (var system in systems)
        {
            table.Append(
                CultureInfo.InvariantCulture,
                $"| {order++} | `{system.Name}` | {From(system.Assembly.GetName().Name)} | {Cell(FirstSentence(Summary(system)))} |\n"
            );
        }

        return table.ToString();
    }

    /// <summary>Every tunable, by name: its default, its range, its scope, and its help.</summary>
    public static string TunablesTable(Tunables tunables)
    {
        ArgumentNullException.ThrowIfNull(tunables);
        var table = new StringBuilder(
            "| Tunable | Default | Range | Scope | What it sets |\n| --- | --- | --- | --- | --- |\n"
        );
        foreach (var tunable in tunables.All.OrderBy(tunable => tunable.Name, StringComparer.Ordinal))
        {
            table.Append(
                CultureInfo.InvariantCulture,
                $"| `{tunable.Name}` | {Number(tunable.Default)} | {Number(tunable.Least)} to {Number(tunable.Most)} | {Scope(tunable.Scope)} | {Cell(tunable.Help)} |\n"
            );
        }

        return table.ToString();
    }

    /// <summary>
    /// Every replicated kind, by its number: whom it reaches, how fast its priority grows, how far from a client's player
    /// it grows that fast, and its fields.
    /// </summary>
    public static string KindsTable(ReplicatedKinds kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
        {
            return "None.\n";
        }

        var table = new StringBuilder(
            "| Number | Kind | Reaches | Priority | Falls off past | Fields | Handler |\n| --- | --- | --- | --- | --- | --- | --- |\n"
        );
        for (var id = 0; id < kinds.Count; id++)
        {
            var kind = kinds[new ReplicatedKindId((byte)id)];
            var reach = kind.RelevanceMetres == 0 ? "every client" : $"{Number(kind.RelevanceMetres)} m";
            var falloff = kind.FalloffMetres == 0 ? "never" : $"{Number(kind.FalloffMetres)} m";
            var fields = string.Join(", ", kind.Schema.Description.Split(';', StringSplitOptions.RemoveEmptyEntries));
            table.Append(
                CultureInfo.InvariantCulture,
                $"| {id} | `{kind.Name}` | {reach} | {Number(kind.Priority)} | {falloff} | {Cell(fields)} | `{kind.Handler.GetType().Name}` |\n"
            );
        }

        return table.ToString();
    }

    /// <summary>Every save section: its name, its version, and the class that writes it, with its doc comment.</summary>
    public static string SavesTable(IEnumerable<ISaveSection> sections) =>
        !sections.Any()
            ? "None.\n"
            : "| Section | Version | Written by | What it holds |\n| --- | --- | --- | --- |\n"
                + string.Concat(
                    sections
                        .OrderBy(section => section.Name, StringComparer.Ordinal)
                        .Select(section =>
                            $"| `{section.Name}` | {section.Version} | `{section.GetType().Name}` | {Cell(FirstSentence(Summary(section.GetType())))} |\n"
                        )
                );

    /// <summary>
    /// Each rules file: what it covers (its first heading) and the paths whose files load it, so an agent finds the one
    /// to read before planning work in an area.
    /// </summary>
    public static string RulesTable(IEnumerable<string> rulesFiles)
    {
        var table = new StringBuilder("| Rules file | Covers | Loads with |\n| --- | --- | --- |\n");
        foreach (var rules in rulesFiles.Order(StringComparer.Ordinal))
        {
            var heading =
                RepositoryDocs
                    .Read(rules)
                    .Split('\n')
                    .FirstOrDefault(line =>
                        line.StartsWith("## ", StringComparison.Ordinal)
                        || line.StartsWith("# ", StringComparison.Ordinal)
                    )
                    ?.TrimStart('#', ' ')
                    .Trim()
                ?? "";
            var paths = string.Join(", ", RepositoryDocs.Globs(rules).Select(glob => $"`{Code(glob)}`"));
            table.Append(CultureInfo.InvariantCulture, $"| `{rules}` | {Cell(heading)} | {paths} |\n");
        }

        return table.ToString();
    }

    /// <summary>A value as the tables write it: in code, numbers in the invariant culture, none as a dash.</summary>
    public static string Value(object? value) =>
        value switch
        {
            null => "none",
            string text when text.Length == 0 => "empty",
            float number => $"`{Number(number)}`",
            double number => $"`{Number(number)}`",
            bool flag => flag ? "`true`" : "`false`",
            System.Collections.IEnumerable items and not string =>
                $"`{string.Join(", ", items.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)))}`",
            _ => $"`{Code(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")}`",
        };

    private static string Number(double number) => number.ToString("R", CultureInfo.InvariantCulture);

    private static string Number(float number) => number.ToString("R", CultureInfo.InvariantCulture);

    private static string Scope(CommandScope scope) =>
        scope switch
        {
            CommandScope.Local => "anywhere",
            CommandScope.Match => "offline or host",
            _ => "offline",
        };

    private static string Scope(TuningScope scope) => scope == TuningScope.Match ? "match" : "local";

    private static string From(string? assembly) =>
        assembly switch
        {
            "Itera.Engine" => "engine",
            "Game" => "game",
            _ => assembly ?? "",
        };

    // Every option's key and the properties that lead to it: a property of a class (not a string) holds more options.
    private static IEnumerable<(string Key, PropertyInfo[] Path)> Leaves(string prefix, Type type, PropertyInfo[] path)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var key = prefix + ":" + property.Name;
            PropertyInfo[] here = [.. path, property];
            var kind = property.PropertyType;
            if (
                kind.IsClass
                && kind != typeof(string)
                && !kind.IsArray
                && !typeof(System.Collections.IEnumerable).IsAssignableFrom(kind)
            )
            {
                foreach (var inner in Leaves(key, kind, here))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return (key, here);
            }
        }
    }

    private static object? Get(object root, PropertyInfo[] path)
    {
        object? owner = root;
        foreach (var property in path)
        {
            owner = owner is null ? null : property.GetValue(owner);
        }

        return owner;
    }

    private static void Set(object root, PropertyInfo[] path, object value)
    {
        var owner = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            owner = path[i].GetValue(owner)!;
        }

        path[^1].SetValue(owner, value);
    }

    // What an option takes: an enum its names, a number the bounds its validator holds it to, found by halving.
    private static string Takes(string key, Type root, PropertyInfo[] path, Func<object, IEnumerable<string>> failures)
    {
        var kind = Nullable.GetUnderlyingType(path[^1].PropertyType) ?? path[^1].PropertyType;
        if (kind.IsEnum)
        {
            return string.Join(", ", Enum.GetNames(kind).Select(name => $"`{name}`"));
        }

        Func<long, object>? make =
            kind == typeof(int) ? step => (int)step
            : kind == typeof(uint) ? step => (uint)step
            : kind == typeof(float) ? step => FloatOf(step)
            : kind == typeof(double) ? step => DoubleOf(step)
            : null;
        Func<object, long>? step =
            kind == typeof(int) ? value => (int)value
            : kind == typeof(uint) ? value => (uint)value
            : kind == typeof(float) ? value => StepOf((float)value)
            : kind == typeof(double) ? value => StepOf((double)value)
            : null;
        if (make is null || step is null)
        {
            // A 64-bit whole number: only whether it takes any value.
            object[] ends =
                kind == typeof(long) ? [long.MinValue, long.MaxValue]
                : kind == typeof(ulong) ? [ulong.MinValue, ulong.MaxValue]
                : [];
            return ends.Length > 0 && ends.All(TakesValue) ? "any" : "";
        }

        bool TakesValue(object value)
        {
            var options = Activator.CreateInstance(root)!;
            Set(options, path, value);
            return !failures(options).Any(failure => failure.StartsWith(key, StringComparison.Ordinal));
        }

        var (lowest, highest) =
            kind == typeof(int) ? (int.MinValue, int.MaxValue)
            : kind == typeof(uint) ? (0L, uint.MaxValue)
            : kind == typeof(float) ? (StepOf(-float.MaxValue), StepOf(float.MaxValue))
            : (StepOf(-double.MaxValue), StepOf(double.MaxValue));
        bool Takes(long at)
        {
            var options = Activator.CreateInstance(root)!;
            Set(options, path, make(at));
            return !failures(options).Any(failure => failure.StartsWith(key, StringComparison.Ordinal));
        }

        if (Get(Activator.CreateInstance(root)!, path) is not { } defaults)
        {
            return "";
        }

        var start = step(defaults);
        if (!Takes(start))
        {
            return "";
        }

        // A number may skip values (1, 2, 4, or 8; 0 or 400 to 8000): past an edge, look on for one taken, a whole
        // number by ones and powers of two, a decimal by powers of ten.
        var whole = kind == typeof(int) || kind == typeof(uint);
        long Farthest(long from, long limit)
        {
            var edge = Edge(from, limit, Takes);
            while (true)
            {
                var towards = limit > edge ? 1 : -1;
                var at = Convert.ToDouble(make(edge), CultureInfo.InvariantCulture);
                var steps = whole
                    ? Enumerable
                        .Range(1, 64)
                        .Select(step => (long)step)
                        .Concat(Enumerable.Range(7, 25).Select(power => 1L << power))
                        .Select(step => edge + towards * step)
                    : Enumerable
                        .Range(-3, 10)
                        .Select(power => at + towards * Math.Pow(10, power))
                        .Select(value => kind == typeof(float) ? StepOf((float)value) : StepOf(value));
                var further = steps
                    .Where(step => towards > 0 ? step > edge && step < limit : step < edge && step > limit)
                    .FirstOrDefault(Takes, edge);
                if (further == edge)
                {
                    break;
                }

                edge = Edge(further, limit, Takes);
            }

            return edge;
        }

        var least = Takes(lowest) ? (long?)null : Farthest(start, lowest);
        var most = Takes(highest) ? (long?)null : Farthest(start, highest);
        string Text(long at) => Value(make(at));
        string Near(long at, int towards)
        {
            // An open bound (above 0) leaves the halving one step past a round number: name that number.
            var outside = Text(at + towards);
            return Text(at).Length > outside.Length + 2 ? (towards < 0 ? "above " : "below ") + outside : Text(at);
        }

        return (least, most) switch
        {
            (null, null) => "any",
            ({ } low, null) => "at least " + Near(low, -1),
            (null, { } high) => "at most " + Near(high, 1),
            ({ } low, { } high) => Near(low, -1) + " to " + Near(high, 1),
        };
    }

    // The farthest step from `taken` towards `refused` that is still taken, by halving.
    private static long Edge(long taken, long refused, Func<long, bool> takes)
    {
        // A double's steps span more than a long holds between them, so the halving counts in 128 bits.
        while (Int128.Abs((Int128)refused - taken) > 1)
        {
            var middle = (long)(((Int128)taken + refused) / 2);
            if (takes(middle))
            {
                taken = middle;
            }
            else
            {
                refused = middle;
            }
        }

        return taken;
    }

    // Floats and doubles as steps in order, so halving steps halves the values between.
    private static long StepOf(float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        return bits < 0 ? int.MinValue - (long)bits : bits;
    }

    private static float FloatOf(long step) =>
        BitConverter.Int32BitsToSingle((int)(step < 0 ? int.MinValue - step : step));

    private static long StepOf(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        return bits < 0 ? long.MinValue - bits : bits;
    }

    private static double DoubleOf(long step) => BitConverter.Int64BitsToDouble(step < 0 ? long.MinValue - step : step);

    // A member's doc comment's summary, as one line of markdown.
    private static string Summary(MemberInfo member)
    {
        var assembly = (member as Type ?? member.DeclaringType)!.Assembly;
        if (!s_summaries.TryGetValue(assembly, out var summaries))
        {
            summaries = ReadSummaries(assembly);
            s_summaries[assembly] = summaries;
        }

        var id = member switch
        {
            Type type => "T:" + type.FullName,
            PropertyInfo property => "P:" + property.DeclaringType!.FullName + "." + property.Name,
            _ => "M:" + member.DeclaringType!.FullName + "." + member.Name,
        };
        return summaries.GetValueOrDefault(id.Replace('+', '.'), "");
    }

    private static Dictionary<string, string> ReadSummaries(Assembly assembly)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".xml");
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!System.IO.File.Exists(path))
        {
            return summaries;
        }

        foreach (var member in XDocument.Load(path).Descendants("member"))
        {
            if (member.Element("summary") is { } summary && member.Attribute("name")?.Value is { } name)
            {
                summaries[name] = Text(summary);
            }
        }

        return summaries;
    }

    // A doc comment's text: `see` as the name it points to, `c` and `paramref` as code, all on one line.
    private static string Text(XElement element)
    {
        var text = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText plain:
                    text.Append(plain.Value);
                    break;
                case XElement { Name.LocalName: "see" or "seealso" } see:
                    var target = see.Attribute("cref")?.Value ?? see.Attribute("langword")?.Value ?? "";
                    var parts = target.Split('(')[0].Split(':')[^1].Split('.');
                    var shown =
                        see.Value.Length > 0 ? see.Value
                        : target.StartsWith("T:", StringComparison.Ordinal) || parts.Length < 2 ? parts[^1]
                        : parts[^2] + "." + parts[^1];
                    text.Append('`').Append(shown.Split('`')[0]).Append('`');
                    break;
                case XElement { Name.LocalName: "c" or "paramref" or "typeparamref" } code:
                    text.Append('`').Append(code.Attribute("name")?.Value ?? code.Value).Append('`');
                    break;
                case XElement inner:
                    text.Append(' ').Append(Text(inner)).Append(' ');
                    break;
            }
        }

        return Regex.Replace(text.ToString(), @"\s+", " ", Options).Trim();
    }

    private static string FirstSentence(string text)
    {
        var end = Regex.Match(text, @"(?<=[.!?])\s+(?=[A-Z`])", Options);
        return end.Success ? text[..end.Index] : text;
    }

    private static string Cell(string text) =>
        text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    private static string Code(string text) =>
        text.Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal);
}
