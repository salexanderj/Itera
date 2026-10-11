using System.Globalization;
using System.Numerics;
using Itera.Engine.Audio;
using Itera.Engine.Matches;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Itera.Engine.Time;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's <c>sound</c> command (<see cref="CommandScope.Local"/>): the counts, on and off, the volume, the
/// recipes' names and kinds, the sounds read from files (each file's channels, rate, length, and problems), a test of a
/// recipe where the camera looks (a one-shot once, at a level; a stream or a file loop for 4 s) or at the listener, and WAV files of
/// recipes in the data folder's <c>Sounds</c>, to listen to outside the game (with a level, that level's variations a
/// file each, or 8 s of a stream at it: material for sound files). The volume is the player's setting
/// (<see cref="PlayerSettings"/>), as the menu's. And the <c>music</c> command: what plays (the decks' tracks and gains,
/// the music thread's work), the tracks, and a track played or stopped with a fade (<see cref="MusicPlayer"/>).
/// </summary>
public sealed class SoundCommands(
    ConsoleCommands commands,
    AudioService audio,
    RenderView view,
    MatchControl matches,
    GameClock clock,
    IOptions<EngineOptions> options,
    PlayerSettings settings
)
{
    /// <summary>How far a test sound may be from the camera, metres; one that hits nothing is made this far ahead.</summary>
    private const float AimMetres = 30;

    /// <summary>How long a tested stream plays, seconds.</summary>
    private const double StreamTestSeconds = 4;

    /// <summary>Adds the command to the registry. Once, at startup.</summary>
    public void Register()
    {
        commands.Register(
            "sound",
            "[on|off|volume V|recipes|files|test NAME [LEVEL]|play NAME|wav NAME [LEVEL]|wav all]",
            "Sound: the counts, on or off, the master volume (0 to 1), the recipes, the sounds from files, one where the camera looks (a level 0 to 1) or at the listener, or WAV files of recipes (with a level, that level's variations a file each, or 8 s of a stream at it).",
            CommandScope.Local,
            Sound
        );
        commands.Register(
            "music",
            "[list|play NAME [SECONDS]|stop [SECONDS]]",
            "Music: what plays, the tracks, a track played (cross-fading over SECONDS, 0 to 60, 2 by default), or the music stopped (fading out).",
            CommandScope.Local,
            Music
        );
    }

    private void Music(CommandContext context)
    {
        var music = audio.Music;
        if (context.Count == 0)
        {
            PrintMusic(context);
            return;
        }

        if (context.Is(0, "list") && context.Count == 1)
        {
            ListTracks(context);
            return;
        }

        if (context.Is(0, "play") && context.Count is 2 or 3)
        {
            if (!music.TryFind(context[1], out var track))
            {
                context.PrintError($"{context[1]}: no such music track; 'music list' lists them.");
                return;
            }

            if (!TryFade(context, 2, out var fade))
            {
                return;
            }

            if (music.Tracks[track].Error is { } error)
            {
                context.PrintError($"{context[1]} cannot be played: {error}");
                return;
            }

            if (music.Current == track)
            {
                context.Print(context[1] + " plays already");
                return;
            }

            music.Play(track, fade);
            context.Print(
                string.Create(CultureInfo.InvariantCulture, $"{context[1]} plays, fading over {fade:0.##} s")
            );
            return;
        }

        if (context.Is(0, "stop") && context.Count is 1 or 2)
        {
            if (TryFade(context, 1, out var fade))
            {
                music.Stop(fade);
                context.Print(string.Create(CultureInfo.InvariantCulture, $"music stops, fading over {fade:0.##} s"));
            }

            return;
        }

        context.PrintUsage();
    }

    /// <summary>The fade's seconds at <paramref name="index"/>, or the default when not given; false (with the usage) when out of range.</summary>
    private static bool TryFade(CommandContext context, int index, out float fade)
    {
        fade = MusicPlayer.DefaultFadeSeconds;
        if (context.Count <= index)
        {
            return true;
        }

        if (context.TryGetFloat(index, out fade) && fade is >= 0 and <= MusicPlayer.MaxFadeSeconds)
        {
            return true;
        }

        context.PrintUsage();
        return false;
    }

    private void PrintMusic(CommandContext context)
    {
        var music = audio.Music;
        var playing =
            music.Current < 0 ? "nothing"
            : music.Tracks[music.Current].Loop ? music.CurrentName + " (looping)"
            : music.CurrentName + " (once)";
        var sound = audio.IsRunning ? "" : " (no sound: " + audio.DeviceName + ")";
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"music: {playing}{sound}, music volume {audio.MusicVolume:0.##} under the master {audio.Volume:0.##}; {music.Tracks.Count} track(s)"
            )
        );
        if (!audio.IsRunning || music.Tracks.Count == 0)
        {
            return;
        }

        var stats = audio.Stats;
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"decks: 1 {Deck(0)}, 2 {Deck(1)}; {stats.MusicUnderruns} underrun(s); the music thread decoded {audio.MusicDecodedSeconds:0.#} s and allocated {audio.MusicAllocatedBytes / 1_048_576.0:0.#} MB"
            )
        );
    }

    /// <summary>What music deck <paramref name="deck"/> plays, and how loud.</summary>
    private string Deck(int deck)
    {
        var track = audio.Stats.MusicDeckTrack(deck);
        return track < 0 || track >= audio.Music.Tracks.Count
            ? "silent"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{audio.Music.Tracks[track].Name} at gain {audio.Stats.MusicDeckGain(deck):0.###}"
            );
    }

    private void ListTracks(CommandContext context)
    {
        var music = audio.Music;
        if (music.Tracks.Count == 0)
        {
            context.Print("no music tracks");
            return;
        }

        for (var i = 0; i < music.Tracks.Count; i++)
        {
            var track = music.Tracks[i];
            var path = Path.GetRelativePath(AppContext.BaseDirectory, track.Path);
            var menu = i == music.MenuTrack ? " (the menu's)" : "";
            context.Print($"{track.Name}: {path}, {DescribeTrack(track)}{menu}");
        }
    }

    private static string DescribeTrack(MusicTrack track)
    {
        if (track.Error is { } error)
        {
            return "cannot be read (" + error + ")";
        }

        if (track.Channels == 0)
        {
            return "not read yet";
        }

        var loop =
            !track.Loop ? "plays once"
            : track.LoopPoints.IsSet
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"loops {track.LoopPoints.Start} to {track.LoopPoints.End}"
                )
            : "loops whole";
        var problem = track.Warning is null ? "" : " (" + track.Warning + ")";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(track.Channels == 2 ? "stereo" : "mono")}, {track.SampleRate} Hz, {track.Seconds:0.#} s, {loop}{problem}"
        );
    }

    private void Sound(CommandContext context)
    {
        if (context.Count == 0)
        {
            PrintCounts(context);
            return;
        }

        if (context.TryGetOnOff(0, out var on))
        {
            audio.Enabled = on;
            context.Print(on ? "sound on" : "sound off");
            return;
        }

        if (context.Is(0, "volume"))
        {
            if (context.Count == 1)
            {
                context.Print("volume " + CommandContext.Number(audio.Volume));
            }
            else if (context.TryGetFloat(1, out var volume) && volume is >= 0 and <= 1)
            {
                settings.SetVolume(volume);
                context.Print("volume " + CommandContext.Number(audio.Volume));
            }
            else
            {
                context.PrintUsage();
            }

            return;
        }

        if (context.Is(0, "recipes"))
        {
            for (var id = 0; id < audio.Recipes.Count; id++)
            {
                context.Print(audio.Recipes.NameOf(id) + " (" + audio.Recipes.KindOf(id) + ")");
            }

            return;
        }

        if (context.Is(0, "files"))
        {
            PrintFiles(context);
            return;
        }

        if (context.Is(0, "test") && context.Count >= 2)
        {
            Test(context, atListener: false);
            return;
        }

        if (context.Is(0, "play") && context.Count == 2)
        {
            Test(context, atListener: true);
            return;
        }

        if (context.Is(0, "wav") && context.Count is 2 or 3)
        {
            Wav(context);
            return;
        }

        context.PrintUsage();
    }

    private void PrintCounts(CommandContext context)
    {
        var stats = audio.Stats;
        var state =
            !audio.IsRunning ? "silent (" + audio.DeviceName + ")"
            : !stats.Connected ? "waiting for an output"
            : audio.Enabled ? "on"
            : "off";
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"sound {state}, volume {audio.Volume:0.##} (music {audio.MusicVolume:0.##}, effects {audio.EffectsVolume:0.##} under it): {stats.OneShots} one-shots, {stats.Streams} streams, and {stats.FileLoops} file loops playing of {audio.MaxVoices} voices; {audio.Recipes.Count} recipes, {stats.RenderedRecipes} rendered ({stats.RenderedBytes / 1024} KB)"
            )
        );
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"since start: {stats.Played} played, {stats.Culled} too quiet, {stats.Stolen} cut short for louder ones, {stats.Refused} refused, {stats.Late} dropped while their recipe rendered, {audio.DroppedEvents} dropped, {stats.Underruns} stream underruns, {stats.MusicUnderruns} music underruns, {stats.DeviceErrors} device errors"
            )
        );
    }

    /// <summary>Each sound read from files, then each of its files as read (or why not), relative to the game's folder.</summary>
    private void PrintFiles(CommandContext context)
    {
        var any = false;
        for (var id = 0; id < audio.Recipes.Count; id++)
        {
            if (audio.Recipes[id] is not FileRecipe recipe)
            {
                continue;
            }

            any = true;
            context.Print($"{audio.Recipes.NameOf(id)} ({audio.Recipes.KindOf(id)}, {recipe.Paths.Count} file(s))");
            var files = audio.FilesOf(id);
            for (var i = 0; i < recipe.Paths.Count; i++)
            {
                var path = Path.GetRelativePath(AppContext.BaseDirectory, recipe.Paths[i]);
                context.Print("  " + path + ": " + Describe(files is not null && i < files.Count ? files[i] : null));
            }
        }

        if (!any)
        {
            context.Print("no sounds from files");
        }
    }

    private string Describe(SoundFileInfo? file)
    {
        if (file is null)
        {
            return audio.IsRunning ? "not read yet" : "not read (no sound: " + audio.DeviceName + ")";
        }

        if (file.Failed)
        {
            return "cannot be read (" + file.Problem + ")";
        }

        var loop = file.LoopPoints.IsSet
            ? string.Create(CultureInfo.InvariantCulture, $", loops {file.LoopPoints.Start} to {file.LoopPoints.End}")
            : "";
        var problem = file.Problem is null ? "" : " (" + file.Problem + ")";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(file.Channels == 2 ? "stereo" : "mono")}, {file.SampleRate} Hz, {file.Seconds:0.##} s, {file.Bytes / 1024} KB{loop}{problem}"
        );
    }

    private void Test(CommandContext context, bool atListener)
    {
        if (!TryRecipe(context, 1, out var recipe))
        {
            return;
        }

        var level = 1f;
        if (!atListener && context.Count >= 3 && (!context.TryGetFloat(2, out level) || level is < 0 or > 1))
        {
            context.PrintUsage();
            return;
        }

        if (!audio.IsRunning)
        {
            context.PrintError("no sound: " + audio.DeviceName);
            return;
        }

        var point = atListener ? Vector3.Zero : Aim();
        if (audio.Recipes.IsSustained(recipe))
        {
            audio.SustainFor(
                new SoundLoop
                {
                    Recipe = recipe,
                    Key = -1,
                    Position = point,
                    Level = level,
                    AtListener = atListener,
                },
                clock.TotalSeconds,
                StreamTestSeconds
            );
        }
        else
        {
            audio.Play(
                new SoundEvent
                {
                    Recipe = recipe,
                    Position = point,
                    Level = level,
                    AtListener = atListener,
                }
            );
        }

        context.Print(
            atListener
                ? audio.Recipes.NameOf(recipe) + " at the listener"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{audio.Recipes.NameOf(recipe)} at level {level:0.##} at ({point.X:F2}, {point.Y:F2}, {point.Z:F2})"
                )
        );
    }

    private void Wav(CommandContext context)
    {
        var folder = Path.Combine(options.Value.Application.DataDirectory(), "Sounds");
        if (context.Is(1, "all"))
        {
            if (context.Count == 3)
            {
                context.PrintUsage();
                return;
            }

            for (var id = 0; id < audio.Recipes.Count; id++)
            {
                if (audio.Recipes[id] is not FileRecipe)
                {
                    context.Print("wrote " + audio.WriteWav(id, folder));
                }
            }

            return;
        }

        if (!TryRecipe(context, 1, out var recipe))
        {
            return;
        }

        if (audio.Recipes[recipe] is FileRecipe)
        {
            context.PrintError($"{audio.Recipes.NameOf(recipe)} is read from files: they are its sound already.");
            return;
        }

        if (context.Count == 3)
        {
            if (!context.TryGetFloat(2, out var level) || level is < 0 or > 1)
            {
                context.PrintUsage();
                return;
            }

            foreach (var path in audio.WriteLevel(recipe, folder, level))
            {
                context.Print("wrote " + path);
            }

            return;
        }

        context.Print("wrote " + audio.WriteWav(recipe, folder));
    }

    private bool TryRecipe(CommandContext context, int index, out int recipe)
    {
        if (audio.Recipes.TryFind(context[index], out recipe))
        {
            return true;
        }

        context.PrintError($"{context[index]}: no such recipe; 'sound recipes' lists them.");
        return false;
    }

    /// <summary>Where the camera looks: the voxel it hits within reach, or a point 10 m ahead.</summary>
    private Vector3 Aim()
    {
        var camera = view.Camera;
        var forward = camera.Forward;
        // Without a match's world, the point ahead.
        return matches.World is { } voxels && voxels.Raycast(camera.Position, forward, AimMetres, out var hit)
            ? camera.Position + forward * hit.DistanceMetres
            : camera.Position + forward * 10;
    }
}
