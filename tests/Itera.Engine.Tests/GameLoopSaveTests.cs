namespace Itera.Engine.Tests;

/// <summary>
/// When the match's world is saved as the run ends:
/// after a loop that ended without an exception, or when the render thread's fault ended it (the world lives on the main
/// thread and is whole); not after another exception of the loop, which may have left it half changed.
/// </summary>
public sealed class GameLoopSaveTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void The_world_is_saved_after_a_clean_end_or_a_render_thread_fault(
        bool loopEnded,
        bool renderFault,
        bool saved
    )
    {
        Assert.Equal(saved, GameLoop.SavesAtEnd(loopEnded, renderFault));
    }
}
