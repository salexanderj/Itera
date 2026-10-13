using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Physics;
using Itera.Engine.Saves;
using Itera.Engine.Tests.Matches;
using Itera.Engine.Voxels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Saves;

/// <summary>
/// A game's sections in a match of the engine's real composition (<see cref="EngineComposition"/>, its save in a
/// temporary folder): at a clean end the last save is captured while the ECS world and the systems still exist, and the
/// next match of the save restores it before its first step.
/// </summary>
public sealed class SaveMatchTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "itera-save-match-tests-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void The_last_save_is_captured_while_the_ecs_world_lives_and_restored_before_the_first_step()
    {
        using var provider = Build();
        var run = new HeadlessRun(provider);
        int entities;
        using (var match = run.Play(seed: 5, saveName: "order"))
        {
            for (var i = 0; i < 3; i++)
            {
                match.World.Create(new CharacterVelocity());
            }

            match.Step(2);
            entities = match.World.Size;
            Assert.False(match.Match.Saves.StartedFromSave);
        }

        Assert.True(entities >= 3);
        using var again = run.Play(seed: 9, saveName: "order");
        var section = again.Services.GetServices<ISaveSection>().OfType<EntityCount>().Single();

        Assert.True(again.Match.Saves.StartedFromSave);
        Assert.Equal(5UL, again.Match.Session.Match.Seed);
        Assert.Equal(entities, section.Restored);
        Assert.Equal(0, section.StepsBeforeRestore);
    }

    /// <summary>The engine's composition with a save in the test's folder and the <see cref="EntityCount"/> section.</summary>
    private ServiceProvider Build() =>
        EngineComposition.Build(configure: services =>
        {
            services.AddScoped<ISaveSection, EntityCount>();
            services.AddScoped(sp => new SaveService(
                sp.GetRequiredService<MatchContext>().Start.SaveName,
                _folder,
                sp.GetService<VoxelWorld>(),
                sp.GetService<LooseBodies>(),
                NullLogger<SaveService>.Instance,
                sp.GetRequiredService<IMatchIdentity>()
            ));
        });

    /// <summary>A section of the ECS world's entity count, which says how many steps ran before it was restored.</summary>
    private sealed class EntityCount(EcsWorld ecs, IEnumerable<IFixedUpdateSystem> systems) : ISaveSection
    {
        public int Restored { get; private set; } = -1;

        public long StepsBeforeRestore { get; private set; } = -1;

        public string Name => "test.entities";

        public int Version => 1;

        public void Capture(BinaryWriter writer) => writer.Write(ecs.World.Size);

        public void Restore(BinaryReader reader, int version)
        {
            Restored = reader.ReadInt32();
            StepsBeforeRestore = systems.OfType<EngineComposition.StepCounter>().Single().Steps;
        }
    }
}
