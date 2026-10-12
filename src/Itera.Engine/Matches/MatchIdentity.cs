using System.Globalization;
using Itera.Engine.Objects;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Matches;

/// <summary>
/// What a match is, as two games must agree on before one joins the other: an id and a version, sent in the hello and
/// the welcome and in a hosting lobby's match key (<c>SessionProtocol.MatchKey</c>), beside the protocol and the step
/// rate. With a voxel world it is its generator's (<see cref="GeneratorMatchIdentity"/>: another generator, or another
/// version of one, builds another world from the seed); without one, the application's name at version 1
/// (<see cref="ApplicationMatchIdentity"/>). A game registers its own to tell its builds apart some other way.
/// </summary>
public interface IMatchIdentity
{
    /// <summary>The id: a generator's (<c>game.world</c>), or the application's name.</summary>
    string Id { get; }

    /// <summary>Its version: games of other versions do not join each other.</summary>
    int Version { get; }
}

/// <summary>
/// A voxel world's match identity: its generator's id and version, and when it places objects, the fingerprint of the
/// run's object kinds (<see cref="VoxelObjectKinds.Fingerprint"/>) after its id: games whose kinds differ build other
/// objects from the same seed.
/// </summary>
public sealed class GeneratorMatchIdentity(IChunkGenerator generator, VoxelObjectKinds? kinds = null) : IMatchIdentity
{
    public string Id =>
        generator.Objects is not null && kinds is { Count: > 0 }
            ? string.Create(CultureInfo.InvariantCulture, $"{generator.Id}+objects-{kinds.Fingerprint:x16}")
            : generator.Id;

    public int Version => generator.Version;
}

/// <summary>The match identity of a game without a voxel world: its name (<c>Engine:Application:Name</c>) at version 1.</summary>
public sealed class ApplicationMatchIdentity(IOptions<EngineOptions> options) : IMatchIdentity
{
    public string Id => options.Value.Application.Name;

    public int Version => 1;
}
