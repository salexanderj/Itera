using Itera.Engine.Input;

namespace Itera.Engine.Tests.Systems;

/// <summary>Tick input for system tests: a snapshot built with <see cref="InputAccumulator"/>.</summary>
internal sealed class FakeTickInput : ITickInput
{
    private InputSnapshot _snapshot;

    public InputSnapshot Snapshot
    {
        set => _snapshot = value;
    }

    public ref readonly InputSnapshot Tick => ref _snapshot;
}
