using Itera.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// Measures GPU time per pass with timestamp queries: two per <see cref="GpuPass"/> per frame slot, and two for each of
/// a game's passes (<see cref="BeginGame"/>, at most <see cref="GpuTimings.MostGamePasses"/>), whose sum is
/// <see cref="GpuPass.Game"/>. The render thread
/// resets a slot's queries at the start of its command buffer, writes a timestamp before and after each pass, and
/// reads the results after that slot's next fence wait, when they are complete. Times go to <see cref="GpuTimings"/>,
/// which <see cref="FrameStats"/> logs. When the graphics queue has no timestamps (<c>timestampValidBits</c> 0), it
/// records nothing. Created before the render thread starts; used by the render thread only.
/// </summary>
public sealed unsafe partial class GpuTimer : IDisposable
{
    private const int Pairs = GpuTimings.PassCount + GpuTimings.MostGamePasses;
    private const int QueriesPerSlot = Pairs * 2;

    private readonly VulkanContext _context;
    private readonly GpuTimings _timings;
    private readonly ILogger<GpuTimer> _logger;
    private readonly bool[] _written = new bool[FrameSync.FramesInFlight];

    /// <summary>Value and availability word for each query of one slot.</summary>
    private readonly ulong[] _results = new ulong[QueriesPerSlot * 2];
    private QueryPool _pool;
    private double _nanosecondsPerTick;
    private ulong _validMask;
    private bool _disposed;

    public GpuTimer(VulkanContext context, GpuTimings timings, ILogger<GpuTimer> logger)
    {
        _context = context;
        _timings = timings;
        _logger = logger;
    }

    /// <summary>True when timestamps are written.</summary>
    public bool Enabled => _pool.Handle != 0;

