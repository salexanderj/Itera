using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Networking;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Centres voxel streaming on the local player's eye (fixed group, last), so the voxels follow the player even while
/// no frames are drawn. While the player flies the debug camera, or when there is no player, it follows the
/// <see cref="ActiveCamera"/> instead, a debug case: a frame system moves that camera. A session host also streams a
/// level-0 disc of <see cref="VoxelWorld.RemotePlayerRadiusMetres"/> (<c>Engine:Voxels:RemotePlayerRadiusMetres</c>)
/// around each remote player, whom it moves from their commands, for their moves, stand-ins, and feet. The world's owner
/// (offline or a host) streams a level-0 disc of the same radius around each <see cref="StreamedCharacter"/> that no
/// player's level-0 disc holds (at most <see cref="MaxCharacterCentres"/>), so a character a game keeps moving far from
/// every player is not left waiting for its voxels. It also streams a level-0 disc around each loose body
/// that no player's level-0 disc holds (at most <see cref="MaxBodyCentres"/>): a body needs the level-0 voxels around it
/// to move (<c>VoxelColliders</c> freezes it in the air where they are not loaded), so a throw that flies past the
/// player's level 0, or a client's throw far from the host, would stop. The disc is <see cref="BodyRadiusMetres"/> about
/// the body, stretched along its velocity to hold where it will be <see cref="BodyLeadSeconds"/> ahead, so the columns
/// load before a fast body reaches them, and wider by the widest voxel object kind's reach where the world places objects
/// (<see cref="VoxelWorld.ObjectReachMetres"/>), so the areas of objects that may touch it are filled (a body waits
/// frozen until they are). A body's disc streams the levels up to the one it collides with (a level's disc
/// is twice as wide as the one below it), unless a player's disc holds it there: level 0 on a 10 cm grid; on a 1 cm grid a
/// giant's body collides with level 2 (<c>VoxelColliders.CoarseLevelOf</c>).
/// All centres go in one call per step, since each call that changes them rebuilds every level's wanted
/// columns. The only caller of <see cref="VoxelWorld.SetStreamingCentres"/>.
/// Reads <see cref="PlayerMode"/>, <see cref="CharacterPosition"/>, <see cref="CharacterSize"/>,
/// <see cref="RemotePlayer"/>, <see cref="StreamedCharacter"/>, <see cref="LooseBody"/>, <see cref="BodyPose"/>, and
/// <see cref="CameraPose"/>; writes no components.
/// </summary>
public sealed class StreamingCentreSystem : BaseSystem<World, FixedStepTime>, IFixedUpdateSystem
{
    private static readonly QueryDescription s_player = new QueryDescription().WithAll<
        PlayerMode,
        CharacterPosition,
        CharacterSize,
        LocalPlayer
    >();

    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    private static readonly QueryDescription s_remote = new QueryDescription().WithAll<
        RemotePlayer,
        CharacterPosition
    >();

    /// <summary>The level-0 radius a host streams around a loose body away from every player, in metres.</summary>
    public const double BodyRadiusMetres = 3;

    /// <summary>How far ahead of a moving body its disc reaches, in seconds of its speed.</summary>
    public const float BodyLeadSeconds = 0.5f;

    /// <summary>The largest radius of a body's disc, in metres, however fast the body moves.</summary>
    public const double MaxBodyRadiusMetres = 12;

    /// <summary>The most loose bodies a host streams around at once; others freeze where no centre reaches them.</summary>
    public const int MaxBodyCentres = 32;

    /// <summary>The most streamed characters the world's owner streams around at once; others wait where no centre reaches them.</summary>
    public const int MaxCharacterCentres = 32;

    private static readonly QueryDescription s_streamed = new QueryDescription().WithAll<
        StreamedCharacter,
        CharacterPosition
    >();

    private static readonly QueryDescription s_bodies = new QueryDescription().WithAll<LooseBody, BodyPose>();

    private readonly VoxelWorld _voxels;
    private readonly LooseBodies? _looseBodies;
    private readonly bool _host;
    private readonly bool _bodies;
    private StreamingCentre[] _centres = new StreamingCentre[8];

