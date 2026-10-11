using System.Globalization;
using Itera.Engine.Networking;
using Itera.Engine.Rendering;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Options;

namespace Itera.Engine;

/// <summary>
/// Rejects configuration values the engine cannot run with, naming the configuration key. It runs the first time
/// <see cref="IOptions{TOptions}.Value"/> is read, which is while <see cref="GameLoop"/> is resolved, so a bad value
/// stops the game at startup with exit code 1 instead of misbehaving later. Without it, <c>FixedStepHz = 0</c> gives
/// a step of +infinity seconds and the simulation silently never ticks. The level distances are checked on the grid of
/// the registered world generator (<see cref="IChunkGenerator.Grid"/>), or of 1 cm when there is none.
/// </summary>
public sealed class EngineOptionsValidator(IEnumerable<IChunkGenerator> generators) : IValidateOptions<EngineOptions>
{
    private const string Prefix = EngineOptions.SectionName + ":";

    private readonly VoxelGrid _grid = generators.FirstOrDefault()?.Grid ?? VoxelGrid.Unit;

    /// <summary>A validator for a world of 1 cm voxels.</summary>
    public EngineOptionsValidator()
        : this([]) { }

    public ValidateOptionsResult Validate(string? name, EngineOptions options)
    {
        var failures = new List<string>();

        Range(failures, "FixedStepHz", options.FixedStepHz, 1, 1000);
        Range(failures, "MaxFixedStepsPerFrame", options.MaxFixedStepsPerFrame, 1, 1000);

        var application = options.Application;
        if (string.IsNullOrWhiteSpace(application.Name))
        {
            failures.Add($"{Prefix}Application:Name must not be empty.");
        }

        if (!IsFolderName(application.DataFolder))
        {
            failures.Add(
                $"{Prefix}Application:DataFolder must hold 1 to 64 letters, digits, '-', or '_'; it is '{application.DataFolder}'."
            );
        }

        if (options.Save.Name is { } save && !IsSaveName(save))
        {
            failures.Add(
                $"{Prefix}Save:Name must hold 1 to 64 letters, digits, spaces (not first or last), '-', or '_'; it is '{save}'."
            );
        }

        Range(failures, "Window:Width", options.Window.Width, 1, 32768);
        if (options.Window.Mode is not (WindowMode.Window or WindowMode.Offscreen or WindowMode.None))
        {
            failures.Add($"{Prefix}Window:Mode must be Window, Offscreen, or None.");
        }

        if (options.FreeRun && options.Window.Mode != WindowMode.None)
        {
            failures.Add(
                $"{Prefix}FreeRun needs Engine:Window:Mode=None: steps run back to back only without frames to draw."
            );
        }

        Range(failures, "Window:OffscreenFps", options.Window.OffscreenFps, 1, 1000);
        Range(failures, "Window:Height", options.Window.Height, 1, 32768);

        Range(failures, "Renderer:MeshFinestLevel", options.Renderer.MeshFinestLevel, 0, VoxelConstants.MaxLevel);
        Range(failures, "Renderer:MeshReachShare", options.Renderer.MeshReachShare, 0.05, 1);
        Range(failures, "Renderer:ViewDistanceMetres", options.Renderer.ViewDistanceMetres, 0, 8000);
        if (options.Renderer.MsaaSamples is not (1 or 2 or 4 or 8))
        {
            failures.Add($"{Prefix}Renderer:MsaaSamples must be 1, 2, 4, or 8.");
        }

        // The heap's first granules hold the materials and the tint map (about 70 KB); every device takes 2D images of
        // 4096 texels a side (maxImageDimension2D's least).
        Range(failures, "Renderer:MeshMegabytes", options.Renderer.MeshMegabytes, 64, 4096);
        Range(failures, "Renderer:SkinMegabytes", options.Renderer.SkinMegabytes, 1, 512);
        Range(failures, "Renderer:ShadowMapTexels", options.Renderer.ShadowMapTexels, 512, 4096);
        var distant = options.Renderer.DistantShadowMetres;
        // Written so NaN fails too.
        if (distant != 0 && !(distant >= 400 && distant <= 8000))
        {
            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Prefix}Renderer:DistantShadowMetres must be 0 (no third cascade) or between 400 and 8000; it is {distant}."
                )
            );
        }

        Range(failures, "Renderer:MostLights", options.Renderer.MostLights, 1, 256);

        Range(failures, "Renderer:SkyRed", options.Renderer.SkyRed, 0, 1);
        Range(failures, "Renderer:SkyGreen", options.Renderer.SkyGreen, 0, 1);
        Range(failures, "Renderer:SkyBlue", options.Renderer.SkyBlue, 0, 1);
        // The sky field is staged whole in one frame (3.8 MB, SkyField.Cells).
        Range(failures, "Renderer:UploadMegabytesPerFrame", options.Renderer.UploadMegabytesPerFrame, 4, 256);
        Range(failures, "Renderer:RenderScale", options.Renderer.RenderScale, RenderScale.Min, RenderScale.Max);
        Range(
            failures,
            "Renderer:DynamicScalingThreshold",
            options.Renderer.DynamicScalingThreshold,
            DynamicRenderScale.LeastThreshold,
            DynamicRenderScale.MostThreshold
        );
        if (options.Renderer.ShaderSourceDirectory is { } shaders && !Directory.Exists(shaders))
        {
            failures.Add($"{Prefix}Renderer:ShaderSourceDirectory is not a folder: {shaders}");
        }

        if (options.Renderer.GameShaderSourceDirectory is { } gameShaders && !Directory.Exists(gameShaders))
        {
            failures.Add($"{Prefix}Renderer:GameShaderSourceDirectory is not a folder: {gameShaders}");
        }

        var logging = options.Logging;
        Range(failures, "Logging:FileSizeLimitMegabytes", logging.FileSizeLimitMegabytes, 1, 1024 * 1024);
        Range(failures, "Logging:RetainedFiles", logging.RetainedFiles, 1, 100_000);
        Range(failures, "Logging:RetainedDays", logging.RetainedDays, 0, 100_000);
        Range(failures, "Logging:StatsIntervalSeconds", logging.StatsIntervalSeconds, 0, 86_400);

        var voxels = options.Voxels;
        // 0 takes the radius from the grid; below 3 m, level 0's reach (the radius less two columns) is under half a
        // metre; the most is 500 m, or 39 level-0 columns on voxels over 10 cm.
        if (voxels.StreamingRadiusMetres != 0)
        {
            Range(failures, "Voxels:StreamingRadiusMetres", voxels.StreamingRadiusMetres, 3, MostRadiusMetres(_grid));
        }

        Range(failures, "Voxels:Levels", voxels.Levels, 1, VoxelConstants.MaxLevel + 1);
        Range(failures, "Voxels:MemoryBudgetMegabytes", voxels.MemoryBudgetMegabytes, 64, 65_536);
        Range(failures, "Voxels:WorkerThreads", voxels.WorkerThreads, 0, 64);
        Range(failures, "Voxels:DebugWalkSpeed", voxels.DebugWalkSpeed, 0, 100);
        Range(failures, "Voxels:EditColumnsPerStep", voxels.EditColumnsPerStep, 1, 64);
        Range(failures, "Voxels:Level0ReachMetres", voxels.Level0ReachMetres, 0, MostRadiusMetres(_grid));
        Range(failures, "Voxels:ReachGrowth", voxels.ReachGrowth, 1.25, 2);
        ReachesGrow(failures, voxels, _grid);

        var camera = options.Camera;
        Range(failures, "Camera:FieldOfViewDegrees", camera.FieldOfViewDegrees, 30, 120);
        Range(failures, "Camera:FlySpeed", camera.FlySpeed, 0.1, 1000);
        Range(failures, "Camera:MouseSensitivity", camera.MouseSensitivity, 0.001, 10);

        var physics = options.Physics;
        Range(failures, "Physics:SettleSeconds", physics.SettleSeconds, 0.5, 60);
        Range(failures, "Physics:MaxLooseBodies", physics.MaxLooseBodies, 1, 1024);
        Range(failures, "Physics:Threads", physics.Threads, 0, 16);
        Range(
            failures,
            "Physics:CollisionCentimetres",
            physics.CollisionCentimetres,
            1,
            PhysicsSettings.MostCollisionCentimetres
        );

        var particles = options.Particles;
        Range(failures, "Particles:Capacity", particles.Capacity, 16_384, 1_048_576);
        Range(failures, "Particles:MaxBurstsPerFrame", particles.MaxBurstsPerFrame, 16, 65_536);
        Range(failures, "Particles:MaxSpawnsPerFrame", particles.MaxSpawnsPerFrame, 1024, 1_048_576);

        var audio = options.Audio;
        Range(failures, "Audio:Volume", audio.Volume, 0, 1);
        Range(failures, "Audio:MusicVolume", audio.MusicVolume, 0, 1);
        Range(failures, "Audio:EffectsVolume", audio.EffectsVolume, 0, 1);
        // OpenAL Soft mixes 255 mono sources by default.
        Range(failures, "Audio:MaxVoices", audio.MaxVoices, 8, 255);
        Range(failures, "Audio:SampleRate", audio.SampleRate, 22_050, 96_000);

        Range(failures, "Ui:FontPixels", options.Ui.FontPixels, 8, 64);
        Range(failures, "Ui:Scale", options.Ui.Scale, 0.75f, 1.5f);
        Range(failures, "Console:ScrollbackLines", options.Console.ScrollbackLines, 100, 10_000);
        if (options.Console.RunFile is { } runFile && !string.IsNullOrWhiteSpace(runFile) && !File.Exists(runFile))
        {
            failures.Add($"{Prefix}Console:RunFile names {runFile}, which does not exist.");
        }

        Range(failures, "Loading:MaxSeconds", options.Loading.MaxSeconds, 1, 300);
        Range(failures, "Control:Port", options.Control.Port, 1024, 65_535);
        Range(failures, "Camera:CircleHeightMetres", options.Camera.CircleHeightMetres, -1000, 1000);
        Range(failures, "Origin:ShiftMetres", options.Origin.ShiftMetres, 8, 4096);
        Range(failures, "Navigation:WindowCells", options.Navigation.WindowCells, 256, 4096);
        Range(failures, "Benchmark:SettleSeconds", options.Benchmark.SettleSeconds, 0, 600);
        Range(failures, "Benchmark:MeasureSeconds", options.Benchmark.MeasureSeconds, 0.1, 600);
        Range(failures, "Benchmark:FlightSeconds", options.Benchmark.FlightSeconds, 0, 3600);

        var session = options.Session;
        if (session.Mode is not (SessionMode.Menu or SessionMode.Offline or SessionMode.Host or SessionMode.Join))
        {
            failures.Add($"{Prefix}Session:Mode must be Menu, Offline, Host, or Join.");
        }

        Range(failures, "Session:Port", session.Port, 1024, 65_535);
        Range(failures, "Session:MaxPlayers", session.MaxPlayers, 2, 16);
        Range(failures, "Session:ConnectTimeoutSeconds", session.ConnectTimeoutSeconds, 1, 60);
        Range(failures, "Session:DisconnectTimeoutSeconds", session.DisconnectTimeoutSeconds, 2, 60);
        // A payload is held in one MemoryStream, whose length is an int.
        Range(failures, "Session:MaxJoinMegabytes", session.MaxJoinMegabytes, 1, 2047);
        if (
            session.PlayerName is not null
            && (
                string.IsNullOrWhiteSpace(session.PlayerName)
                || session.PlayerName.Length > SessionProtocol.MaxNameLength
            )
        )
        {
            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Prefix}Session:PlayerName must hold 1 to {SessionProtocol.MaxNameLength} characters, or be left out for the Steam name."
                )
            );
        }

        if (session.Transport is not (SessionTransportKind.Direct or SessionTransportKind.Steam))
        {
            failures.Add($"{Prefix}Session:Transport must be Direct or Steam.");
        }
        else if (session.Mode == SessionMode.Join || (session.Mode == SessionMode.Menu && session.Address is not null))
        {
            if (
                session.Transport == SessionTransportKind.Direct
                && !SessionAddress.TryParse(session.Address, out _, out _)
            )
            {
                failures.Add(
                    $"{Prefix}Session:Address must be host:port (a name, an IPv4 address, or [IPv6]:port) to join; it is '{session.Address}'."
                );
            }
            else if (
                session.Transport == SessionTransportKind.Steam
                && !SteamAddress.TryParse(session.Address, out _, out _)
            )
            {
                failures.Add(
                    $"{Prefix}Session:Address must be lobby:ID or steam:ID to join through Steam; it is '{session.Address}'."
                );
            }
        }

        // The menu does not need Steam to start: a Steam join it starts without Steam fails there, with the reason shown.
        if (
            session.Transport == SessionTransportKind.Steam
            && !options.Steam.Enabled
            && session.Mode != SessionMode.Menu
        )
        {
            failures.Add($"{Prefix}Session:Transport is Steam, but {Prefix}Steam:Enabled is false.");
        }

        if (
            session.Transport == SessionTransportKind.Steam
            && options.Steam.AppId == 0
            && session.Mode != SessionMode.Menu
        )
        {
            failures.Add($"{Prefix}Session:Transport is Steam, but {Prefix}Steam:AppId is 0 (no App ID).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// A name Windows keeps for a device, in any case (CON, PRN, AUX, NUL, COM0 to COM9, LPT0 to LPT9): no file can have
    /// it there.
    /// </summary>
    internal static bool IsWindowsDeviceName(string value)
    {
        var upper = value.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        return upper.Length == 4
            && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
            && char.IsAsciiDigit(upper[3]);
    }

    // One rule on every platform: the characters a file name may hold differ between Windows, Linux, and macOS.
    internal static bool IsSaveName(string value) =>
        value.Length > 0 && value[0] != ' ' && value[^1] != ' ' && IsFolderName(value.Replace(' ', '_'));

    private static bool IsFolderName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || IsWindowsDeviceName(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The most a streaming radius or level 0's reach may be in a world of <paramref name="grid"/>, in metres: 500 m, or
    /// as many of its level-0 columns as 500 m holds of 10 cm voxels' (39) on voxels over 10 cm.
    /// </summary>
    internal static double MostRadiusMetres(VoxelGrid grid) => 500 * Math.Max(1, grid.BaseCentimetres / 10.0);

    /// <summary>True when the options <see cref="LevelDistances"/> reads are within their ranges.</summary>
    private static bool DistancesInRange(VoxelSettings voxels, VoxelGrid grid) =>
        (
            voxels.StreamingRadiusMetres == 0
            || voxels.StreamingRadiusMetres >= 3 && voxels.StreamingRadiusMetres <= MostRadiusMetres(grid)
        )
        && voxels.Levels >= 1
        && voxels.Level0ReachMetres >= 0
        && voxels.Level0ReachMetres <= MostRadiusMetres(grid)
        && voxels.ReachGrowth is >= 1.25 and <= 2;

    /// <summary>
    /// Level 0 must be drawn somewhere (a radius of two level-0 columns or less draws it nowhere), and each level farther
    /// than the one before, the coarsest too: a level 0 drawn far with little growth would otherwise reach past the
    /// coarser levels, whose radii it would also pass.
    /// </summary>
    private static void ReachesGrow(List<string> failures, VoxelSettings voxels, VoxelGrid grid)
    {
        if (!DistancesInRange(voxels, grid))
        {
            return;
        }

        var distances = LevelDistances.From(voxels, grid);
        if (distances.Levels > 1 && distances.ReachCentimetres(0) <= 0)
        {
            var margin = grid.ReachMarginCentimetres(0) / (double)VoxelConstants.CentimetresPerMetre;
            failures.Add(
                $"{Prefix}Voxels:StreamingRadiusMetres ({voxels.StreamingRadiusMetresFor(grid):F2} m) draws level 0 nowhere: with {Prefix}Voxels:Level0ReachMetres 0 it must be over {margin:F2} m, two of its columns."
            );
            return;
        }

        for (var level = 1; level < distances.Levels; level++)
        {
            if (distances.ReachCentimetres(level) <= distances.ReachCentimetres(level - 1))
            {
                var before = distances.ReachMetres(level - 1);
                var reach = distances.ReachMetres(level);
                failures.Add(
                    $"{Prefix}Voxels:Level0ReachMetres ({voxels.Level0ReachMetres}) and {Prefix}Voxels:ReachGrowth ({voxels.ReachGrowth}) draw level {level} to {reach:F2} m, no farther than level {level - 1} ({before:F2} m); lower the reach or raise {Prefix}Voxels:StreamingRadiusMetres ({voxels.StreamingRadiusMetresFor(grid):F2} m)."
                );
                return;
            }
        }
    }

    private static void Range(List<string> failures, string key, double value, double minimum, double maximum)
    {
        // Written so NaN fails too.
        if (!(value >= minimum && value <= maximum))
        {
            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Prefix}{key} must be between {minimum} and {maximum}; it is {value}."
                )
            );
        }
    }
}
