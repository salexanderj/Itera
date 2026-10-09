using System.Runtime.Loader;

namespace Itera.Engine.Modding;

/// <summary>
/// Simple names of assemblies that mods must resolve from the host instead of their own folder,
/// so the host and every mod share one copy of each type.
/// </summary>
internal sealed class HostSharedAssemblies
{
    private readonly HashSet<string> _names;

    public HostSharedAssemblies(IEnumerable<string> simpleNames)
    {
        _names = new HashSet<string>(simpleNames, StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _names.Count;

    /// <summary>
    /// Builds the set from the trusted platform assemblies (framework plus everything in the host's output folder)
    /// and from assemblies already loaded into the default load context.
    /// </summary>
    public static HostSharedAssemblies FromHost()
    {
        var names = new List<string>();

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                names.Add(Path.GetFileNameWithoutExtension(path));
            }
        }

        foreach (var assembly in AssemblyLoadContext.Default.Assemblies)
        {
            var name = assembly.GetName().Name;
            if (name is not null)
            {
                names.Add(name);
            }
        }

        return new HostSharedAssemblies(names);
    }

    public bool Contains(string simpleName)
    {
        return _names.Contains(simpleName);
    }
}