    /// <param name="world">The ECS world.</param>
    /// <param name="voxels">The voxels to stream.</param>
    /// <param name="role">
    /// The session role: a host streams around remote players, and a host or an offline game around loose bodies (a
    /// client's are kinematic mirrors of the host's, which need no voxels).
    /// </param>
    /// <param name="looseBodies">The loose bodies, for which level each collides with; none streams level 0 for all.</param>
    public StreamingCentreSystem(
        World world,
        VoxelWorld voxels,
        SessionRole role = SessionRole.Offline,
        LooseBodies? looseBodies = null
    )
        : base(world)
    {
        _voxels = voxels;
        _looseBodies = looseBodies;
        _host = role == SessionRole.Host;
        _bodies = role != SessionRole.Client;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<PlayerMode, CharacterPosition, CharacterSize>(in s_player);
        EcsChecks.RequireAll<CameraPose>(in s_camera);
        EcsChecks.RequireAll<RemotePlayer, CharacterPosition>(in s_remote);
        EcsChecks.RequireAll<LooseBody, BodyPose>(in s_bodies);
        EcsChecks.RequireAll<StreamedCharacter, CharacterPosition>(in s_streamed);
    }

    public override void Update(in FixedStepTime step)
    {
        var player = new FindEye();
        World.InlineQuery<FindEye, PlayerMode, CharacterPosition, CharacterSize>(in s_player, ref player);
        Vector3 centre;
        if (player.Found && !player.Flying)
        {
            centre = player.Eye;
        }
        else
        {
            var camera = new FindCamera();
            World.InlineQuery<FindCamera, CameraPose>(in s_camera, ref camera);
            if (!camera.Found)
            {
                return;
            }

            centre = camera.Position;
        }

        var count = 0;
        _centres[count++] = new StreamingCentre(centre, _voxels.RadiusMetres(0), _voxels.Levels, View: true);
        if (_host)
        {
            var remote = new CollectRemote
            {
                Centres = _centres,
                Count = count,
                RadiusMetres = _voxels.RemotePlayerRadiusMetres,
            };
            World.InlineQuery<CollectRemote, CharacterPosition>(in s_remote, ref remote);
            _centres = remote.Centres;
            count = remote.Count;
        }

        if (_bodies)
        {
            var characters = new CollectCharacters
            {
                Centres = _centres,
                Count = count,
                Players = count,
                RadiusMetres = _voxels.RemotePlayerRadiusMetres,
                Voxels = _voxels,
            };
            World.InlineQuery<CollectCharacters, CharacterPosition>(in s_streamed, ref characters);
            _centres = characters.Centres;
            count = characters.Count;
            var bodies = new CollectBodies
            {
                Centres = _centres,
                Count = count,
                Players = count,
                Seconds = step.StepSeconds,
                Bodies = _looseBodies,
                Voxels = _voxels,
            };
            World.InlineQuery<CollectBodies, LooseBody, BodyPose>(in s_bodies, ref bodies);
            _centres = bodies.Centres;
            count = bodies.Count;
        }

        _voxels.SetStreamingCentres(_centres.AsSpan(0, count));
    }

    /// <summary>
    /// Adds a centre for each loose body that none of the players' centres (the first <see cref="Players"/>)
    /// holds with a margin of its own radius, up to <see cref="MaxBodyCentres"/>: halfway along where it moves in
    /// <see cref="BodyLeadSeconds"/> (at its speed over the last step), wide enough for that path and
    /// <see cref="BodyRadiusMetres"/> around both ends, rounded up to a whole metre; with levels up to the one the body collides with.
    /// </summary>
    private struct CollectBodies : IForEach<LooseBody, BodyPose>
    {
        public StreamingCentre[] Centres;
        public int Count;
        public int Players;
        public float Seconds;
        public LooseBodies? Bodies;
        public VoxelWorld Voxels;

