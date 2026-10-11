using System.Globalization;
using System.Numerics;
using Itera.Engine.Audio;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Loading;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.MeshSample.Systems;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.MeshSample.Tests;

/// <summary>
/// <see cref="SampleSoundSystem"/> in the sample played without a window or a sound device, its sounds kept for the test
/// (<see cref="AudioService.QueueWithoutThread"/>, sound on), with no loading screen: the yard's music plays from the match's start,
/// the screen's hum is held at its picture's middle each frame, and the chime plays once at the listener (with a loading
/// screen, once it no longer covers the picture, and again after it covers it once more); a clang sounds
/// at a block's middle as the aim comes onto it, once, again when the aim comes back, and at another block; a frame
/// allocates nothing.
/// </summary>
public sealed class SampleSoundSystemTests
{
    private static readonly Dictionary<string, string?> s_options = new()
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
        ["Engine:Loading:Enabled"] = "false",
    };

    private static readonly float s_eyeHeight = CharacterSize.ForHeight(1.7f).EyeMetres;

    [Fact]
    public void The_match_plays_the_yards_music_holds_the_hum_and_chimes_once()
    {
        using var provider = SampleComposition.Build(s_options);
        var audio = provider.GetRequiredService<AudioService>();
        audio.Enabled = true;
        audio.QueueWithoutThread = true;
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());

        var chime = Recipe(audio, SampleSounds.Chime);
        var hum = Recipe(audio, SampleSounds.Hum);
        var speaker = match.Services.GetRequiredService<WorldOrigin>().ToLocal(new WorldPoint(-3.3, 5, -12));
        Assert.Equal(SampleSounds.YardMusic, audio.Music.CurrentName);
        var played = audio.QueuedEvents.ToArray();
        var chimes = played.Where(sound => sound.Recipe == chime).ToArray();
        Assert.True(Assert.Single(chimes).AtListener);
        var loops = audio.QueuedLoops.ToArray();
        Assert.Equal(2, loops.Length);
        Assert.All(loops, loop => Assert.Equal(hum, loop.Recipe));
        // The screen (its panel's foot at -5, 0, -12) shows its picture from 0.1 to 3.3 m along x and 4.1 to 5.9 m up.
        Assert.All(loops, loop => AssertNear(speaker + new Vector3(0, 0, 0.004f), loop.Position));

        audio.Publish(ListenerPose.Default, 0);
        match.Step(10);

        Assert.DoesNotContain(audio.QueuedEvents.ToArray(), sound => sound.Recipe == chime);
        Assert.Equal(10, audio.QueuedLoops.Length);
    }

    [Fact]
    public void The_chime_waits_for_the_loading_screen_and_plays_again_after_it_covers_the_picture_again()
    {
        var options = new Dictionary<string, string?>(s_options) { ["Engine:Loading:Enabled"] = "true" };
        using var provider = SampleComposition.Build(options);
        var audio = provider.GetRequiredService<AudioService>();
        audio.Enabled = true;
        audio.QueueWithoutThread = true;
        var loading = provider.GetRequiredService<LoadingScreen>();
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var chime = Recipe(audio, SampleSounds.Chime);

        // A headless match never moves the loading screen on: it covers the picture until the test lifts it.
        match.Step(10);
        var covered = Count(audio, chime);
        loading.End();
        match.Step(10);
        var lifted = Count(audio, chime);
        loading.BeginAgain(match.Seconds);
        match.Step(10);
        var again = Count(audio, chime);
        loading.End();
        match.Step(10);

        Assert.True(covered == 0 && lifted == 1 && again == 0, $"{covered}, {lifted}, {again}");
        Assert.Equal(1, Count(audio, chime));
    }

    [Fact]
    public void A_clang_sounds_at_a_block_as_the_aim_comes_onto_it()
    {
        using var provider = SampleComposition.Build(s_options);
        var audio = provider.GetRequiredService<AudioService>();
        audio.Enabled = true;
        audio.QueueWithoutThread = true;
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var clang = Recipe(audio, SampleSounds.Clang);

        // The 2 m block (x -4 to -2, z -6 to -4), from 0.5 m off its +z face: one clang at its middle, however long aimed.
        var first = Clangs(provider, match, clang, new Vector3(-3, s_eyeHeight, -3.5f), pitch: 0);
        var away = Clangs(provider, match, clang, new Vector3(-3, s_eyeHeight, -3.5f), pitch: 70);
        var back = Clangs(provider, match, clang, new Vector3(-3, s_eyeHeight, -3.5f), pitch: 0);
        // The 3 m block (x 0 to 3, z -8 to -5).
        var other = Clangs(provider, match, clang, new Vector3(1.5f, s_eyeHeight, -4.5f), pitch: 0);

        AssertNear(new Vector3(-3, 1, -5), Assert.Single(first).Position);
        Assert.Empty(away);
        AssertNear(new Vector3(-3, 1, -5), Assert.Single(back).Position);
        AssertNear(new Vector3(1.5f, 1.5f, -6.5f), Assert.Single(other).Position);
    }

    [Fact]
    public void A_frame_allocates_nothing()
    {
        using var provider = SampleComposition.Build(s_options);
        var audio = provider.GetRequiredService<AudioService>();
        audio.Enabled = true;
        audio.QueueWithoutThread = true;
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        Teleport(provider, new Vector3(-3, s_eyeHeight, -3.5f), pitch: 0);
        match.Step(10);
        audio.Publish(ListenerPose.Default, 0);
        var system = match.Services.GetServices<IFrameUpdateSystem>().OfType<SampleSoundSystem>().Single();
        var highlight = provider.GetRequiredService<AimHighlight>();
        var box = highlight.Transform;
        var block = highlight.Block;
        Assert.True(highlight.Active);

        // The aim off and back on every tenth frame: a clang each time (fewer than the events an iteration holds).
        var bytes = SystemAllocation.BytesAllocated(
            system,
            frame =>
            {
                if (frame % 10 == 0)
                {
                    highlight.Hide();
                }
                else
                {
                    highlight.Show(box, block);
                }

                return new FrameTime(1f / 60, frame / 60.0, (ulong)frame, 0);
            },
            warmup: 100,
            measured: 400
        );

        Assert.Equal(0, bytes);
        Assert.Equal(50, audio.QueuedEvents.Length);
    }

    /// <summary>The clangs played over 10 steps with the eye at <paramref name="eye"/>, looking along -z.</summary>
    private static SoundEvent[] Clangs(
        IServiceProvider provider,
        HeadlessMatch match,
        int clang,
        Vector3 eye,
        float pitch
    )
    {
        var audio = provider.GetRequiredService<AudioService>();
        audio.Publish(ListenerPose.Default, 0);
        Teleport(provider, eye, pitch);
        match.Step(10);
        return [.. audio.QueuedEvents.ToArray().Where(sound => sound.Recipe == clang)];
    }

    /// <summary>How many times <paramref name="recipe"/> was played since the last call; forgets them.</summary>
    private static int Count(AudioService audio, int recipe)
    {
        var count = audio.QueuedEvents.ToArray().Count(sound => sound.Recipe == recipe);
        audio.Publish(ListenerPose.Default, 0);
        return count;
    }

    private static int Recipe(AudioService audio, string name)
    {
        Assert.True(audio.Recipes.TryFind(name, out var recipe), name);
        return recipe;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"{actual} is not {expected}");

    private static void Teleport(IServiceProvider provider, Vector3 eye, float pitch)
    {
        var output = new Output();
        provider
            .GetRequiredService<ConsoleCommands>()
            .Execute(string.Create(CultureInfo.InvariantCulture, $"tp {eye.X} {eye.Y} {eye.Z} 0 {pitch}"), output);
        Assert.Empty(output.Failures);
    }

    private sealed class Output : IConsoleOutput
    {
        public List<string> Failures { get; } = [];

        public void Print(string text) { }

        public void PrintError(string text) => Failures.Add(text);
    }
}
