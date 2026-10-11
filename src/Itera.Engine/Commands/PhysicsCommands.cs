using System.Globalization;
using System.Numerics;
using Itera.Engine.Matches;
using Itera.Engine.Physics;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's <c>physics</c> command (<see cref="CommandScope.Local"/>: each game's own physics): the match's bodies
/// and statics, its solver's iterations and substeps, its damping, and its gravity; and for this match, the solver's
/// iterations and substeps (<see cref="PhysicsWorld.SetSolver"/>), the damping (<see cref="PhysicsWorld.SetDamping"/>),
/// and the gravity (<see cref="PhysicsWorld.SetGravity"/>); and the run's physics materials.
/// </summary>
public sealed class PhysicsCommands(ConsoleCommands commands, MatchControl matches, PhysicsMaterials materials)
{
    /// <summary>Adds the command to the registry. Once, at startup.</summary>
    public void Register()
    {
        commands.Register(
            "physics",
            "[materials|solver ITERATIONS SUBSTEPS|damping LINEAR ANGULAR|gravity X Y Z]",
            "Physics: the match's bodies and statics, solver, damping, gravity, and bounces; materials lists the run's physics materials; solver sets its velocity iterations (1 to 32) and substeps (1 to 8), damping the shares of speed and spin bodies lose each second (0 to 1), gravity its gravity (m/s², each part -100 to 100; characters fall by its strength's share of 9.81), for this match.",
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
                context.Print(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{materials.NameOf(id)}: friction {material.Friction:0.###}, bounciness {material.Bounciness:0.###}"
                    )
                );
            }

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
                $"physics: {all} bodies ({bodies.ActiveSet.Count} awake), {physics.Simulation.Statics.Count} statics; solver {physics.VelocityIterations} iterations, {physics.Substeps} substep(s); damping {physics.LinearDamping:0.###} linear, {physics.AngularDamping:0.###} angular; gravity ({gravity.X:0.##}, {gravity.Y:0.##}, {gravity.Z:0.##}); {physics.Bounces} bounces set, {physics.ContactsDropped} contacts dropped"
            )
        );
    }
}