        public void Update(ref LooseBody body, ref BodyPose pose)
        {
            if (Count - Players >= MaxBodyCentres)
            {
                return;
            }

            // Across the ground only: discs hold whole columns. The radius grows in whole metres, since a centre whose
            // radius changes rebuilds every level's wanted columns.
            var moved = pose.Position - pose.PreviousPosition;
            var ahead = new Vector3(moved.X, 0, moved.Z) * (BodyLeadSeconds / Seconds);
            // Wider by the objects' reach, so the areas of objects that may touch it are known (VoxelColliders).
            var radius =
                Math.Min(BodyRadiusMetres + Math.Ceiling(0.5 * ahead.Length()), MaxBodyRadiusMetres)
                + Math.Ceiling(Voxels.ObjectReachMetres);
            var middle = pose.Position + 0.5f * ahead;
            // The levels 0 to the one the body collides with, each disc twice as wide as the one below.
            var levels = (Bodies?.CollisionLevel(body.Body) ?? 0) + 1;
            var widen = 1 << (levels - 1);
            for (var i = 0; i < Players; i++)
            {
                ref readonly var centre = ref Centres[i];
                if (centre.Levels < levels)
                {
                    continue;
                }

                // The players' disc at the body's level, which is the view's own radius there for the view.
                var inside = Voxels.RadiusMetres(in centre, levels - 1) - radius * widen;
                var dx = middle.X - centre.Position.X;
                var dz = middle.Z - centre.Position.Z;
                if (inside > 0 && dx * dx + dz * dz <= inside * inside)
                {
                    return;
                }
            }

            if (Count == Centres.Length)
            {
                Array.Resize(ref Centres, Centres.Length * 2);
            }

            Centres[Count++] = new StreamingCentre(middle, radius, levels);
        }
    }

    /// <summary>
    /// Adds a level-0 centre at each streamed character's feet that none of the players' centres (the first
    /// <see cref="Players"/>) holds with a margin of its own radius, up to <see cref="MaxCharacterCentres"/>, growing the
    /// array when full.
    /// </summary>
    private struct CollectCharacters : IForEach<CharacterPosition>
    {
        public StreamingCentre[] Centres;
        public int Count;
        public int Players;
        public double RadiusMetres;
        public VoxelWorld Voxels;

        public void Update(ref CharacterPosition position)
        {
            if (Count - Players >= MaxCharacterCentres)
            {
                return;
            }

            for (var i = 0; i < Players; i++)
            {
                ref readonly var centre = ref Centres[i];
                var inside = Voxels.RadiusMetres(in centre, 0) - RadiusMetres;
                var dx = position.Feet.X - centre.Position.X;
                var dz = position.Feet.Z - centre.Position.Z;
                if (inside > 0 && dx * dx + dz * dz <= inside * inside)
                {
                    return;
                }
            }

            if (Count == Centres.Length)
            {
                Array.Resize(ref Centres, Centres.Length * 2);
            }

            Centres[Count++] = new StreamingCentre(position.Feet, RadiusMetres, 1);
        }
    }

    /// <summary>Adds a level-0 centre at each remote player's feet, growing the array when full.</summary>
    private struct CollectRemote : IForEach<CharacterPosition>
    {
        public StreamingCentre[] Centres;
        public int Count;
        public double RadiusMetres;

        public void Update(ref CharacterPosition position)
        {
            if (Count == Centres.Length)
            {
                Array.Resize(ref Centres, Centres.Length * 2);
            }

            Centres[Count++] = new StreamingCentre(position.Feet, RadiusMetres, 1);
        }
    }

    private struct FindEye : IForEach<PlayerMode, CharacterPosition, CharacterSize>
    {
        public bool Found;
        public bool Flying;
        public Vector3 Eye;

        public void Update(ref PlayerMode mode, ref CharacterPosition position, ref CharacterSize size)
        {
            Found = true;
            Flying = mode.Flying;
            Eye = position.Feet + new Vector3(0, size.EyeMetres, 0);
        }
    }

    private struct FindCamera : IForEach<CameraPose>
    {
        public Vector3 Position;
        public bool Found;

        public void Update(ref CameraPose pose)
        {
            Position = pose.Position;
            Found = true;
        }
    }
}
