using System.Globalization;
using System.Reflection;
using Itera.Engine;
using Itera.Engine.Matches;
using Itera.Engine.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The run's state that the engine's loop alone sets (its setters are the engine's own), set and read here as the loop
/// does, so the sample's menu is tested without a window or a running loop: a match's phase, how many started, why the
/// last ended, the start or leave a menu asked for (taken, as the loop takes it), and player settings with no file.
/// </summary>
internal static class EngineState
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void SetPhase(MatchControl matches, MatchPhase phase) =>
        Set(matches, nameof(MatchControl.Phase), phase);

    public static void SetStarted(MatchControl matches, int started) =>
        Set(matches, nameof(MatchControl.Started), started);

    public static void SetLastEnd(MatchControl matches, string? reason) =>
        Set(matches, nameof(MatchControl.LastEnd), reason);

    public static void SetJoinAddress(MatchControl matches, string? address) =>
        Set(matches, nameof(MatchControl.JoinAddress), address);

    /// <summary>The start asked for since the last take, or null.</summary>
    public static MatchStart? TakeStart(MatchControl matches) => (MatchStart?)Call(matches, "TakeStart");

    /// <summary>The leave asked for since the last take, or null.</summary>
    public static string? TakeLeave(MatchControl matches) => (string?)Call(matches, "TakeLeave");

    /// <summary>The player's settings at the options' defaults, read from and written to no file.</summary>
    public static PlayerSettings SettingsWithoutFile() =>
        (PlayerSettings)
            Activator.CreateInstance(
                typeof(PlayerSettings),
                Members,
                null,
                [
                    new EngineOptions(),
                    CommandLineKeys.None,
                    null,
                    NullLogger<PlayerSettings>.Instance,
                    null,
                    null,
                    null,
                    null,
                    null,
                ],
                CultureInfo.InvariantCulture
            )!;

    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property, Members)!.SetValue(target, value);

    private static object? Call(object target, string method) =>
        target.GetType().GetMethod(method, Members)!.Invoke(target, null);
}
