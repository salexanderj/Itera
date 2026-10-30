using System.Globalization;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Settings;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's <c>tune</c> (<see cref="CommandScope.Local"/>): the run's tunables (<see cref="Tunables"/>) listed, those
/// not at their defaults listed, one read (with where its default comes from), set, or put back to its default, by
/// name. A match tunable is set only offline or on a host, which sends it to its clients; a client is told it follows
/// the host.
/// </summary>
public sealed class TuningCommands(ConsoleCommands commands, Tunables tunables, MatchControl matches)
{
    /// <summary>Adds the command to the registry. Once, at startup.</summary>
    public void Register() =>
        commands.Register(
            "tune",
            "[NAME [VALUE|default] | changed | default]",
            "The tunables, numbers a game reads while it plays: with nothing, each one; changed, those not at their defaults; NAME prints one and where its default comes from; NAME VALUE sets it; NAME default or default (all) puts them back.",
            CommandScope.Local,
            Run
        );

    private void Run(CommandContext context)
    {
        if (context.Count == 0)
        {
            foreach (var tunable in tunables.All)
            {
                context.Print(tunable.ToString());
            }

            context.Print(Text($"{tunables.All.Count} tunable(s)"));
            return;
        }

        if (context.Count == 1 && string.Equals(context[0], "changed", StringComparison.Ordinal))
        {
            var changed = 0;
            foreach (var tunable in tunables.All)
            {
                if (tunable.Value != tunable.Default)
                {
                    context.Print(tunable.ToString());
                    changed++;
                }
            }

            context.Print(Text($"{changed} tunable(s) not at their defaults"));
            return;
        }

        if (context.Count == 1 && string.Equals(context[0], "default", StringComparison.Ordinal))
        {
            var client = matches.Role == SessionRole.Client;
            tunables.Reset(TuningScope.Local);
            if (!client)
            {
                tunables.Reset(TuningScope.Match);
            }

            context.Print(
                client
                    ? "The local tunables are back to their defaults; a client follows the host's."
                    : "Every tunable is back to its default."
            );
            return;
        }

        if (tunables.Find(context[0]) is not { } found)
        {
            context.PrintError(Text($"tune: there is no tunable {context[0]} (tune lists them)."));
            return;
        }

        if (context.Count == 1)
        {
            context.Print(found.ToString());
            if (found.Source is { } source)
            {
                context.Print(Text($"  its default from {source}"));
            }

            return;
        }

        if (found.Scope == TuningScope.Match && matches.Role == SessionRole.Client)
        {
            context.PrintError(Text($"tune: {found.Name} is the match's: the host sets it."));
            return;
        }

        var value = found.Default;
        if (
            !string.Equals(context[1], "default", StringComparison.Ordinal)
            && !float.TryParse(context[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        )
        {
            context.PrintError(Text($"tune: {context[1]} is not a number."));
            return;
        }

        if (!tunables.TrySet(found, value))
        {
            context.PrintError(Text($"tune: {found.Name} takes {found.Least:G6} to {found.Most:G6}."));
            return;
        }

        context.Print(found.ToString());
    }

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
