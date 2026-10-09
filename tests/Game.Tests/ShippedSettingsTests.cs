using System.Globalization;
using System.Reflection;
using Itera.Engine;
using Microsoft.Extensions.Configuration;

namespace Game.Tests;

/// <summary>
/// The shipped appsettings.json lists options with their values for reference; each must equal the default its options
/// class gives in code, so the two cannot drift apart unseen. The few values the game sets on purpose are listed here.
/// </summary>
public sealed class ShippedSettingsTests
{
    /// <summary>Values the game gives differently from the engine's defaults, on purpose.</summary>
    private static readonly HashSet<string> s_setOnPurpose = new(StringComparer.OrdinalIgnoreCase)
    {
        "Engine:Application:Name",
        "Engine:Application:DataFolder",
        "Engine:Window:Title",
    };

    [Fact]
    public void Every_listed_value_is_the_code_default()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();
        var roots = new (string Section, object Defaults)[]
        {
            ("Engine", new EngineOptions()),
            (WorldOptions.SectionName, new WorldOptions()),
            (PlayerOptions.SectionName, new PlayerOptions()),
        };

        var differences = new List<string>();
        var compared = 0;
        foreach (var (section, defaults) in roots)
        {
            foreach (var (key, value) in configuration.GetSection(section).AsEnumerable(makePathsRelative: true))
            {
                var path = section + ":" + key;
                if (s_setOnPurpose.Contains(path) || !TryDefault(defaults, key, out var expected))
                {
                    continue;
                }

                compared++;
                if (!string.Equals(Text(expected), value ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    differences.Add($"{path} is {value ?? "null"} in appsettings.json and {Text(expected)} in code");
                }
            }
        }

        Assert.Empty(differences);
        Assert.True(compared > 30, $"Only {compared} values were compared.");
    }

    /// <summary>
    /// Every option the engine and the game have is in the file, so a clone sees each in one place with its default, and
    /// a new option's default cannot be forgotten.
    /// </summary>
    [Fact]
    public void Every_option_is_listed()
    {
        // Read as JSON: the configuration drops keys whose value is null.
        using var file = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
        );
        var missing = new List<string>();
        foreach (
            var (section, defaults) in new (string, object)[]
            {
                ("Engine", new EngineOptions()),
                (WorldOptions.SectionName, new WorldOptions()),
                (PlayerOptions.SectionName, new PlayerOptions()),
            }
        )
        {
            foreach (var key in OptionKeys(section, defaults.GetType()))
            {
                if (!Listed(file.RootElement, key))
                {
                    missing.Add(key);
                }
            }
        }

        Assert.True(missing.Count == 0, "Not in appsettings.json: " + string.Join(", ", missing));
    }

    /// <summary>True when the JSON has <paramref name="key"/> (a ':' path, any case), whatever its value, null too.</summary>
    private static bool Listed(System.Text.Json.JsonElement element, string key)
    {
        foreach (var name in key.Split(':'))
        {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            var found = false;
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    element = property.Value;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every option's key under <paramref name="prefix"/>: a value's, or a settings class's options in turn.</summary>
    private static IEnumerable<string> OptionKeys(string prefix, Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            var key = prefix + ":" + property.Name;
            var kind = property.PropertyType;
            if (kind == typeof(string) || kind.IsValueType)
            {
                yield return key;
            }
            else
            {
                foreach (var inner in OptionKeys(key, kind))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>The default at <paramref name="key"/> (a ':' path of properties); false for a section, not a value.</summary>
    private static bool TryDefault(object root, string key, out object? value)
    {
        value = root;
        foreach (var name in key.Split(':'))
        {
            var property = value
                ?.GetType()
                .GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
            {
                value = null;
                return false;
            }

            value = property.GetValue(value);
        }

        // A section (a settings object) is not a value; strings, numbers, enums, and nulls are.
        return value is null or string or ValueType;
    }

    private static string Text(object? value) =>
        value switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
}
