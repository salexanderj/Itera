using System.Globalization;
using Itera.Engine.Diagnostics;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's <c>zones</c> (this game only): <c>zones start</c> records the zones the code times
/// (<see cref="Zones"/>: each system's update, the fixed step's and the frame build's parts, the navigation's fields, the
/// voxel jobs), and <c>zones stop</c> writes them as a trace that Perfetto or Chrome's tracing opens, in the data
/// folder's <c>Traces</c>; <c>zones</c> alone says whether it records.
/// </summary>
public sealed class ZoneCommands(ConsoleCommands commands, IOptions<EngineOptions> options)
{
    private readonly string _folder = Path.Combine(options.Value.Application.DataDirectory(), "Traces");

    /// <summary>Adds the command to the registry. Once, at startup.</summary>
    public void Register() =>
        commands.Register(
            "zones",
            "[start|stop]",
            "Records the timed zones of the code (each system, the step's and the frame build's parts, navigation's fields, the voxel jobs; each thread its newest 131,072); stop writes them to the data folder's Traces as a trace for ui.perfetto.dev.",
            CommandScope.Local,
            Run
        );

    private void Run(CommandContext context)
    {
        if (context.Count == 0)
        {
            context.Print(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"zones: {(Zones.Recording ? "recording" : "not recording")}, {Zones.Count} named"
                )
            );
            return;
        }

        if (context.Count != 1)
        {
            context.PrintUsage();
            return;
        }

        if (context.Is(0, "start"))
        {
            Zones.Start();
            context.Print("zones: recording");
            return;
        }

        if (!context.Is(0, "stop"))
        {
            context.PrintUsage();
            return;
        }

        if (!Zones.Recording)
        {
            context.PrintError("zones: not recording; zones start first");
            return;
        }

        Zones.Stop();
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(
            _folder,
            $"zones-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json"
        );
        var written = Zones.WriteTrace(path);
        context.Print(string.Create(CultureInfo.InvariantCulture, $"zones: {written} written to {path}"));
    }
}
