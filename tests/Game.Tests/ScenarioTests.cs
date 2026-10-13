using System.Numerics;
using Arch.Core;
using Game.Components;
using Game.Systems;
using Game.WorldGeneration;
using Itera.Engine.Assets;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Rendering;
using Itera.Engine.Voxels;
using Microsoft.Extensions.DependencyInjection;
using Silk.NET.Input;

namespace Game.Tests;

/// <summary>
/// The starter played without a window (<see cref="HeadlessMatch"/>): its real world, player, and systems, stepped for
/// seconds of play and read through the ECS world. One physics thread, so the steps repeat.
/// </summary>
public sealed class ScenarioTests
{
    private static readonly Dictionary<string, string?> s_options = new()
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
        ["Engine:Voxels:WorkerThreads"] = "4",
    };

    private static readonly QueryDescription s_player = new QueryDescription().WithAll<
        LocalPlayer,
        CharacterPosition,
        CharacterContact
    >();

    private static readonly QueryDescription s_balls = new QueryDescription().WithAll<LooseBody>();

    private static readonly QueryDescription s_fox = new QueryDescription().WithAll<
        FoxRoute,
        MeshPlacement,
        MeshAnimator
    >();

    [Fact]
    public void The_player_stands_on_the_starters_ground()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);

        Assert.True(match.WaitUntilLoaded());
        match.Step(60);

        var (position, contact) = Player(match.World);
        var ground = GameGenerator.SurfaceHeight(0, 0, 1) / (double)VoxelConstants.CentimetresPerMetre;
        Assert.True(contact.Grounded);
        Assert.Equal(ground, Origin(match).ToWorld(position.Feet).Y, 0.15);
    }

    [Fact]
    public void The_fox_stands_in_front_of_the_start_and_is_drawn_every_frame()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(2);

        var assets = provider.GetRequiredService<MeshAssets>();
        var fox = Assert.Single(provider.GetRequiredService<MeshInstances>().Instances.ToArray());
        Assert.Equal(assets.Find(GameModels.Fox), fox.Asset);
        Assert.Equal(576, assets[fox.Asset].TriangleCount);
        // Posed by its rig's 24 joints, looking about.
        Assert.True(fox.Posed);
        Assert.Equal(24, fox.Joints);
        // The start pose looks along -z from over the middle.
        var at = Origin(match).ToWorld(Vector3.Transform(Vector3.Zero, fox.Transform));
        var ground = GameGenerator.SurfaceHeight(0, -400, 1) / (double)VoxelConstants.CentimetresPerMetre;
        Assert.Equal(0, at.X, 1e-4);
        Assert.Equal(ground, at.Y, 1e-4);
        Assert.Equal(-FoxSystem.AheadMetres, at.Z, 1e-4);
    }

    [Fact]
    public void The_foxs_clip_moves_on_with_the_steps_and_not_between_them()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(30);
        var before = Fox(match.World).Animator.Seconds;

        // Frames with no step between them, as while the match is paused: the clip stays.
        for (var i = 0; i < 30; i++)
        {
            match.Frame();
        }

        var held = Fox(match.World).Animator.Seconds;
        match.Step(60);
        var after = Fox(match.World).Animator.Seconds;

        Assert.Equal(before, held);
        var survey = provider.GetRequiredService<MeshAssets>()[0].Rig!.Clips[0];
        Assert.Equal("Survey", survey.Name);
        Assert.Equal((before + 1) % survey.Duration, after, 1e-3f);
    }

    [Fact]
    public void The_fox_walks_round_its_circle_after_looking_about()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var start = Fox(match.World).Placement.Position;

        // Two seconds into its walk.
        match.Step((int)((FoxSystem.SurveySeconds + 2) * 60));

        var (route, placement, animator) = Fox(match.World);
        var walk = provider.GetRequiredService<MeshAssets>()[0].Rig!.FindClip("Walk");
        Assert.Equal(walk, animator.Clip);
        var flat = new Vector2(placement.Position.X - route.Centre.X, placement.Position.Z - route.Centre.Z);
        Assert.Equal(FoxSystem.CircleMetres, flat.Length(), 1e-3f);
        Assert.InRange(Vector3.Distance(start, placement.Position), 1.5f, 2.5f);
        // On the ground: within a voxel of where a ray down finds it.
        var at = Origin(match).ToWorld(placement.Position);
        var ground = GameGenerator.SurfaceHeight((int)Math.Round(at.X * 100), (int)Math.Round(at.Z * 100), 1) / 100.0;
        Assert.Equal(ground, at.Y, 0.15);
    }

    [Fact]
    public void Holding_forward_for_a_second_walks_the_player_forward()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(30);
        var before = Player(match.World).Position.Feet;

        match.Input.InjectKey(Key.W, true);
        match.Step(60);
        match.Input.InjectKey(Key.W, false);

        var after = Player(match.World).Position.Feet;
        var walked = new Vector2(after.X - before.X, after.Z - before.Z);
        // The start pose looks along -z; walking is 4.5 m/s once up to speed.
        Assert.True(walked.Y < 0, $"walked {walked}");
        Assert.InRange(walked.Length(), 3f, MovementStats.Default.Walk + 0.01f);
    }

    [Fact]
    public void Balls_drop_rest_and_go_back_into_the_world()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(10);
        var edits = match.Match.Voxels.EditCount;
        var models = match.Match.Models.Count;
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var output = new Output();

        commands.Execute("balls 4", output);
        match.Step(10);
        var dropped = match.World.CountEntities(in s_balls);
        // Settled for 3 s, each ball is filled back into the grid as an edit.
        match.Step(60 * 10);

        Assert.Empty(output.Failures);
        Assert.Equal(4, dropped);
        Assert.Equal(0, match.World.CountEntities(in s_balls));
        Assert.Equal(edits + 4, match.Match.Voxels.EditCount);
        // Their models went too: nothing draws them until the world's meshes hold their voxels.
        Assert.Equal(models, match.Match.Models.Count);
    }

    /// <summary>
    /// The origin following the camera every 8 m (<c>Engine:Origin:ShiftMetres</c>, against 512, where the walk never
    /// moves it) changes nothing in the world: the fox on its circle and four balls that rest and are settled across
    /// the moves come out where they do in a match whose origin stays, and the player's walk within a millimetre (that
    /// match rounds the feet to floats 30 to 40 m from its origin, about a micrometre a step, which the followed one,
    /// its feet near its origin, does not: 0.3 mm over the walk when measured).
    /// </summary>
    [Fact]
    public void A_walk_with_the_origin_following_the_camera_keeps_to_one_without_a_move()
    {
        var still = WalkWithBalls(shiftMetres: 512);
        var followed = WalkWithBalls(shiftMetres: 8);

        Assert.Equal(0, still.Shifts);
        Assert.True(followed.Shifts >= 4, $"The origin moved {followed.Shifts} times.");
        for (var i = 0; i < still.Feet.Count; i++)
        {
            Assert.True(
                still.Feet[i].DistanceTo(followed.Feet[i]) < 1e-3,
                $"Block {i}: the feet at {followed.Feet[i]}, without shifts at {still.Feet[i]}."
            );
            Assert.True(
                still.Fox[i].DistanceTo(followed.Fox[i]) < 1e-4,
                $"Block {i}: the fox at {followed.Fox[i]}, without shifts at {still.Fox[i]}."
            );
        }

        Assert.Equal(4, still.Settled.Count);
        Assert.Equal(still.Settled, followed.Settled);
    }

    /// <summary>
    /// Drops four balls and lets them come to rest (a ball rolling across a move of the origin goes its own way by
    /// centimetres, as any small change sends it), then walks forward for 600 steps, reading the feet and the fox
    /// (absolute) every 100 steps, while the balls settle; the origin follows the camera every
    /// <paramref name="shiftMetres"/>.
    /// </summary>
    private static Walked WalkWithBalls(double shiftMetres)
    {
        var options = new Dictionary<string, string?>(s_options)
        {
            ["Engine:Origin:ShiftMetres"] = shiftMetres.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        using var provider = GameComposition.Build(options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var origin = match.Services.GetRequiredService<WorldOrigin>();
        var output = new Output();
        match.Step(30);
        var edits = match.Match.Voxels.EditCount;
        provider.GetRequiredService<ConsoleCommands>().Execute("balls 4", output);
        match.Step(150);

        var walked = new Walked([], [], [], 0);
        var moves = origin.Moves;
        match.Input.InjectKey(Key.W, true);
        for (var block = 0; block < 6; block++)
        {
            match.Step(100);
            walked.Feet.Add(origin.ToWorld(Player(match.World).Position.Feet));
            walked.Fox.Add(origin.ToWorld(Fox(match.World).Placement.Position));
        }

        match.Input.InjectKey(Key.W, false);
        walked = walked with { Shifts = (int)(origin.Moves - moves) };
        match.Step(60 * 5);
        for (var sequence = edits + 1; sequence <= match.Match.Voxels.EditCount; sequence++)
        {
            walked.Settled.Add(match.Match.Voxels.GetEdit(sequence).Origin);
        }

        Assert.Empty(output.Failures);
        return walked;
    }

    private sealed record Walked(List<WorldPoint> Feet, List<WorldPoint> Fox, List<VoxelCoord> Settled, int Shifts);

    /// <summary>
    /// A flight of 2.8 km across the play area at 500 m/s: <c>tp</c> 1.4 km out moves the origin there first, so the
    /// camera lands exactly; then the origin follows the camera (<c>OriginShiftSystem</c>) each time it is 512 m out, so
    /// it never gets much further (512 m and a frame's 8.3 m), and the camera ends where 336 frames of flight take it,
    /// within a centimetre (its floats near the origin round each frame's move by at most 31 micrometres).
    /// </summary>
    [Fact]
    public void A_flight_across_the_play_area_keeps_the_origin_near_the_camera()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var origin = Origin(match);
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var output = new Output();
        commands.Execute("fly on; flyspeed 500", output);
        match.Step(1);
        commands.Execute("tp -1400 80 0 -90 0", output);
        match.Step(1);

        Assert.Empty(output.Failures);
        Assert.Equal(new WorldPoint(-1_400, 80, 0), origin.Metres);
        var start = origin.ToWorld(Camera(match.World).Position);
        Assert.True(start.DistanceTo(new WorldPoint(-1_400, 80, 0)) < 1e-4, $"Landed at {start}.");

        var moves = origin.Moves;
        var farthest = 0f;
        match.Input.InjectKey(Key.W, true);
        for (var frame = 0; frame < 336; frame++)
        {
            match.Step(1);
            var local = Camera(match.World).Position;
            farthest = Math.Max(farthest, Math.Max(Math.Abs(local.X), Math.Max(Math.Abs(local.Y), Math.Abs(local.Z))));
        }

        match.Input.InjectKey(Key.W, false);
        var end = origin.ToWorld(Camera(match.World).Position);

        Assert.True(origin.Moves - moves >= 5, $"The origin moved {origin.Moves - moves} times.");
        Assert.True(farthest < 512 + 8.4f, $"The camera went {farthest} m from the origin.");
        var flown = 336 * (500.0 / 60);
        Assert.True(
            end.DistanceTo(new WorldPoint(-1_400 + flown, 80, 0)) < 0.01,
            $"The flight ended at {end.X:R},{end.Y:R},{end.Z:R}, not {-1_400 + flown:R},80,0."
        );
    }

    /// <summary>
    /// The map's edge at 4,006.4 m (313 columns of 12.8 m) is a wall: the player jumps to 4 km out (the origin with it),
    /// stands on the ground there once it has loaded, and walks along +x for 5 s without passing it.
    /// </summary>
    [Fact]
    public void The_maps_edge_4_km_out_is_a_wall()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var origin = Origin(match);
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var output = new Output();
        var eye = GameGenerator.SurfaceHeight(399_000, 0, 1) / 100 + CharacterSize.ForHeight(1.7f).EyeMetres + 0.5;
        // Looking along +x: a yaw of -90 degrees.
        commands.Execute(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tp 3990 {eye} 0 -90 0"),
            output
        );
        match.Step(1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(60);
        var stood = Player(match.World).Contact.Grounded;

        match.Input.InjectKey(Key.W, true);
        match.Step(300);
        match.Input.InjectKey(Key.W, false);
        var (position, contact) = Player(match.World);
        var feet = origin.ToWorld(position.Feet);

        Assert.Empty(output.Failures);
        Assert.Equal(3_990, origin.Metres.X);
        Assert.True(stood, "Not on the ground after the jump.");
        Assert.True(contact.Grounded, $"Not on the ground at {feet}.");
        Assert.InRange(feet.X, 4_000, 4_006.4);
    }

    /// <summary>
    /// The boulder nearest the start (seed 1): the player
    /// stands at its foot on its -x side looking at it, presses Space, and holds W until the climb ends, which leaves it
    /// standing on the boulder well over the ground.
    /// </summary>
    [Fact]
    public void Space_at_a_boulder_and_forward_climb_onto_it()
    {
        var (middle, foot, ground) = FindBoulder();
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var output = new Output();
        var eye = foot + new Vector3(0, CharacterSize.ForHeight(1.7f).EyeMetres, 0);
        // Looking along +x: a yaw of -90 degrees.
        commands.Execute(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tp {eye.X} {eye.Y} {eye.Z} -90 0"),
            output
        );
        match.Step(1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(60);

        match.Input.InjectKey(Key.Space, true);
        match.Step(1);
        match.Input.InjectKey(Key.Space, false);
        var grabbed = Climb(match.World).State == ClimbState.Climbing;
        match.Input.InjectKey(Key.W, true);
        var origin = Origin(match);
        var highest = 0.0;
        for (var step = 0; step < 600 && Climb(match.World).State != ClimbState.Free; step++)
        {
            match.Step(1);
            highest = Math.Max(highest, origin.ToWorld(Player(match.World).Position.Feet).Y);
        }

        match.Input.InjectKey(Key.W, false);
        match.Step(60);
        var (position, contact) = Player(match.World);
        var feet = origin.ToWorld(position.Feet);

        Assert.Empty(output.Failures);
        Assert.True(grabbed, $"No grab at {foot} by the boulder at {middle}.");
        Assert.True(highest - ground > 1, $"Climbed to {highest:F2} m from ground at {ground:F2} m.");
        Assert.True(contact.Grounded);
        Assert.True(feet.Y - ground > 1, $"Stands at {feet} by the boulder at {middle}.");
    }

    /// <summary>
    /// The broadleaf nearest the middle with nothing on the 3 m before it along -x (seed 1): the player walking at it
    /// along +x stops at its trunk; a clear through the trunk from 30 to 60 cm over its foot has the world's owner find
    /// its top cut off, log the top's removal, and drop it as a cut body.
    /// </summary>
    [Fact]
    public void A_tree_stops_the_player_and_falls_when_cut_through()
    {
        var tree = FindTree();
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var output = new Output();
        var anchor = tree.Anchor;
        var startX = anchor.X / 100.0 - 2.5;
        var ground = GameGenerator.Height(anchor.X - 250, anchor.Z, 1) / 100;
        var eye = ground + CharacterSize.ForHeight(1.7f).EyeMetres;
        // Looking along +x: a yaw of -90 degrees.
        commands.Execute(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"tp {startX} {eye} {anchor.Z / 100.0} -90 0"
            ),
            output
        );
        match.Step(1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(30);

        match.Input.InjectKey(Key.W, true);
        match.Step(90);
        match.Input.InjectKey(Key.W, false);
        match.Step(10);
        var feet = Origin(match).ToWorld(Player(match.World).Position.Feet);
        Assert.Empty(output.Failures);
        Assert.InRange(feet.X, startX + 1, anchor.X / 100.0 - 0.3);

        var voxels = match.Match.Voxels;
        var before = voxels.EditCount;
        voxels.Apply(
            VoxelEdit.Box(
                new VoxelCoord(anchor.X - 60, anchor.Y + 30, anchor.Z - 60),
                new VoxelCoord(120, 30, 120),
                VoxelEditOperation.Clear
            )
        );
        var fallen = match.Match.Terrain!.Fallen;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (fallen.Made == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Nothing fell.");
            match.Step(1);
            Thread.Sleep(1);
        }

        Assert.Equal(VoxelEditOperation.RemovePart, voxels.GetEdit(before + 2).Operation);
        Assert.True(match.World.CountEntities(new QueryDescription().WithAll<LooseBody, CutPiece>()) >= 1);
    }

    /// <summary>
    /// The broadleaf of seed 1 nearest the middle whose 3 m along -x hold no other object's trunk, stems, or rock.
    /// </summary>
    private static Itera.Engine.Objects.ObjectPlacement FindTree()
    {
        var found = new List<Itera.Engine.Objects.ObjectPlacement>();
        new GameForests().Find(1, -15_000, 15_000, -15_000, 15_000, found);
        static GameObjectKind KindOf(Itera.Engine.Objects.ObjectPlacement placement) =>
            (GameObjectKind)(placement.Kind.Value / GameObjects.Shapes);
        foreach (
            var tree in found
                .Where(p => KindOf(p) == GameObjectKind.Broadleaf)
                .OrderBy(p => (double)p.Anchor.X * p.Anchor.X + (double)p.Anchor.Z * p.Anchor.Z)
        )
        {
            var clear = found.TrueForAll(other =>
                other.Id == tree.Id
                || other.Anchor.X < tree.Anchor.X - 400
                || other.Anchor.X > tree.Anchor.X + 100
                || Math.Abs(other.Anchor.Z - tree.Anchor.Z) > GameObjects.SpreadCentimetres(KindOf(other)) + 80
            );
            if (clear)
            {
                return tree;
            }
        }

        throw new InvalidOperationException("No broadleaf near the start.");
    }

    /// <summary>
    /// The boulder of seed 1 nearest the middle, out to 100 m (a point more than 1.5 m over the ground): its highest point,
    /// where its -x foot meets the ground along the row through it (a little out from it), and the ground's height there,
    /// in metres.
    /// </summary>
    internal static (Vector3 Middle, Vector3 Foot, float Ground) FindBoulder()
    {
        for (var reach = 100; reach <= 10_000; reach += 100)
        {
            for (var x = -reach; x <= reach; x += 100)
            {
                for (var z = -reach; z <= reach; z += 100)
                {
                    if (
                        Math.Max(Math.Abs(x), Math.Abs(z)) != reach
                        || GameGenerator.SurfaceHeight(x, z, 1) - GameGenerator.Height(x, z, 1) < 150
                    )
                    {
                        continue;
                    }

                    // Up to its highest point, then out along -x to where it meets the ground.
                    var (topX, topZ) = (x, z);
                    var climbing = true;
                    while (climbing)
                    {
                        climbing = false;
                        foreach (var (dx, dz) in new[] { (10, 0), (-10, 0), (0, 10), (0, -10) })
                        {
                            if (
                                GameGenerator.SurfaceHeight(topX + dx, topZ + dz, 1)
                                > GameGenerator.SurfaceHeight(topX, topZ, 1)
                            )
                            {
                                (topX, topZ) = (topX + dx, topZ + dz);
                                climbing = true;
                            }
                        }
                    }

                    var edge = topX;
                    while (GameGenerator.SurfaceHeight(edge, topZ, 1) - GameGenerator.Height(edge, topZ, 1) > 1)
                    {
                        edge -= 5;
                    }

                    var footX = edge - 30;
                    var ground = (float)GameGenerator.SurfaceHeight(footX, topZ, 1) / 100;
                    var middle = new Vector3(
                        topX / 100f,
                        (float)GameGenerator.SurfaceHeight(topX, topZ, 1) / 100,
                        topZ / 100f
                    );
                    return (middle, new Vector3(footX / 100f, ground, topZ / 100f), ground);
                }
            }
        }

        throw new InvalidOperationException("No boulder near the start.");
    }

    /// <summary>The match's origin, which the starter's start pose puts at its eye.</summary>
    private static WorldOrigin Origin(HeadlessMatch match) => match.Services.GetRequiredService<WorldOrigin>();

    private static CameraPose Camera(World world)
    {
        var found = default(CameraPose);
        world.Query(new QueryDescription().WithAll<CameraPose, ActiveCamera>(), (ref CameraPose pose) => found = pose);
        return found;
    }

    private static Climber Climb(World world)
    {
        var found = default(Climber);
        world.Query(new QueryDescription().WithAll<Climber, LocalPlayer>(), (ref Climber climber) => found = climber);
        return found;
    }

    private static (FoxRoute Route, MeshPlacement Placement, MeshAnimator Animator) Fox(World world)
    {
        var found = default((FoxRoute, MeshPlacement, MeshAnimator));
        var count = 0;
        world.Query(
            in s_fox,
            (ref FoxRoute route, ref MeshPlacement placement, ref MeshAnimator animator) =>
            {
                found = (route, placement, animator);
                count++;
            }
        );
        Assert.Equal(1, count);
        return found;
    }

    private static (CharacterPosition Position, CharacterContact Contact) Player(World world)
    {
        var found = default((CharacterPosition, CharacterContact));
        var count = 0;
        world.Query(
            in s_player,
            (ref CharacterPosition position, ref CharacterContact contact) =>
            {
                found = (position, contact);
                count++;
            }
        );
        Assert.Equal(1, count);
        return found;
    }

    private sealed class Output : IConsoleOutput
    {
        public List<string> Failures { get; } = [];

        public void Print(string text) { }

        public void PrintColumns(string left, string right, int column, int indent) { }

        public void PrintError(string text) => Failures.Add(text);
    }
}
