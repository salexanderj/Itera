using System.Globalization;
using System.Numerics;
using Itera.Engine.Matches;
using Itera.Engine.Particles;
using Itera.Engine.Rendering;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's <c>particles</c> command (<see cref="CommandScope.Local"/>): the counts, on and off, the recipes'
/// names, a test burst of a recipe where the camera looks (or pulled to the camera through its attractor, as gathered
/// voxels fly to a player), and the stress test for the benchmark.
/// </summary>
public sealed class ParticleCommands(
    ConsoleCommands commands,
    ParticleEmitter emitter,
    ParticleStats stats,
    RenderView view,
    MatchControl matches
)
{
    /// <summary>How far a test burst may be from the camera, metres; one that hits nothing is made this far ahead.</summary>
    private const float AimMetres = 30;

    /// <summary>Adds the command to the registry. Once, at startup.</summary>
    public void Register()
    {
        commands.Register(
            "particles",
            "[on|off|recipes|test NAME [COUNT]|pull NAME [COUNT]|stress NAME BURSTS|stress off]",
            "Particles: the counts, on or off, the recipes, a burst where the camera looks (pull: flying to the camera), or N bursts a frame there.",
            CommandScope.Local,
            Particles
        );
    }

    private void Particles(CommandContext context)
    {
        if (context.Count == 0)
        {
            var culture = CultureInfo.InvariantCulture;
            context.Print(
                string.Create(
                    culture,
                    $"particles {(emitter.Enabled ? "on" : "off")}: {stats.Living} living ({stats.Cubes} cubes and {stats.Sprites} sprites drawn) of {emitter.Capacity}, {emitter.Recipes.Count} recipes, {emitter.DroppedBursts} bursts dropped, {stats.Overwritten} living overwritten"
                )
            );
            if (emitter.StressBursts > 0)
            {
                context.Print(
                    string.Create(
                        culture,
                        $"stress test: {emitter.StressBursts} bursts of {emitter.Recipes.NameOf(emitter.StressRecipe)} a frame"
                    )
                );
            }

            return;
        }

        if (context.TryGetOnOff(0, out var on))
        {
            emitter.Enabled = on;
            context.Print(on ? "particles on" : "particles off");
            return;
        }

        if (context.Is(0, "recipes"))
        {
            for (var id = 0; id < emitter.Recipes.Count; id++)
            {
                context.Print(emitter.Recipes.NameOf(id));
            }

            return;
        }

        if (context.Is(0, "stress"))
        {
            Stress(context);
            return;
        }

        if ((context.Is(0, "test") || context.Is(0, "pull")) && context.Count >= 2)
        {
            Test(context, pull: context.Is(0, "pull"));
            return;
        }

        context.PrintUsage();
    }

    private void Test(CommandContext context, bool pull)
    {
        if (!TryRecipe(context, 1, out var recipe))
        {
            return;
        }

        var count = 100;
        if (context.Count >= 3 && (!context.TryGetInt(2, out count) || count < 1))
        {
            context.PrintUsage();
            return;
        }

        var (point, normal) = Aim();
        emitter.Emit(
            new ParticleBurst
            {
                Recipe = recipe,
                Count = count,
                Shape = ParticleShape.Sphere,
                Origin = point + normal * 0.05f,
                Radius = 0.1f,
                Direction = normal,
                Attractor = pull ? ParticleEmitter.CameraAttractor : 0,
            }
        );
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{count} of {emitter.Recipes.NameOf(recipe)} at ({point.X:F2}, {point.Y:F2}, {point.Z:F2})"
            )
        );
    }

    private void Stress(CommandContext context)
    {
        if (context.Count == 2 && context.Is(1, "off"))
        {
            emitter.StressBursts = 0;
            context.Print("stress test off");
            return;
        }

        if (context.Count != 3 || !TryRecipe(context, 1, out var recipe))
        {
            if (context.Count != 3)
            {
                context.PrintUsage();
            }

            return;
        }

        if (!context.TryGetInt(2, out var bursts) || bursts < 1)
        {
            context.PrintUsage();
            return;
        }

        if (bursts > emitter.MaxBursts)
        {
            // Every frame loops over them: a typo's count froze the game.
            context.PrintError(
                string.Create(CultureInfo.InvariantCulture, $"stress test: at most {emitter.MaxBursts} bursts a frame")
            );
            return;
        }

        emitter.StressRecipe = recipe;
        emitter.StressBursts = bursts;
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"stress test: {bursts} bursts of {emitter.StressCount} {emitter.Recipes.NameOf(recipe)} a frame where the camera looks"
            )
        );
    }

    /// <summary>
    /// Keeps the stress test's bursts where the camera looks, while it runs. Main thread, each frame built, after the
    /// frame systems placed the camera and before the particles are written.
    /// </summary>
    public void UpdateStress()
    {
        if (emitter.StressBursts > 0)
        {
            var (point, normal) = Aim();
            emitter.StressOrigin = point + normal * 0.3f;
        }
    }

    private bool TryRecipe(CommandContext context, int index, out int recipe)
    {
        if (emitter.Recipes.TryFind(context[index], out recipe))
        {
            return true;
        }

        context.PrintError($"{context[index]}: no such recipe; 'particles recipes' lists them.");
        return false;
    }

    /// <summary>Where the camera looks: the voxel it hits within reach and that face's normal, or a point ahead and up.</summary>
    private (Vector3 Point, Vector3 Normal) Aim()
    {
        var camera = view.Camera;
        var forward = camera.Forward;
        // Without a match's world, the point ahead.
        if (matches.World is { } voxels && voxels.Raycast(camera.Position, forward, AimMetres, out var hit))
        {
            return (
                camera.Position + forward * hit.DistanceMetres,
                new Vector3(hit.Normal.X, hit.Normal.Y, hit.Normal.Z)
            );
        }

        return (camera.Position + forward * 10, Vector3.UnitY);
    }
}
