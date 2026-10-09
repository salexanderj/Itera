using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Itera.Engine;

/// <summary>
/// Refuses configuration keys in an options section that no property of the options takes, naming each by its full
/// path: the configuration binder ignores them, so a misspelt key (<c>--Engine:Renderer:VSync</c> for
/// <c>Engine:Window:VSync</c>) would otherwise do nothing with no message (the run stops and names the
/// key). Keys match properties as the binder matches them, ignoring case; the settings classes a section holds are
/// walked in turn. Read once at start, like the other checks.
/// </summary>
public sealed class UnknownOptionKeys<TOptions> : IValidateOptions<TOptions>
    where TOptions : class
{
    private readonly IConfigurationSection _section;

    /// <param name="section">The section <typeparamref name="TOptions"/> binds from.</param>
    public UnknownOptionKeys(IConfigurationSection section)
    {
        _section = section;
    }

    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        var unknown = new List<string>();
        UnknownOptionKeys.Collect(_section, typeof(TOptions), unknown);
        return unknown.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(unknown);
    }
}

/// <summary>The walk behind <see cref="UnknownOptionKeys{TOptions}"/>.</summary>
public static class UnknownOptionKeys
{
    /// <summary>
    /// Adds a message to <paramref name="unknown"/> for each key under <paramref name="section"/> that no public
    /// instance property of <paramref name="type"/> takes, and walks the properties that hold settings classes.
    /// </summary>
    public static void Collect(IConfigurationSection section, Type type, List<string> unknown)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(unknown);
        foreach (var child in section.GetChildren())
        {
            var property = type.GetProperty(
                child.Key,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
            );
            if (property is null)
            {
                unknown.Add(
                    string.Create(CultureInfo.InvariantCulture, $"{child.Path} is not an option; check its spelling.")
                );
                continue;
            }

            var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (IsSettings(valueType))
            {
                Collect(child, valueType, unknown);
            }
        }
    }

    /// <summary>A class of settings, as opposed to a value, a string, or a collection the binder fills.</summary>
    private static bool IsSettings(Type type) =>
        type.IsClass && type != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
}