    /// <summary>Creates the query pool, or logs why the GPU cannot be timed.</summary>
    public void Initialize()
    {
        var bits = _context.GraphicsTimestampValidBits;
        if (bits == 0)
        {
            LogUnavailable();
            return;
        }

        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = FrameSync.FramesInFlight * QueriesPerSlot,
        };
        _context.Vk.CreateQueryPool(_context.Device, &info, null, out _pool).ThrowIfFailed("vkCreateQueryPool");
        _context.SetObjectName(ObjectType.QueryPool, _pool.Handle, "gpu timer");
        _nanosecondsPerTick = _context.Limits.TimestampPeriod;
        _validMask = bits >= 64 ? ulong.MaxValue : (1UL << (int)bits) - 1;
        _timings.Enabled = true;
    }

    /// <summary>
    /// Reads the times the slot's last submitted frame wrote. Render thread, right after the slot's fence wait, so the
    /// queries are complete. A query that was reset but not written (a pass that did not run) is skipped.
    /// </summary>
    public void Collect(int slot)
    {
        if (!Enabled || !_written[slot])
        {
            return;
        }

        _written[slot] = false;
        fixed (ulong* results = _results)
        {
            var result = _context.Vk.GetQueryPoolResults(
                _context.Device,
                _pool,
                (uint)(slot * QueriesPerSlot),
                QueriesPerSlot,
                (nuint)(_results.Length * sizeof(ulong)),
                results,
                2 * sizeof(ulong),
                QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit
            );
            if (result is not (Result.Success or Result.NotReady))
            {
                result.ThrowIfFailed("vkGetQueryPoolResults");
            }
        }

        Span<long> nanoseconds = stackalloc long[Pairs];
        Parse(_results, _validMask, _nanosecondsPerTick, nanoseconds);
        for (var pass = 0; pass < GpuTimings.PassCount; pass++)
        {
            if (nanoseconds[pass] >= 0 && pass != (int)GpuPass.Game)
            {
                _timings.Record((GpuPass)pass, nanoseconds[pass]);
            }
        }

        // The game's passes one by one, and their sum when any ran.
        long game = -1;
        for (var index = 0; index < GpuTimings.MostGamePasses; index++)
        {
            var time = nanoseconds[GpuTimings.PassCount + index];
            if (time >= 0)
            {
                _timings.RecordGame(index, time);
                game = Math.Max(game, 0) + time;
            }
        }

        if (game >= 0)
        {
            _timings.Record(GpuPass.Game, game);
        }
    }

    /// <summary>
    /// Each pass's time from a slot's query results (<paramref name="results"/>: for each pass its begin and end
    /// timestamps, each followed by its availability word), into <paramref name="nanoseconds"/>: -1 for a pass whose
    /// begin or end was not written (it did not run). Timestamps count only their <paramref name="validMask"/> bits, so a
    /// counter that wrapped between the two still gives the time between. Allocates nothing.
    /// </summary>
    internal static void Parse(
        ReadOnlySpan<ulong> results,
        ulong validMask,
        double nanosecondsPerTick,
        Span<long> nanoseconds
    )
    {
        for (var pass = 0; pass < nanoseconds.Length; pass++)
        {
            var begin = pass * 4;
            var end = begin + 2;
            if (results[begin + 1] == 0 || results[end + 1] == 0)
            {
                nanoseconds[pass] = -1;
                continue;
            }

            var ticks = (results[end] - results[begin]) & validMask;
            nanoseconds[pass] = (long)(ticks * nanosecondsPerTick);
        }
    }

    /// <summary>Resets the slot's queries. Record first in the command buffer, outside any rendering.</summary>
    public void Reset(CommandBuffer commandBuffer, int slot)
    {
        if (Enabled)
        {
            _context.Vk.CmdResetQueryPool(commandBuffer, _pool, (uint)(slot * QueriesPerSlot), QueriesPerSlot);
        }
    }

    /// <summary>Writes the timestamp before <paramref name="pass"/>, once every earlier command has finished.</summary>
    public void Begin(CommandBuffer commandBuffer, int slot, GpuPass pass) => Write(commandBuffer, slot, pass, 0);

    /// <summary>Writes the timestamp after <paramref name="pass"/>, once every earlier command has finished.</summary>
    public void End(CommandBuffer commandBuffer, int slot, GpuPass pass) => Write(commandBuffer, slot, pass, 1);

    /// <summary>Writes the timestamp before the game's pass <paramref name="index"/> (below <see cref="GpuTimings.MostGamePasses"/>).</summary>
    public void BeginGame(CommandBuffer commandBuffer, int slot, int index) =>
        Write(commandBuffer, slot, (GpuPass)(GpuTimings.PassCount + index), 0);

    /// <summary>Writes the timestamp after the game's pass <paramref name="index"/>.</summary>
    public void EndGame(CommandBuffer commandBuffer, int slot, int index) =>
        Write(commandBuffer, slot, (GpuPass)(GpuTimings.PassCount + index), 1);

    /// <summary>Marks the slot's queries as written by a submitted command buffer, so the next collect reads them.</summary>
    public void OnSubmitted(int slot)
    {
        if (Enabled)
        {
            _written[slot] = true;
        }
    }

    /// <summary>Destroys the query pool. The GPU must be done with it. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_context.IsAbandoned && _pool.Handle != 0)
        {
            _context.Vk.DestroyQueryPool(_context.Device, _pool, null);
        }

        _pool = default;
    }

    private void Write(CommandBuffer commandBuffer, int slot, GpuPass pass, int end)
    {
        if (Enabled)
        {
            var query = (uint)(slot * QueriesPerSlot + (int)pass * 2 + end);
            // All commands: the timestamp waits for every earlier command, so a pass's time excludes earlier work.
            _context.Vk.CmdWriteTimestamp2(commandBuffer, PipelineStageFlags2.AllCommandsBit, _pool, query);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "GPU timing unavailable: the graphics queue writes no timestamps"
    )]
    private partial void LogUnavailable();
}
