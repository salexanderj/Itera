using System.Text.Json;
using System.Text.RegularExpressions;

namespace Itera.Modding;

/// <summary>Reads and validates <see cref="ModManifest"/> files.</summary>
public static partial class ModManifestReader
{
    /// <summary>File name the loader looks for in each mod folder.</summary>
    public const string FileName = "mod.json";

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Indented camelCase, the form every manifest uses.</summary>
    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Parses manifest JSON. Throws <see cref="JsonException"/> on malformed input or missing required fields.</summary>
    public static ModManifest Parse(string json)
    {
        return JsonSerializer.Deserialize<ModManifest>(json, s_readOptions)
            ?? throw new JsonException("Manifest JSON is null.");
    }

    /// <summary>Reads and parses a manifest file.</summary>
    public static ModManifest ReadFile(string path)
    {
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Serialises a manifest to indented, camelCase JSON.</summary>
    public static string Write(ModManifest manifest)
    {
        return JsonSerializer.Serialize(manifest, s_writeOptions);
    }

    /// <summary>
    /// Checks a manifest against the rules the loader enforces. Returns an empty list when the manifest is valid.
    /// </summary>
    /// <param name="manifest">Manifest to check.</param>
    /// <param name="modDirectory">Folder the manifest was read from. Used to check the entry assembly location.</param>
    public static IReadOnlyList<string> Validate(ModManifest manifest, string modDirectory)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            errors.Add("Id is required.");
        }
        else if (!IdPattern().IsMatch(manifest.Id))
        {
            errors.Add("Id may only contain lower-case letters, digits, '.', '_' and '-'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add("Name is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add("Version is required.");
        }

        if (manifest.ApiVersion == 0)
        {
            errors.Add($"ApiVersion is required and must equal the host contract version {ModdingApi.Version}.");
        }
        else if (manifest.ApiVersion != ModdingApi.Version)
        {
            errors.Add(
                $"ApiVersion {manifest.ApiVersion} does not match the host contract version {ModdingApi.Version}."
            );
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            errors.Add("EntryAssembly is required.");
        }
        else
        {
            var fullDirectory = Path.GetFullPath(modDirectory);
            var entryPath = Path.GetFullPath(Path.Combine(fullDirectory, manifest.EntryAssembly));
            var directoryWithSeparator = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? fullDirectory
                : fullDirectory + Path.DirectorySeparatorChar;

            if (!entryPath.StartsWith(directoryWithSeparator, StringComparison.Ordinal))
            {
                errors.Add("EntryAssembly must be inside the mod folder.");
            }
            else if (!File.Exists(entryPath))
            {
                errors.Add($"EntryAssembly '{manifest.EntryAssembly}' was not found in the mod folder.");
            }
        }

        return errors;
    }

    /// <summary>Resolves the absolute path of the entry assembly.</summary>
    public static string GetEntryAssemblyPath(ModManifest manifest, string modDirectory)
    {
        return Path.GetFullPath(Path.Combine(modDirectory, manifest.EntryAssembly));
    }

    [GeneratedRegex("^[a-z0-9_.-]+$")]
    private static partial Regex IdPattern();
}
