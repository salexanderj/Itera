using System.Numerics;
using Itera.Engine.Assets;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// <see cref="MeshAssetDraws"/> over regions in memory: instances culled by the view and the cascades as their shadow
/// allows, grouped into one command per primitive with its instances' draws, by pipeline range; posed instances bounded
/// by their joints, given skin groups and skinning matrices in the slot's regions, their draws reading their skinned
/// copies, and drawn unposed past the slot's room or with the wrong joints; nothing allocated.
/// </summary>
public sealed class MeshAssetDrawsTests
{
    private readonly FakeTargets _targets;
    private readonly MeshView _view;
    private readonly MeshAssetDraws _draws;

    public MeshAssetDrawsTests()
    {
        var opaque = MeshAssetPassTests.Asset("opaque", MeshMaterialData.Default, vertices: 3);
        var mixed = MeshAssetPassTests.Asset(
            "mixed",
            new MeshMaterialData { AlphaMode = MeshAlphaMode.Mask },
            vertices: 4,
            second: new MeshMaterialData { DoubleSided = true }
        );
        _targets = new FakeTargets(MeshAssetLayout.From([opaque, mixed]));
        _view = new MeshView(_targets, Options.Create(new EngineOptions()));
        _draws = new MeshAssetDraws(_targets, NullLogger<MeshAssetDraws>.Instance);
    }

    [Fact]
    public void Instances_in_view_make_one_command_per_primitive_with_their_draws()
    {
        _view.Begin(1, LookingAlongMinusZ(shadows: false));
        MeshInstance[] instances =
        [
            new(0, Matrix4x4.CreateTranslation(0, 0, -10), MeshInstances.NoTint, ModelShadow.Casts),
            new(0, Matrix4x4.CreateTranslation(1, 0, -12), 0xFF0000FFu, ModelShadow.Casts),
            new(1, Matrix4x4.CreateTranslation(-1, 0, -8), MeshInstances.NoTint, ModelShadow.Casts),
        ];

        var frame = _draws.Write(1, _view, instances);

        // The opaque asset's primitive (two instances), the mixed asset's two-sided one, then its masked one.
        Assert.Equal(new MeshAssetList(1, 1, 1, 0, 4), frame.Camera);
        var commands = _targets.Commands(1, 0);
        var drawBase = (uint)MeshAssetPass.DrawBase(1, 0);
        Assert.Equal(new DrawIndexedIndirectCommand(3, 2, 0, 0, drawBase), commands[0]);
        Assert.Equal(new DrawIndexedIndirectCommand(3, 1, 6, 3, drawBase + 2), commands[1]);
        Assert.Equal(new DrawIndexedIndirectCommand(3, 1, 3, 3, drawBase + 3), commands[2]);
        var draws = _targets.Draws(1, 0);
        Assert.Equal(instances[0].Transform, draws[0].Transform);
        Assert.Equal(0xFF0000FFu, draws[1].Tint);
        Assert.Equal(2u, draws[2].Material);
        Assert.Equal(1u, draws[3].Material);
        // Shadows off: nothing casts.
        Assert.Equal(default, frame.Near);
        Assert.Equal(default, frame.Far);
    }

    [Fact]
    public void Instances_out_of_view_are_not_drawn()
    {
        _view.Begin(0, LookingAlongMinusZ(shadows: false));
        MeshInstance[] instances =
        [
            new(0, Matrix4x4.CreateTranslation(0, 0, 10), MeshInstances.NoTint, ModelShadow.Casts),
            new(0, Matrix4x4.CreateTranslation(0, 0, -500), MeshInstances.NoTint, ModelShadow.Casts),
        ];

        var frame = _draws.Write(0, _view, instances);

        Assert.Equal(0, frame.Camera.Commands);
    }

