using System.Globalization;
using Itera.Engine.Matches;
using Itera.Engine.Time;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's time commands, offline only (<see cref="CommandScope.Offline"/>: a host that paused would leave its
/// peers behind): <c>pause</c>, <c>resume</c>, <c>step [N]</c> (pauses, then runs N fixed steps), and
/// <c>timescale [X]</c> (game time per second of real time for the match, 0.05 to 16). Frames go on being drawn while
/// paused. <c>pause</c> and <c>step</c> wait for a match's loading to end, since the world streams in fixed steps. A new
/// match starts unpaused at real time (<see cref="FixedStepAccumulator.Restart"/>).
/// </summary>
public sealed class TimeCommands(ConsoleCommands commands, FixedStepAccumulator time, MatchControl? matches = null)
{
    /// <summary>Most steps one <c>step</c> may ask for.</summary>
    public const int MostSteps = 100_000;

    /// <summary>Adds the commands to the registry. Once, at startup.</summary>
    public void Register()
    {
        commands.Register(
            "pause",
            "",
            "Stops the fixed steps (the simulation); frames go on being drawn. Not while a match loads (the world streams in steps).",
            CommandScope.Offline,
            Pause
        );
        commands.Register("resume", "", "Runs the fixed steps again.", CommandScope.Offline, Resume);
        commands.Register(
            "step",
            "[N]",
            "Pauses, then runs N fixed steps (1), as many a loop iteration as the step cap allows.",
            CommandScope.Offline,
            Step
        );
        commands.Register(
            "timescale",
            "[X]",
            "Game time per second of real time for this match, 0.05 to 16 (1); faster than the machine keeps up drops steps.",
            CommandScope.Offline,
            TimeScale
        );
    }

    private void Pause(CommandContext context)
    {
        if (Loading(context))
        {
            return;
        }

        time.Pause();
        context.Print($"paused at tick {time.Tick.ToString(CultureInfo.InvariantCulture)}");
    }

    private void Resume(CommandContext context)
    {
        time.Resume();
        context.Print($"resumed at tick {time.Tick.ToString(CultureInfo.InvariantCulture)}");
    }

    private void Step(CommandContext context)
    {
        var steps = 1;
        if (
            context.Count > 1
            || (context.Count == 1 && !(context.TryGetInt(0, out steps) && steps is > 0 and <= MostSteps))
        )
        {
            context.PrintUsage();
            return;
        }

        if (Loading(context))
        {
            return;
        }

        time.StepWhilePaused(steps);
        context.Print(
            string.Create(CultureInfo.InvariantCulture, $"stepping {steps} from tick {time.Tick}, then paused")
        );
    }

    /// <summary>True, and says so, while a match loads: its world streams in the fixed steps a pause would stop.</summary>
    private bool Loading(CommandContext context)
    {
        if (matches?.Phase != MatchPhase.Loading)
        {
            return false;
        }

        context.PrintError(
            $"{context.Command.Name}: the match is loading, and its world streams in fixed steps; wait loaded first."
        );
        return true;
    }

    private void TimeScale(CommandContext context)
    {
        if (context.Count > 1)
        {
            context.PrintUsage();
            return;
        }

        if (context.Count == 1)
        {
            if (
                !context.TryGetFloat(0, out var scale)
                || !(scale >= FixedStepAccumulator.LeastTimeScale && scale <= FixedStepAccumulator.MostTimeScale)
            )
            {
                context.PrintUsage();
                return;
            }

            time.SetTimeScale(scale);
        }

        context.Print("timescale " + CommandContext.Number(time.TimeScale));
    }
}
