using System.Globalization;
using Arch.Core;
using Itera.Engine.Ecs;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's <c>ecs</c>, which reads the match's entities (<see cref="EcsInspector"/>): <c>counts</c> (the
/// archetypes and how many entities each holds), <c>list COMPONENT [N]</c> (the entities with that component and its
/// fields), and <c>show ID[:VERSION]|player</c> (every component of one entity). Registered when a match's ECS world
/// starts, so it goes when the match ends. Reads only, so any peer may use it.
/// </summary>
public static class EcsCommands
{
    /// <summary>Entities <c>ecs list</c> prints unless told another number.</summary>
    public const int DefaultListed = 20;

    /// <summary>Adds <c>ecs</c> over <paramref name="world"/> to <paramref name="commands"/>.</summary>
    public static void Register(ConsoleCommands commands, World world)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(world);
        commands.Register(
            "ecs",
            "counts | list COMPONENT [N] | show ID[:VERSION]|player",
            "Reads the match's entities: counts lists the archetypes (each set of components) with how many entities have it; list prints the first N (20) entities with COMPONENT and that component's fields; show prints every component of one entity, by its id (as list prints it) or the local player's.",
            CommandScope.Local,
            context => Run(context, world)
        );
    }

    private static void Run(CommandContext context, World world)
    {
        if (context.Count == 0)
        {
            context.PrintUsage();
            return;
        }

        if (context.Is(0, "counts") && context.Count == 1)
        {
            Counts(context, world);
        }
        else if (context.Is(0, "list") && context.Count is 2 or 3)
        {
            List(context, world);
        }
        else if (context.Is(0, "show") && context.Count == 2)
        {
            Show(context, world);
        }
        else
        {
            context.PrintUsage();
        }
    }

    private static void Counts(CommandContext context, World world)
    {
        var rows = EcsInspector.Archetypes(world);
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"ecs: {world.Size} entities in {rows.Count} archetype(s) with entities"
            )
        );
        foreach (var (count, components) in rows)
        {
            context.PrintColumns(count.ToString(CultureInfo.InvariantCulture), components, 8, 8);
        }
    }

    private static void List(CommandContext context, World world)
    {
        var most = DefaultListed;
        if (context.Count == 3 && !(context.TryGetInt(2, out most) && most is > 0 and <= EcsInspector.MostListed))
        {
            context.PrintUsage();
            return;
        }

        if (!EcsInspector.TryFindComponent(world, context[1], out var type))
        {
            context.PrintError($"ecs: no entity has a component named {context[1]}.");
            return;
        }

        var entities = EcsInspector.EntitiesWith(world, type, int.MaxValue);
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"ecs: {entities.Count} entities with {type.Type.Name}{(entities.Count > most ? $", the first {most}" : "")}"
            )
        );
        for (var i = 0; i < entities.Count && i < most; i++)
        {
            var entity = entities[i];
            context.PrintColumns(
                EcsInspector.Name(entity),
                EcsInspector.Describe(EcsInspector.Component(world, entity, type)),
                10,
                10
            );
        }
    }

    private static void Show(CommandContext context, World world)
    {
        if (!EcsInspector.TryFindEntityOrPlayer(world, context[1], out var entity))
        {
            context.PrintError(
                context.Is(1, "player") ? "ecs: no entity is the local player." : $"ecs: no living entity {context[1]}."
            );
            return;
        }

        var components = EcsInspector.Components(world, entity);
        context.Print($"ecs: {EcsInspector.Name(entity)}, {components.Count} component(s)");
        foreach (var (name, value) in components)
        {
            context.PrintColumns(name, EcsInspector.Describe(value), 24, 24);
        }
    }
}
