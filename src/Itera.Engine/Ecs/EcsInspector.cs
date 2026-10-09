using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Arch.Core;
using Itera.Engine.Components;

namespace Itera.Engine.Ecs;

/// <summary>
/// Reads a world's entities and their components for people and tools (the console's <c>ecs</c>, the control port's
/// <c>query</c>): the archetypes and their counts, the entities that have a component, one entity's components, and each
/// component's public fields as text or JSON. It reads by reflection and boxes every value, so it allocates: commands
/// and requests only, never a system's update. Main thread.
/// </summary>
public static class EcsInspector
{
    /// <summary>Most entities one listing returns.</summary>
    public const int MostListed = 1000;

    /// <summary>Each archetype with entities, most entities first: its count and its components' names.</summary>
    public static List<(int Count, string Components)> Archetypes(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var rows = new List<(int, string)>();
        foreach (var archetype in world.Archetypes.AsSpan())
        {
            if (archetype.EntityCount > 0)
            {
                rows.Add((archetype.EntityCount, string.Join(", ", Names(archetype))));
            }
        }

        rows.Sort((a, b) => b.Item1 != a.Item1 ? b.Item1.CompareTo(a.Item1) : string.CompareOrdinal(a.Item2, b.Item2));
        return rows;
    }

    /// <summary>
    /// Finds a component type that some entity of <paramref name="world"/> has, by its name without its namespace (any
    /// case).
    /// </summary>
    public static bool TryFindComponent(World world, string name, out ComponentType type)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(name);
        foreach (var archetype in world.Archetypes.AsSpan())
        {
            // Arch keeps an archetype after its last entity goes.
            if (archetype.EntityCount == 0)
            {
                continue;
            }

            foreach (var component in archetype.Signature.Components)
            {
                if (string.Equals(component.Type.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    type = component;
                    return true;
                }
            }
        }

        type = default;
        return false;
    }

    /// <summary>Up to <paramref name="most"/> entities that have <paramref name="type"/>, by id.</summary>
    public static List<Entity> EntitiesWith(World world, ComponentType type, int most = MostListed)
    {
        ArgumentNullException.ThrowIfNull(world);
        var entities = new List<Entity>();
        foreach (var archetype in world.Archetypes.AsSpan())
        {
            if (archetype.EntityCount > 0 && archetype.Has(type))
            {
                AddEntities(archetype, entities);
            }
        }

        entities.Sort((a, b) => a.Id.CompareTo(b.Id));
        if (entities.Count > most)
        {
            entities.RemoveRange(most, entities.Count - most);
        }

        return entities;
    }

    /// <summary>
    /// Finds a living entity by its text: <c>ID</c> or <c>ID:VERSION</c>, as <see cref="Name"/> writes it. Matched over
    /// every entity, never made from the numbers, so a stale id finds nothing.
    /// </summary>
    public static bool TryFindEntity(World world, string text, out Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(text);
        entity = Entity.Null;
        var parts = text.Split(':');
        if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return false;
        }