    [Fact]
    public void Shadow_only_instances_are_cast_but_not_drawn_and_shadowless_ones_drawn_but_not_cast()
    {
        _view.Begin(0, LookingAlongMinusZ(shadows: true));
        var at = Matrix4x4.CreateTranslation(0, 0, -10);

        var only = _draws.Write(0, _view, [new MeshInstance(0, at, MeshInstances.NoTint, ModelShadow.Only)]);
        var none = _draws.Write(0, _view, [new MeshInstance(0, at, MeshInstances.NoTint, ModelShadow.None)]);
        var casts = _draws.Write(0, _view, [new MeshInstance(0, at, MeshInstances.NoTint, ModelShadow.Casts)]);

        Assert.Equal((0, 1), (only.Camera.Draws, only.Near.Draws));
        Assert.Equal((1, 0, 0), (none.Camera.Draws, none.Near.Draws, none.Far.Draws));
        Assert.Equal((1, 1), (casts.Camera.Draws, casts.Near.Draws));
    }

    [Fact]
    public void A_large_scale_grows_the_bounding_sphere()
    {
        _view.Begin(0, LookingAlongMinusZ(shadows: false));
        // The asset is 2 m wide with its centre at x = 1: scaled 100 times and moved 160 m along -x, its sphere's centre is
        // 60 m to the side and its edge still reaches the view.
        var transform = Matrix4x4.CreateScale(100) * Matrix4x4.CreateTranslation(-160, 0, -20);

        var frame = _draws.Write(0, _view, [new MeshInstance(0, transform, MeshInstances.NoTint, ModelShadow.Casts)]);

        Assert.Equal(1, frame.Camera.Draws);
    }

    [Fact]
    public void An_instance_of_an_unknown_asset_is_left_out()
    {
        _view.Begin(0, LookingAlongMinusZ(shadows: false));

        var frame = _draws.Write(
            0,
            _view,
            [new MeshInstance(7, Matrix4x4.CreateTranslation(0, 0, -10), MeshInstances.NoTint, ModelShadow.Casts)]
        );

        Assert.Equal(0, frame.Camera.Commands);
    }

    [Fact]
    public void A_full_list_leaves_the_instances_past_it_out()
    {
        _view.Begin(0, LookingAlongMinusZ(shadows: false));
        // The mixed asset draws two primitives an instance: 9,000 instances would make 18,000 draws.
        var instances = new MeshInstance[9000];
        Array.Fill(
            instances,
            new MeshInstance(1, Matrix4x4.CreateTranslation(0, 0, -10), MeshInstances.NoTint, ModelShadow.Casts)
        );

        var frame = _draws.Write(0, _view, instances);

        Assert.Equal(MeshAssetPass.MaxDraws, frame.Camera.Draws);
        var commands = _targets.Commands(0, 0);
        Assert.Equal((uint)(MeshAssetPass.MaxDraws / 2), commands[0].InstanceCount);
    }

    [Fact]
    public void Primitives_past_the_room_for_commands_are_left_out()
    {
        var assets = new MeshAsset[MeshAssetPass.MaxCommands + 3];
        var instances = new MeshInstance[assets.Length];
        for (var i = 0; i < assets.Length; i++)
        {
            assets[i] = MeshAssetPassTests.Asset($"a{i}", MeshMaterialData.Default, vertices: 3);
            instances[i] = new MeshInstance(
                i,
                Matrix4x4.CreateTranslation(0, 0, -10),
                MeshInstances.NoTint,
                ModelShadow.Casts
            );
        }

        var targets = new FakeTargets(MeshAssetLayout.From(assets));
        var view = new MeshView(targets, Options.Create(new EngineOptions()));
        var draws = new MeshAssetDraws(targets, NullLogger<MeshAssetDraws>.Instance);
        view.Begin(0, LookingAlongMinusZ(shadows: false));

        var frame = draws.Write(0, view, instances);

        // One primitive an asset, in the assets' order: the last command is the last asset's that fits.
        Assert.Equal(MeshAssetPass.MaxCommands, frame.Camera.Commands);
        Assert.Equal((MeshAssetPass.MaxCommands - 1) * 3, targets.Commands(0, 0)[^1].VertexOffset);
    }

