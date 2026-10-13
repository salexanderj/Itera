using Itera.Engine.Diagnostics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Tests.Matches;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// A game's render passes (<see cref="IRenderPass"/>) in the engine's composition: one at every stage resolves with the
/// loop, in the order the game registered them; without graphics none is made, written, or recorded, and each is
/// disposed once however often the engine disposes them; more than can be timed, or two of one name, or a pass without
/// one, stop the run's step; a pass reads no picture unless it says so.
/// </summary>
public sealed class RenderPassesTests
{
    [Fact]
    public void A_pass_at_every_stage_resolves_with_the_loop_and_does_nothing_without_graphics()
    {
        var passes = Enum.GetValues<RenderStage>().Select(stage => new TestPass($"test {stage}", stage)).ToArray();
        using var provider = EngineComposition.Build(
            configure: services =>
            {
                foreach (var pass in passes)
                {
                    services.AddSingleton<IRenderPass>(pass);
                }
            },
            terrain: false
        );

        Assert.NotNull(provider.GetRequiredService<GameLoop>());
        var renderPasses = provider.GetRequiredService<RenderPasses>();
        Assert.Equal(passes, renderPasses.Passes);
        var time = new FrameTime(1f / 60, 1, 1, 0);
        var view = default(ViewConstants);
        renderPasses.Write(0, in time, in view);
        Assert.All(Enum.GetValues<RenderStage>(), stage => Assert.False(renderPasses.Has(stage)));
        renderPasses.Dispose();
        renderPasses.Dispose();

        Assert.All(passes, pass => Assert.Equal((0, 0, 1), (pass.Initialized, pass.Written, pass.Disposed)));
    }

    [Fact]
    public void A_pass_reads_no_picture_unless_it_says_so()
    {
        IRenderPass pass = new TestPass("screen", RenderStage.Screen);

        Assert.All(Enumerable.Range(0, RenderPassSetup.FramesInFlight), slot => Assert.False(pass.ReadsPicture(slot)));
        Assert.Equal(0ul, default(RenderPassContext).Picture.Handle);
    }

    [Fact]
    public void More_passes_than_are_timed_or_names_empty_or_used_twice_are_refused()
    {
        var most = Enumerable.Range(0, GpuTimings.MostGamePasses).Select(i => new TestPass($"pass {i}")).ToArray();

        RenderPasses.Check(most);
        Assert.Throws<InvalidOperationException>(() => RenderPasses.Check([.. most, new TestPass("one more")]));
        Assert.Throws<InvalidOperationException>(() => RenderPasses.Check([new TestPass("a"), new TestPass("a")]));
        Assert.Throws<InvalidOperationException>(() => RenderPasses.Check([new TestPass(" ")]));
    }

    private sealed class TestPass(string name, RenderStage stage = RenderStage.Opaque) : IRenderPass
    {
        public string Name => name;

        public RenderStage Stage => stage;

        public int Initialized { get; private set; }

        public int Written { get; private set; }

        public int Disposed { get; private set; }

        public void Initialize(RenderPassSetup setup) => Initialized++;

        public void Write(int slot, in RenderPassFrame frame) => Written++;

        public void Record(in RenderPassContext context) =>
            throw new InvalidOperationException("Recorded without graphics.");

        public void OnTargetsChanged(RenderPassSetup setup) { }

        public object CompileShaders() => Array.Empty<byte[]>();

        public int ReplacePipelines(object compiled) => 0;

        public void Dispose() => Disposed++;
    }
}