        var version = -1;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out version))
        {
            return false;
        }

        foreach (var archetype in world.Archetypes.AsSpan())
        {
            if (archetype.EntityCount == 0)
            {
                continue;
            }

            var entities = new List<Entity>();
            AddEntities(archetype, entities);
            foreach (var candidate in entities)
            {
                if (candidate.Id == id && (version < 0 || candidate.Version == version))
                {
                    entity = candidate;
                    return world.IsAlive(candidate);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Finds an entity by its text (<see cref="TryFindEntity"/>), or the local player's (the entity with
    /// <see cref="LocalPlayer"/>) for <c>player</c>.
    /// </summary>
    public static bool TryFindEntityOrPlayer(World world, string text, out Entity entity)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!string.Equals(text, "player", StringComparison.OrdinalIgnoreCase))
        {
            return TryFindEntity(world, text, out entity);
        }

        entity = Entity.Null;
        if (!TryFindComponent(world, nameof(LocalPlayer), out var local))
        {
            return false;
        }

        var players = EntitiesWith(world, local, 1);
        if (players.Count == 0)
        {
            return false;
        }

        entity = players[0];
        return true;
    }

    /// <summary>An entity as the inspector writes it: <c>ID:VERSION</c>.</summary>
    public static string Name(Entity entity) =>
        entity.Id.ToString(CultureInfo.InvariantCulture) + ":" + entity.Version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Every component of <paramref name="entity"/>, by name, its value boxed.</summary>
    public static List<(string Name, object? Value)> Components(World world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        var components = new List<(string, object?)>();
        if (!world.IsAlive(entity))
        {
            return components;
        }

        ComponentType[] types = world.GetSignature(entity);
        var values = world.GetAllComponents(entity);
        for (var i = 0; i < types.Length && i < values.Length; i++)
        {
            components.Add((types[i].Type.Name, values[i]));
        }

        components.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return components;
    }

    /// <summary>One component of <paramref name="entity"/>, boxed; null when it has none of that type.</summary>
    public static object? Component(World world, Entity entity, ComponentType type)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.IsAlive(entity) && world.Has(entity, type) ? world.Get(entity, type) : null;
    }

    /// <summary>
    /// A value as text: numbers with up to three decimals, vectors as <c>(x, y, z)</c>, a struct's public fields as
    /// <c>{Name=value, ...}</c> (a tag as <c>{}</c>).
    /// </summary>
    public static string Describe(object? value)
    {
        var text = new StringBuilder();
        Describe(value, text, 0);
        return text.ToString();
    }

    /// <summary>Writes a value as JSON: numbers, vectors as arrays, enums as their names, structs as objects of their fields.</summary>
    public static void WriteJson(Utf8JsonWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteJson(writer, value, 0);
    }

    /// <summary>Deepest a struct's fields are followed into; deeper ones are written as their type's name.</summary>
    private const int MostDepth = 4;

    private static List<string> Names(Archetype archetype)
    {
        var names = new List<string>();
        foreach (var component in archetype.Signature.Components)
        {
            names.Add(component.Type.Name);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static void AddEntities(Archetype archetype, List<Entity> entities)
    {
        for (var c = 0; c < archetype.ChunkCount; c++)
        {
            ref var chunk = ref archetype.GetChunk(c);
            for (var i = 0; i < chunk.Count; i++)
            {
                entities.Add(chunk.Entities[i]);
            }
        }
    }

    private static void Describe(object? value, StringBuilder text, int depth)
    {
        switch (value)
        {
            case null:
                text.Append("null");
                return;
            case float f:
                text.Append(f.ToString("0.###", CultureInfo.InvariantCulture));
                return;
            case double d:
                text.Append(d.ToString("0.###", CultureInfo.InvariantCulture));
                return;
            case Vector2 v2:
                text.Append(CultureInfo.InvariantCulture, $"({Number(v2.X)}, {Number(v2.Y)})");
                return;
            case Vector3 v3:
                text.Append(CultureInfo.InvariantCulture, $"({Number(v3.X)}, {Number(v3.Y)}, {Number(v3.Z)})");
                return;
            case Vector4 v4:
                text.Append(
                    CultureInfo.InvariantCulture,
                    $"({Number(v4.X)}, {Number(v4.Y)}, {Number(v4.Z)}, {Number(v4.W)})"
                );
                return;
            case Quaternion q:
                text.Append(
                    CultureInfo.InvariantCulture,
                    $"({Number(q.X)}, {Number(q.Y)}, {Number(q.Z)}, {Number(q.W)})"
                );
                return;
            case Entity entity:
                text.Append(Name(entity));
                return;
            case bool b:
                text.Append(b ? "true" : "false");
                return;
            case string s:
                text.Append(s);
                return;
            case Enum e:
                text.Append(e.ToString());
                return;
            case IFormattable formattable when value.GetType().IsPrimitive:
                text.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
        }

        var type = value.GetType();
        if (!type.IsValueType || depth >= MostDepth)
        {
            text.Append(type.Name);
            return;
        }

        text.Append('{');
        var first = true;
        foreach (var field in Fields(type))
        {
            if (!first)
            {
                text.Append(", ");
            }

            first = false;
            text.Append(field.Name).Append('=');
            Describe(field.GetValue(value), text, depth + 1);
        }

        text.Append('}');
    }

    private static void WriteJson(Utf8JsonWriter writer, object? value, int depth)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case float f:
                WriteNumber(writer, f);
                return;
            case double d:
                WriteNumber(writer, d);
                return;
            case Vector2 v2:
                WriteNumbers(writer, v2.X, v2.Y);
                return;
            case Vector3 v3:
                WriteNumbers(writer, v3.X, v3.Y, v3.Z);
                return;
            case Vector4 v4:
                WriteNumbers(writer, v4.X, v4.Y, v4.Z, v4.W);
                return;
            case Quaternion q:
                WriteNumbers(writer, q.X, q.Y, q.Z, q.W);
                return;
            case Entity entity:
                writer.WriteStringValue(Name(entity));
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case Enum e:
                writer.WriteStringValue(e.ToString());
                return;
            case byte or sbyte or short or ushort or int:
                writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                return;
            case uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            case ulong u:
                writer.WriteNumberValue(u);
                return;
        }

        var type = value.GetType();
        if (!type.IsValueType || depth >= MostDepth)
        {
            writer.WriteStringValue(type.Name);
            return;
        }

        writer.WriteStartObject();
        foreach (var field in Fields(type))
        {
            writer.WritePropertyName(field.Name);
            WriteJson(writer, field.GetValue(value), depth + 1);
        }

        writer.WriteEndObject();
    }

    /// <summary>JSON has no NaN or infinity: those are written as strings.</summary>
    private static void WriteNumber(Utf8JsonWriter writer, double value)
    {
        if (double.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else
        {
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A float as its shortest text (6.037886, not 6.0378861427307129); NaN and infinity as strings.</summary>
    private static void WriteNumber(Utf8JsonWriter writer, float value)
    {
        if (float.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else
        {
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void WriteNumbers(Utf8JsonWriter writer, params ReadOnlySpan<float> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            WriteNumber(writer, value);
        }

        writer.WriteEndArray();
    }

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static FieldInfo[] Fields(Type type) => type.GetFields(BindingFlags.Public | BindingFlags.Instance);
}
