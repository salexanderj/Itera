using System.Globalization;
using System.Numerics;
using System.Text;
using Itera.Engine.Matches;
using Itera.Engine.Physics;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's <c>physics</c> command (<see cref="CommandScope.Local"/>: each game's own physics): the match's bodies
/// and statics, its solver's iterations and substeps, its damping, and its gravity; and for this match, the solver's
/// iterations and substeps (<see cref="PhysicsWorld.SetSolver"/>), the damping (<see cref="PhysicsWorld.SetDamping"/>),
/// and the gravity (<see cref="PhysicsWorld.SetGravity"/>); the run's physics materials; and the layers, which touch
/// which (the match's table in a match, else the run's), and which the characters and the voxels are in.
/// </summary>
public sealed class PhysicsCommands(
    ConsoleCommands commands,
    MatchControl matches,
    PhysicsMaterials materials,
    PhysicsLayers layers
)
{
    /// <summary>Adds the command to the registry. Once, at startup.</summary>
    public void Register()
    {
        commands.Register(
            "physics",
            "[materials|layers|solver ITERATIONS SUBSTEPS|damping LINEAR ANGULAR|gravity X Y Z]",
            "Physics: the match's bodies and statics, solver, damping, gravity, and bounces; materials lists the run's physics materials; layers the collision layers and which touch; solver sets its velocity iterations (1 to 32) and substeps (1 to 8), damping the shares of speed and spin bodies lose each second (0 to 1), gravity its gravity (m/s², each part -100 to 100; characters fall by its strength's share of 9.81), for this match.",
            CommandScope.Local,
            Physics
        );
    }

    private void Physics(CommandContext context)
    {
        if (context.Is(0, "materials") && context.Count == 1)
        {
            for (var i = 0; i < materials.Count; i++)
            {
                var id = new PhysicsMaterialId((byte)i);
                var material = materials[id];
                // The voxel materials mapped to it (the default's are every other).
                var voxels = new StringBuilder();
                for (var voxel = 1; voxel < 256 && i > 0; voxel++)
                {
                    if (materials.OfVoxel((byte)voxel) == id)
                    {
                        voxels.Append(voxels.Length == 0 ? "; voxels " : ", ").Append(voxel);
                    }
                }

                context.Print(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{materials.NameOf(id)}: friction {material.Friction:0.###}, bounciness {material.Bounciness:0.###}{voxels}"
                    )
                );
            }

            return;
        }

        if (context.Is(0, "layers") && context.Count == 1)
        {
            PrintLayers(context, matches.Current?.Physics is { IsInitialized: true } match ? match : null);
            return;
        }

        if (matches.Current?.Physics is not { IsInitialized: true } physics)
        {
            context.PrintError("no match: physics belongs to a match");
            return;
        }

        if (context.Count == 0)
        {
            Print(context, physics);
            return;
        }

        if (context.Is(0, "solver") && context.Count == 3)
        {
            if (
                context.TryGetInt(1, out var iterations)
                && context.TryGetInt(2, out var substeps)
                && iterations is >= 1 and <= PhysicsSettings.MostVelocityIterations
                && substeps is >= 1 and <= PhysicsSettings.MostSubsteps
            )
            {
                physics.SetSolver(iterations, substeps);
                Print(context, physics);
            }
            else
            {
                context.PrintUsage();
            }

            return;
        }

        if (context.Is(0, "damping") && context.Count == 3)
        {
            if (
                context.TryGetFloat(1, out var linear)
                && context.TryGetFloat(2, out var angular)
                && linear is >= 0 and <= 1
                && angular is >= 0 and <= 1
            )
            {
                physics.SetDamping(linear, angular);
                Print(context, physics);
            }
            else
            {
                context.PrintUsage();
            }

            return;
        }

        if (context.Is(0, "gravity") && context.Count == 4)
        {
            if (
                context.TryGetFloat(1, out var x)
                && context.TryGetFloat(2, out var y)
                && context.TryGetFloat(3, out var z)
                && MathF.Max(MathF.Abs(x), MathF.Max(MathF.Abs(y), MathF.Abs(z))) <= GravitySettings.Most
            )
            {
                physics.SetGravity(new Vector3(x, y, z));
                Print(context, physics);
            }
            else
            {
                context.PrintUsage();
            }

            return;
        }

        context.PrintUsage();
    }

    private static void Print(CommandContext context, PhysicsWorld physics)
    {
        var bodies = physics.Simulation.Bodies;
        var all = 0;
        for (var i = 0; i < bodies.Sets.Length; i++)
        {
            all += bodies.Sets[i].Allocated ? bodies.Sets[i].Count : 0;
        }

        var gravity = physics.Gravity;
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"physics: {all} bodies ({bodies.ActiveSet.Count} awake), {physics.Simulation.Statics.Count} statics; solver {physics.VelocityIterations} iterations, {physics.Substeps} substep(s); damping {physics.LinearDamping:0.###} linear, {physics.AngularDamping:0.###} angular; gravity ({gravity.X:0.##}, {gravity.Y:0.##}, {gravity.Z:0.##}); {physics.Bounces} bounces set, {physics.ContactsDropped} contacts dropped; {physics.TriggerCount} trigger zone(s), {physics.TriggerEventsDropped} trigger overlaps and events dropped"
            )
        );
    }

    /// <summary>Each layer, what it is for, and the layers it does not touch: the match's table, or the run's.</summary>
    private void PrintLayers(CommandContext context, PhysicsWorld? match)
    {
        var characters = match?.CharacterLayer ?? layers.CharacterLayer;
        var voxels = match?.VoxelLayer ?? layers.VoxelLayer;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = new PhysicsLayerId((byte)i);
            var line = new StringBuilder(layers.NameOf(layer));
            if (layer == characters || layer == voxels)
            {
                line.Append(
                    layer == characters && layer == voxels ? " (characters, voxels)"
                    : layer == characters ? " (characters)"
                    : " (voxels)"
                );
            }

            var apart = new StringBuilder();
            var count = 0;
            for (var j = 0; j < layers.Count; j++)
            {
                var other = new PhysicsLayerId((byte)j);
                if (!(match?.Touches(layer, other) ?? layers.Touches(layer, other)))
                {
                    apart.Append(count++ == 0 ? "" : ", ").Append(layers.NameOf(other));
                }
            }

            line.Append(
                count == 0 ? ": touches all"
                : count == layers.Count ? ": touches none"
                : ": touches all but "
            );
            if (count > 0 && count < layers.Count)
            {
                line.Append(apart);
            }

            context.Print(line.ToString());
        }
    }
}
