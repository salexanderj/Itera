using Itera.Engine.Modding;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Modding;

/// <summary>Loads the real sample mod, built and copied into TestMods/ by the project file.</summary>
public sealed class ModLoaderIntegrationTests
{
    private static readonly string s_modsDirectory = Path.Combine(AppContext.BaseDirectory, "TestMods");

    [Fact]
    public void Sample_mod_loads_runs_unloads_and_is_collected()
    {
        Assert.True(
            File.Exists(Path.Combine(s_modsDirectory, "SampleMod", "Itera.SampleMod.dll")),
            "sample mod output missing; build the solution first"
        );

        using var loader = CreateLoader();

        var summary = loader.LoadAll();

        Assert.Equal(new ModLoadSummary(Found: 1, Loaded: 1, Failed: 0), summary);
        Assert.Equal(["itera.samplemod"], loader.LoadedModIds);

        var step = new FixedStepTime(1f / 60f, 60);
        loader.FixedUpdate(in step);
        var frame = new FrameTime(1f / 60f, 1.0, 1, 0f);
        loader.Update(in frame);
        loader.NotifyGameExit();

        Assert.True(loader.Unload("itera.samplemod"));
        Assert.Empty(loader.LoadedModIds);
        Assert.True(loader.LastUnloadWasCollected, "the sample mod's load context was not collected after unload");
    }

    [Fact]
    public void Unloading_an_unknown_id_returns_false()
    {
        using var loader = CreateLoader();

        Assert.False(loader.Unload("does.not.exist"));
    }

    [Fact]
    public void Missing_mods_directory_is_not_an_error()
    {
        var options = Options.Create(
            new EngineOptions { Mods = new ModsSettings { Directory = Path.Combine(s_modsDirectory, "missing") } }
        );
        using var loader = new ModLoader(
            options,
            NullLoggerFactory.Instance,
            EmptyServiceProvider.Instance,
            NullLogger<ModLoader>.Instance
        );

        Assert.Equal(new ModLoadSummary(0, 0, 0), loader.LoadAll());
    }

    private static ModLoader CreateLoader()
    {
        var options = Options.Create(new EngineOptions { Mods = new ModsSettings { Directory = s_modsDirectory } });
        return new ModLoader(
            options,
            NullLoggerFactory.Instance,
            EmptyServiceProvider.Instance,
            NullLogger<ModLoader>.Instance
        );
    }
}
