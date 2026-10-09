using System.Numerics;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Rendering;

/// <summary>
/// Lines drawn over the picture, such as the outlines of loose bodies. The console's <c>lines</c> turns
/// <see cref="Enabled"/> on and off, which the debug outlines and the collision cells follow; others add theirs
/// whenever they show (a tool's next edit, <c>nav</c>'s arrows). <see cref="GameLoop"/> clears the list at the start of each frame
/// and hands it to <see cref="DebugLinePass"/>, which hides them behind what the depth image holds. Holds at most
/// <see cref="MaxLines"/>; more are dropped, with a Warning once. Main thread only; allocates nothing.
/// </summary>
public sealed partial class DebugLines
{
    /// <summary>The most lines in one frame.</summary>
    public const int MaxLines = 16_384;

    private readonly DebugLineVertex[] _vertices = new DebugLineVertex[2 * MaxLines];
    private readonly ILogger<DebugLines> _logger;
    private int _count;
    private bool _warned;

    public DebugLines(ILogger<DebugLines> logger, bool enabled = false)
    {
        _logger = logger;
        Enabled = enabled;
    }

    /// <summary>Whether lines are drawn; frame systems add none while this is false.</summary>
    public bool Enabled { get; set; }

    /// <summary>The frame's line ends so far, two per line.</summary>
    public ReadOnlySpan<DebugLineVertex> Vertices => _vertices.AsSpan(0, _count);

    /// <summary>A colour as four bytes, red lowest, as the line shader reads it.</summary>
    public static uint Colour(byte red, byte green, byte blue, byte alpha = 255) =>
        red | ((uint)green << 8) | ((uint)blue << 16) | ((uint)alpha << 24);

    /// <summary>Starts a frame with no lines.</summary>
    public void Clear() => _count = 0;

    /// <summary>Adds a line from <paramref name="from"/> to <paramref name="to"/>, in world metres.</summary>
    public void Add(Vector3 from, Vector3 to, uint colour)
    {
        if (_count + 2 > _vertices.Length)
        {
            if (!_warned)
            {
                _warned = true;
                LogFull(MaxLines);
            }

            return;
        }

        _vertices[_count++] = new DebugLineVertex { Position = from, Colour = colour };
        _vertices[_count++] = new DebugLineVertex { Position = to, Colour = colour };
    }

    /// <summary>
    /// Adds the 12 edges of a box from <paramref name="min"/> to <paramref name="max"/> in its own axes, turned by
    /// <paramref name="orientation"/> about its origin, which sits at <paramref name="origin"/>.
    /// </summary>
    public void AddBox(Vector3 origin, Quaternion orientation, Vector3 min, Vector3 max, uint colour)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var local = new Vector3(
                (i & 1) != 0 ? max.X : min.X,
                (i & 2) != 0 ? max.Y : min.Y,
                (i & 4) != 0 ? max.Z : min.Z
            );
            corners[i] = origin + Vector3.Transform(local, orientation);
        }

        // Each edge joins two corners that differ in one axis bit.
        for (var i = 0; i < 8; i++)
        {
            for (var bit = 1; bit < 8; bit <<= 1)
            {
                if ((i & bit) == 0)
                {
                    Add(corners[i], corners[i | bit], colour);
                }
            }
        }
    }

    /// <summary>Adds an upright cylinder standing on <paramref name="feet"/>: its top and bottom circles and four sides.</summary>
    public void AddCylinder(Vector3 feet, float radius, float height, uint colour)
    {
        const int Segments = 16;
        var top = new Vector3(0, height, 0);
        var previous = feet + new Vector3(radius, 0, 0);
        for (var i = 1; i <= Segments; i++)
        {
            var angle = i * (2 * MathF.PI / Segments);
            var next = feet + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
            Add(previous, next, colour);
            Add(previous + top, next + top, colour);
            if (i % (Segments / 4) == 0)
            {
                Add(next, next + top, colour);
            }

            previous = next;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Debug lines: more than {Max} in one frame; the rest are dropped"
    )]
    private partial void LogFull(int max);
}