    [Fact]
    public void A_posed_instance_is_skinned_into_the_slots_region_and_its_draws_read_it()
    {
        var (targets, view, draws) = Skinned(capacity: 100);
        view.Begin(1, LookingAlongMinusZ(shadows: false));
        var at = Matrix4x4.CreateTranslation(0, 0, -10);
        MeshInstance[] instances =
        [
            new(0, at, MeshInstances.NoTint, ModelShadow.Casts),
            new(1, at, MeshInstances.NoTint, ModelShadow.Casts, FirstJoint: 0, Joints: 2),
        ];
        Matrix4x4[] palettes = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 1, 0)];

        var frame = draws.Write(1, view, instances, palettes);

        Assert.Equal((1, 3, 2), (frame.SkinGroups, frame.SkinVertices, frame.SkinJoints));
        var group = targets.SkinGroupsOf(1)[0];
        var place = targets.Layout.Assets[1];
        Assert.Equal((uint)place.FirstVertex, group.SourceFirst);
        Assert.Equal(0u, group.SkinFirst);
        Assert.Equal((uint)targets.SkinVertexBase(1), group.OutputFirst);
        Assert.Equal((uint)MeshAssetPass.PaletteBase(1), group.PaletteFirst);
        Assert.Equal(3u, group.VertexCount);
        Assert.Equal(palettes, targets.PalettesOf(1)[..2].ToArray());
        // The static asset's draw reads its own vertices, the posed one's its copy.
        var written = targets.Draws(1, 0);
        Assert.Equal(0, written[0].VertexDelta);
        Assert.Equal(targets.SkinVertexBase(1) - place.FirstVertex, written[1].VertexDelta);
    }

    [Fact]
    public void A_posed_instance_is_culled_where_its_joints_are_not_where_its_asset_was()
    {
        var (_, view, draws) = Skinned(capacity: 100);
        view.Begin(0, LookingAlongMinusZ(shadows: false));
        // Placed behind the camera, its joints moved 30 m along -z: in view.
        var behind = new MeshInstance(
            1,
            Matrix4x4.CreateTranslation(0, 0, 10),
            MeshInstances.NoTint,
            ModelShadow.Casts,
            0,
            2
        );
        Matrix4x4[] forward = [Matrix4x4.CreateTranslation(0, 0, -30), Matrix4x4.CreateTranslation(0, 0, -30)];
        // Placed ahead, its joints moved 30 m along +z: out of view.
        var ahead = behind with
        {
            Transform = Matrix4x4.CreateTranslation(0, 0, -10),
        };
        Matrix4x4[] back = [Matrix4x4.CreateTranslation(0, 0, 30), Matrix4x4.CreateTranslation(0, 0, 30)];

        Assert.Equal(1, draws.Write(0, view, [behind], forward).Camera.Draws);
        Assert.Equal(0, draws.Write(0, view, [ahead], back).Camera.Draws);
    }

    [Fact]
    public void Posed_instances_past_the_slots_room_are_drawn_unposed()
    {
        var (targets, view, draws) = Skinned(capacity: 4);
        view.Begin(0, LookingAlongMinusZ(shadows: false));
        var posed = new MeshInstance(
            1,
            Matrix4x4.CreateTranslation(0, 0, -10),
            MeshInstances.NoTint,
            ModelShadow.Casts,
            0,
            2
        );
        Matrix4x4[] palettes = [Matrix4x4.Identity, Matrix4x4.Identity];

        var frame = draws.Write(0, view, [posed, posed], palettes);

        Assert.Equal(1, frame.SkinGroups);
        var written = targets.Draws(0, 0);
        Assert.NotEqual(0, written[0].VertexDelta);
        Assert.Equal(0, written[1].VertexDelta);
    }

    [Fact]
    public void A_posed_instance_with_the_wrong_joints_is_drawn_unposed()
    {
        var (targets, view, draws) = Skinned(capacity: 100);
        view.Begin(0, LookingAlongMinusZ(shadows: false));
        var posed = new MeshInstance(
            1,
            Matrix4x4.CreateTranslation(0, 0, -10),
            MeshInstances.NoTint,
            ModelShadow.Casts,
            0,
            1
        );

        var frame = draws.Write(0, view, [posed], [Matrix4x4.Identity]);

        Assert.Equal((0, 1), (frame.SkinGroups, frame.Camera.Draws));
        Assert.Equal(0, targets.Draws(0, 0)[0].VertexDelta);
    }

    [Fact]
    public void A_frame_of_posed_instances_allocates_nothing()
    {
        var (_, view, draws) = Skinned(capacity: 1000);
        var constants = LookingAlongMinusZ(shadows: true);
        var instances = new MeshInstance[100];
        var palettes = new Matrix4x4[200];
        for (var i = 0; i < instances.Length; i++)
        {
            instances[i] = new MeshInstance(
                1,
                Matrix4x4.CreateTranslation(i % 10, 0, -5 - i / 10),
                MeshInstances.NoTint,
                ModelShadow.Casts,
                2 * i,
                2
            );
            palettes[2 * i] = Matrix4x4.Identity;
            palettes[2 * i + 1] = Matrix4x4.Identity;
        }

        void Frame()
        {
            view.Begin(0, in constants);
            draws.Write(0, view, instances, palettes);
            view.End(0, []);
        }

        for (var i = 0; i < 50; i++)
        {
            Frame();
        }

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                Frame();
            }
        });

        Assert.Equal(0, bytes);
    }

    /// <summary>
    /// Blended primitives are in no list's ranges and cast nothing: the blended list draws them, instances farthest
    /// first, a two-sided one's back faces before its front faces, each one draw and command.
    /// </summary>
    [Fact]
    public void Blended_primitives_are_drawn_apart_back_to_front_and_cast_no_shadow()
    {
        var glass = new MeshMaterialData { AlphaMode = MeshAlphaMode.Blend };
        var targets = new FakeTargets(
            MeshAssetLayout.From([
                MeshAssetPassTests.Asset("opaque", MeshMaterialData.Default, vertices: 3),
                MeshAssetPassTests.Asset("pane", glass, vertices: 3),
                MeshAssetPassTests.Asset(
                    "lantern",
                    MeshMaterialData.Default,
                    vertices: 4,
                    second: glass with
                    {
                        DoubleSided = true,
                    }
                ),
            ])
        );
        var view = new MeshView(targets, Options.Create(new EngineOptions()));
        var draws = new MeshAssetDraws(targets, NullLogger<MeshAssetDraws>.Instance);
        view.Begin(0, LookingAlongMinusZ(shadows: true));
        MeshInstance[] instances =
        [
            new(1, Matrix4x4.CreateTranslation(0, 0, -5), MeshInstances.NoTint, ModelShadow.Casts),
            new(2, Matrix4x4.CreateTranslation(0, 0, -20), 0x80FFFFFFu, ModelShadow.Casts),
            new(1, Matrix4x4.CreateTranslation(1, 0, -12), MeshInstances.NoTint, ModelShadow.Casts),
            new(0, Matrix4x4.CreateTranslation(0, 0, -8), MeshInstances.NoTint, ModelShadow.Casts),
        ];

        var frame = draws.Write(0, view, instances);

        // The camera's lists hold the opaque primitives alone (the opaque asset's and the lantern's frame); the cascades
        // the same, and no blended one.
        Assert.Equal(new MeshAssetList(2, 0, 0, 0, 2), frame.Camera);
        Assert.Equal(2, frame.Near.Draws);
        // The lantern (20 m) first, its glass's back faces then front faces; the pane at 12 m; the one at 5 m last.
        Assert.Equal(4, frame.Blended);
        var blended = targets.Draws(0, MeshAssetPass.BlendedList);
        var commands = targets.Commands(0, MeshAssetPass.BlendedList);
        Assert.Equal(
            [
                (instances[1].Transform, MeshAssetDraw.BackOnly),
                (instances[1].Transform, MeshAssetDraw.FrontOnly),
                (instances[2].Transform, MeshAssetDraw.FrontOnly),
                (instances[0].Transform, MeshAssetDraw.FrontOnly),
            ],
            blended.Take(4).Select(d => (d.Transform, d.Sides))
        );
        Assert.Equal(0x80FFFFFFu, blended[0].Tint);
        var drawBase = (uint)MeshAssetPass.DrawBase(0, MeshAssetPass.BlendedList);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(1u, commands[i].InstanceCount);
            Assert.Equal(drawBase + (uint)i, commands[i].FirstInstance);
        }

        // The lantern's glass is its second primitive: its indices after its frame's, its vertices after the pane's.
        Assert.Equal(new DrawIndexedIndirectCommand(3, 1, 9, 6, drawBase), commands[0]);
        Assert.Equal(new DrawIndexedIndirectCommand(3, 1, 3, 3, drawBase + 2), commands[2]);
    }

    [Fact]
    public void A_frame_of_blended_instances_allocates_nothing()
    {
        var glass = new MeshMaterialData { AlphaMode = MeshAlphaMode.Blend, DoubleSided = true };
        var targets = new FakeTargets(MeshAssetLayout.From([MeshAssetPassTests.Asset("glass", glass, vertices: 3)]));
        var view = new MeshView(targets, Options.Create(new EngineOptions()));
        var draws = new MeshAssetDraws(targets, NullLogger<MeshAssetDraws>.Instance);
        var constants = LookingAlongMinusZ(shadows: true);
        var instances = new MeshInstance[300];
        for (var i = 0; i < instances.Length; i++)
        {
            instances[i] = new MeshInstance(
                0,
                Matrix4x4.CreateTranslation(i % 10, 0, -5 - (i * 7 % 40)),
                MeshInstances.NoTint,
                ModelShadow.Casts
            );
        }

        void Frame()
        {
            view.Begin(0, in constants);
            draws.Write(0, view, instances);
            view.End(0, []);
        }

        for (var i = 0; i < 50; i++)
        {
            Frame();
        }

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                Frame();
            }
        });

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void A_frame_allocates_nothing()
    {
        var constants = LookingAlongMinusZ(shadows: true);
        var instances = new MeshInstance[200];
        for (var i = 0; i < instances.Length; i++)
        {
            instances[i] = new MeshInstance(
                i % 2,
                Matrix4x4.CreateTranslation(i % 10, 0, -5 - i / 10),
                MeshInstances.NoTint,
                ModelShadow.Casts
            );
        }

        for (var i = 0; i < 50; i++)
        {
            Frame(in constants, instances);
        }

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                Frame(in constants, instances);
            }
        });

        Assert.Equal(0, bytes);
    }

    private void Frame(in ViewConstants constants, MeshInstance[] instances)
    {
        _view.Begin(0, in constants);
        _draws.Write(0, _view, instances);
        _view.End(0, []);
    }

    /// <summary>A static asset (0) and a skinned one (1, <see cref="MeshAssetPassTests.SkinnedAsset"/>), with room for <paramref name="capacity"/> skinned vertices a slot.</summary>
    private static (FakeTargets Targets, MeshView View, MeshAssetDraws Draws) Skinned(int capacity)
    {
        var targets = new FakeTargets(
            MeshAssetLayout.From([
                MeshAssetPassTests.Asset("static", MeshMaterialData.Default, vertices: 3),
                MeshAssetPassTests.SkinnedAsset("posed"),
            ]),
            capacity
        );
        return (
            targets,
            new MeshView(targets, Options.Create(new EngineOptions())),
            new MeshAssetDraws(targets, NullLogger<MeshAssetDraws>.Instance)
        );
    }

    private static ViewConstants LookingAlongMinusZ(bool shadows) =>
        new()
        {
            Position = Vector3.Zero,
            Forward = new Vector3(0, 0, -1),
            Right = new Vector3(1, 0, 0),
            Up = new Vector3(0, 1, 0),
            SunDirection = ViewConstants.DefaultSunDirection,
            MaxDistance = 200,
            Switches = shadows ? ViewSwitches.Shadows : ViewSwitches.None,
        };

    /// <summary>The asset draws' and the view's regions of two slots, in memory.</summary>
    private sealed class FakeTargets(MeshAssetLayout layout, int skinCapacity = 0) : IMeshAssetTarget, IMeshDrawTarget
    {
        private readonly MeshSkinGroup[] _groups = new MeshSkinGroup[
            FrameSync.FramesInFlight * MeshAssetPass.MaxSkinGroups
        ];
        private readonly Matrix4x4[] _palettes = new Matrix4x4[
            FrameSync.FramesInFlight * MeshInstances.MaxPaletteJoints
        ];

        private readonly MeshAssetDraw[] _draws = new MeshAssetDraw[
            MeshAssetPass.DrawBase(FrameSync.FramesInFlight, 0)
        ];
        private readonly DrawIndexedIndirectCommand[] _commands = new DrawIndexedIndirectCommand[
            MeshAssetPass.CommandBase(FrameSync.FramesInFlight, 0)
        ];
        private readonly MeshLighting[] _lighting = new MeshLighting[FrameSync.FramesInFlight];
        private readonly MeshLight[] _lights = new MeshLight[FrameSync.FramesInFlight * MeshPass.MostLights];

        public MeshAssetLayout Layout { get; } = layout;

        public ulong HeapBytes => 0;

        public Span<MeshAssetDraw> DrawsOf(int slot, int list) =>
            _draws.AsSpan(MeshAssetPass.DrawBase(slot, list), MeshAssetPass.MaxDraws);

        public Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list) =>
            _commands.AsSpan(MeshAssetPass.CommandBase(slot, list), MeshAssetPass.MaxCommands);

        public MeshAssetDraw[] Draws(int slot, int list) => DrawsOf(slot, list).ToArray();

        public DrawIndexedIndirectCommand[] Commands(int slot, int list) => CommandsOf(slot, list).ToArray();

        public int SkinVertexCapacity => skinCapacity;

        public int SkinVertexBase(int slot) => Layout.VertexCount + slot * skinCapacity;

        public Span<MeshSkinGroup> SkinGroupsOf(int slot) =>
            _groups.AsSpan(MeshAssetPass.GroupBase(slot), MeshAssetPass.MaxSkinGroups);

        public Span<Matrix4x4> PalettesOf(int slot) =>
            _palettes.AsSpan(MeshAssetPass.PaletteBase(slot), MeshInstances.MaxPaletteJoints);

        public ref MeshLighting LightingOf(int slot) => ref _lighting[slot];

        public Span<MeshLight> LightsOf(int slot) => _lights.AsSpan(slot * MeshPass.MostLights, MeshPass.MostLights);

        public Span<MeshDraw> DrawsOf(int slot) => throw new NotSupportedException();

        Span<DrawIndexedIndirectCommand> IMeshDrawTarget.CommandsOf(int slot, int list) =>
            throw new NotSupportedException();

        public Span<MeshModelDraw> ModelDrawsOf(int slot, int list) => throw new NotSupportedException();

        public Span<DrawIndexedIndirectCommand> ModelCommandsOf(int slot, int list) =>
            throw new NotSupportedException();
    }
}
