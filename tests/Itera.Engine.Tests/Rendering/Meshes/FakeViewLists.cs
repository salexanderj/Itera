using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>An image view's lists of two frame slots in memory (<see cref="IRenderViewLists"/>), with its own view.</summary>
internal sealed class FakeViewLists : IRenderViewLists
{
    private const int Slots = FrameSync.FramesInFlight;
    private readonly MeshDraw[] _draws = new MeshDraw[Slots * RenderViewTarget.MaxDraws];
    private readonly DrawIndexedIndirectCommand[] _commands = new DrawIndexedIndirectCommand[
        Slots * RenderViewTarget.MaxDraws
    ];
    private readonly MeshLighting[] _lighting = new MeshLighting[Slots];
    private readonly MeshLight[] _lights = new MeshLight[Slots * MeshPass.MostLights];
    private readonly MeshModelDraw[] _modelDraws = new MeshModelDraw[Slots * RenderViewTarget.MaxModelRecords];
    private readonly DrawIndexedIndirectCommand[] _modelCommands = new DrawIndexedIndirectCommand[
        Slots * RenderViewTarget.MaxModelCommands
    ];
    private readonly MeshAssetDraw[] _assetDraws = new MeshAssetDraw[RenderViewTarget.AssetDrawBase(Slots, 0)];
    private readonly DrawIndexedIndirectCommand[] _assetCommands = new DrawIndexedIndirectCommand[
        Slots * 2 * RenderViewTarget.MaxAssetCommands
    ];
    private readonly RenderViewFrame[] _frames = new RenderViewFrame[Slots];

    public FakeViewLists(int index = 0)
    {
        Index = index;
        View = new MeshView(this, Options.Create(new EngineOptions()));
    }

    public int Index { get; }

    public MeshView View { get; }

    public ulong HeapBytes => 0;

    /// <summary>
    /// An orthographic view from <paramref name="middle"/> looking straight down, north up, 32 m across and 18 m high,
    /// 100 m each way along the view.
    /// </summary>
    public static ViewConstants LookingDown(Vector3 middle) =>
        new()
        {
            Position = middle,
            Forward = new Vector3(0, -1, 0),
            Right = new Vector3(16, 0, 0),
            Up = new Vector3(0, 0, -9),
            SunDirection = ViewConstants.DefaultSunDirection,
            MaxDistance = 200,
            Switches = ViewSwitches.Orthographic,
            Near = -100,
            Far = 100,
        };

    public ref RenderViewFrame FrameOf(int slot) => ref _frames[slot];

    public Span<MeshDraw> DrawsOf(int slot) =>
        _draws.AsSpan(RenderViewTarget.RecordBase(slot), RenderViewTarget.MaxDraws);

    public Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list) =>
        _commands.AsSpan(slot * RenderViewTarget.MaxDraws, RenderViewTarget.MaxDraws);

    public ref MeshLighting LightingOf(int slot) => ref _lighting[slot];

    public Span<MeshLight> LightsOf(int slot) => _lights.AsSpan(slot * MeshPass.MostLights, MeshPass.MostLights);

    public Span<MeshModelDraw> ModelDrawsOf(int slot, int list) =>
        _modelDraws.AsSpan(RenderViewTarget.ModelRecordBase(slot), RenderViewTarget.MaxModelRecords);

    public Span<DrawIndexedIndirectCommand> ModelCommandsOf(int slot, int list) =>
        _modelCommands.AsSpan(slot * RenderViewTarget.MaxModelCommands, RenderViewTarget.MaxModelCommands);

    public Span<MeshAssetDraw> AssetDrawsOf(int slot, int list) =>
        _assetDraws.AsSpan(RenderViewTarget.AssetDrawBase(slot, list), RenderViewTarget.MaxAssetDraws);

    public Span<DrawIndexedIndirectCommand> AssetCommandsOf(int slot, int list) =>
        _assetCommands.AsSpan(
            ((slot * 2) + list) * RenderViewTarget.MaxAssetCommands,
            RenderViewTarget.MaxAssetCommands
        );
}
