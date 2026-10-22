using Arch.System;

namespace Itera.TestSupport;

/// <summary>Measures the managed memory a system allocates per update, as the game loop calls it.</summary>
internal static class SystemAllocation
{
    /// <summary>
    /// Runs the system's BeforeUpdate, Update, and AfterUpdate <paramref name="warmup"/> times so first-use
    /// work (component registration, query caching, tiered JIT) is done, then returns the bytes allocated on
    /// this thread across <paramref name="measured"/> more runs. A system that follows the ECS rules returns 0.
    /// </summary>
    /// <param name="system">The system under test, already initialised.</param>
    /// <param name="time">The value passed to every call: a <c>FixedStepTime</c> or a <c>FrameTime</c>.</param>
    /// <param name="warmup">Runs before measuring.</param>
    /// <param name="measured">Runs that are measured.</param>
    public static long BytesAllocated<T>(ISystem<T> system, T time, int warmup = 500, int measured = 1000)
    {
        for (var i = 0; i < warmup; i++)
        {
            Run(system, in time);
        }

        var before = StartMeasurement();
        for (var i = 0; i < measured; i++)
        {
            Run(system, in time);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// <see cref="BytesAllocated{T}(ISystem{T}, T, int, int)"/> with a time value for each run, made by
    /// <paramref name="timeOf"/> from the run's number (counting warm-up runs first): for systems that act only as the tick
    /// or the frame's time moves on. Counts this thread's allocations only.
    /// </summary>
    public static long BytesAllocated<T>(ISystem<T> system, Func<int, T> timeOf, int warmup = 500, int measured = 1000)
    {
        for (var i = 0; i < warmup; i++)
        {
            var time = timeOf(i);
            Run(system, in time);
        }

        var before = StartMeasurement();
        for (var i = 0; i < measured; i++)
        {
            var time = timeOf(warmup + i);
            Run(system, in time);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// Reads this thread's allocation counter after a gen0 collection; use it as the start of every measured window.
    /// The collection empties the thread's allocation block. Without it, a collection that another thread's
    /// allocation triggers during the window makes <see cref="GC.GetAllocatedBytesForCurrentThread"/> count the
    /// unused rest of this thread's block (up to 8 KB) as allocated, so a window with no allocation fails at random.
    /// Found on 2026-09-23: the heartbeat test failed with 2,464 and 7,584 bytes in about one full test run in ten;
    /// the jump was reproduced outside the tests, and a gen0 collection first removed it. The collection also makes
    /// the window start with any weakly held runtime cache dropped, so code that rebuilds one (for example
    /// Enum.IsDefined) fails every time instead of at random.
    /// </summary>
    public static long StartMeasurement()
    {
        GC.Collect(0);
        return GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>
    /// The bytes this thread allocates running <paramref name="measured"/> once, in a window no collection fell in: a
    /// collection another thread's allocation sets off during a long window can count up to 8 KB never allocated (as at
    /// the start, <see cref="StartMeasurement"/>), so a window with one is run again, at most
    /// <paramref name="attempts"/> times, and the least is returned. A real allocation shows in every window. Found on
    /// 2026-10-08: a 100-frame window of the world's meshes counted 16,800 bytes in one run of three beside the mesher's
    /// tests, none alone. A background collection's pauses count no gen0 collection but add the bytes too (8,168 in one
    /// frame of a full suite's run, 2026-10-22), so a window the runtime paused threads in is run again as well.
    /// </summary>
    public static long BytesAllocatedWithoutCollection(Action measured, int attempts = 5)
    {
        var least = long.MaxValue;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var before = StartMeasurement();
            var collections = GC.CollectionCount(0);
            var paused = GC.GetTotalPauseDuration();
            measured();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            least = Math.Min(least, allocated);
            if (GC.CollectionCount(0) == collections && GC.GetTotalPauseDuration() == paused)
            {
                return allocated;
            }
        }

        return least;
    }

    private static void Run<T>(ISystem<T> system, in T time)
    {
        system.BeforeUpdate(in time);
        system.Update(in time);
        system.AfterUpdate(in time);
    }
}
