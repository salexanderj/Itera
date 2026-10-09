using System.Numerics;
using System.Text;
using System.Text.Json;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Diagnostics;
using Itera.Engine.Ecs;
using Itera.Engine.Networking;
using Itera.Engine.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Ecs;

/// <summary>
/// <see cref="EcsInspector"/> and the console's <c>ecs</c>: archetypes and counts, entities by component, one entity's
/// components by id or as the local player, and values written as text and JSON.
/// </summary>
public sealed class EcsInspectorTests : IDisposable
{
    private readonly World _world = World.Create();

    public void Dispose() => World.Destroy(_world);

    [Fact]
    public void Archetypes_are_counted_most_entities_first_with_their_components_by_name()
    {
        _world.Create(new CharacterPosition());
        _world.Create(new CharacterPosition());
        _world.Create(new CharacterPosition(), new LocalPlayer());

        var rows = EcsInspector.Archetypes(_world);

        Assert.Equal([(2, "CharacterPosition"), (1, "CharacterPosition, LocalPlayer")], rows);
    }

    [Fact]
    public void Entities_with_a_component_are_found_by_its_name_in_any_case_in_id_order()
    {
        var first = _world.Create(new CharacterPosition());
        _world.Create(new LocalPlayer());
        var second = _world.Create(new CharacterPosition(), new LocalPlayer());

        Assert.True(EcsInspector.TryFindComponent(_world, "characterPOSITION", out var type));
        Assert.Equal([first, second], EcsInspector.EntitiesWith(_world, type));
        Assert.Equal([first], EcsInspector.EntitiesWith(_world, type, 1));
        Assert.False(EcsInspector.TryFindComponent(_world, "Nothing", out _));
    }

    [Fact]
    public void An_entity_is_found_by_its_id_and_version_and_a_stale_one_is_not()
    {
        var gone = _world.Create(new CharacterPosition());
        _world.Destroy(gone);
        var entity = _world.Create(new CharacterPosition());

        Assert.True(EcsInspector.TryFindEntity(_world, EcsInspector.Name(entity), out var found));
        Assert.Equal(entity, found);
        Assert.True(
            EcsInspector.TryFindEntity(
                _world,
                entity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                out _
            )
        );
        Assert.Equal(gone.Id, entity.Id);
        Assert.False(EcsInspector.TryFindEntity(_world, EcsInspector.Name(gone), out _));
        Assert.False(EcsInspector.TryFindEntity(_world, "x:1", out _));
    }

    [Fact]
    public void The_local_player_is_found_as_player()
    {
        _world.Create(new CharacterPosition());
        var player = _world.Create(new LocalPlayer(), new CharacterPosition());

        Assert.True(EcsInspector.TryFindEntityOrPlayer(_world, "player", out var found));
        Assert.Equal(player, found);
    }

    [Fact]
    public void Values_are_written_as_text_with_their_fields()
    {
        var entity = _world.Create(
            new CharacterPosition { Feet = new Vector3(1.5f, 2, -3.25f), PreviousFeet = Vector3.Zero },
            new PlayerMode { Flying = true },
            new LocalPlayer()
        );

        var components = EcsInspector.Components(_world, entity);

        Assert.Equal(["CharacterPosition", "LocalPlayer", "PlayerMode"], components.Select(c => c.Name));
        var position = EcsInspector.Describe(components[0].Value);
        Assert.Contains("Feet=(1.5, 2, -3.25)", position, StringComparison.Ordinal);
        Assert.Equal("{}", EcsInspector.Describe(components[1].Value));
        Assert.Contains("Flying=true", EcsInspector.Describe(components[2].Value), StringComparison.Ordinal);
    }

    [Fact]
    public void Values_are_written_as_json_with_vectors_as_arrays()
    {
        var value = new CharacterPosition { Feet = new Vector3(1, 2, 3) };

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            EcsInspector.WriteJson(writer, value);
        }

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        var feet = document.RootElement.GetProperty("Feet");
        Assert.Equal([1f, 2f, 3f], feet.EnumerateArray().Select(e => e.GetSingle()));
    }

    [Fact]
    public void The_ecs_command_counts_lists_and_shows()
    {
        var commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
        var console = new DevConsole(
            Options.Create(new EngineOptions()),
            commands,
            new ConsoleLogBuffer(),
            NullLogger<DevConsole>.Instance
        );
        EcsCommands.Register(commands, _world);
        var player = _world.Create(new LocalPlayer(), new CharacterPosition { Feet = new Vector3(0, 7, 0) });
        _world.Create(new CharacterPosition());

        var counts = Run(console, "ecs counts");
        var listed = Run(console, "ecs list CharacterPosition 1");
        var shown = Run(console, "ecs show player");
        var missing = Run(console, "ecs show 999");

        Assert.Equal("ecs: 2 entities in 2 archetype(s) with entities", counts[0]);
        Assert.Equal("ecs: 2 entities with CharacterPosition, the first 1", listed[0]);
        Assert.Equal(2, listed.Count);
        Assert.Equal($"ecs: {EcsInspector.Name(player)}, 2 component(s)", shown[0]);
        Assert.Contains(shown, line => line.Contains("Feet=(0, 7, 0)", StringComparison.Ordinal));
        Assert.Equal(["ecs: no living entity 999."], missing);
    }

    /// <summary>Runs a line and returns the console lines it added, both columns of each joined.</summary>
    private static List<string> Run(DevConsole console, string line)
    {
        var before = console.ScrollbackCount;
        console.Submit(line);
        console.RunPending();
        var lines = new List<string>();
        for (var i = before; i < console.ScrollbackCount; i++)
        {
            var row = console.ScrollbackLine(i);
            if (row.Kind is ConsoleLineKind.Answer or ConsoleLineKind.Failure)
            {
                lines.Add(row.Right.Length > 0 ? row.Text + " " + row.Right : row.Text);
            }
        }

        return lines;
    }
}
