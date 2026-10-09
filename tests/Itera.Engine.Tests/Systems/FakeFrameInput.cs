using System.Numerics;
using Itera.Engine.Input;

namespace Itera.Engine.Tests.Systems;

/// <summary>Frame input for system tests: a snapshot built with <see cref="InputAccumulator"/>.</summary>
internal sealed class FakeFrameInput : IFrameInput
{
    private InputSnapshot _snapshot;

    public InputSnapshot Snapshot
    {
        set => _snapshot = value;
    }

    public ref readonly InputSnapshot Frame => ref _snapshot;

    public Vector2 MouseSinceTick { get; set; }
}
