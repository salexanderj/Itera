using System.Numerics;

namespace Itera.Engine.Rendering;

/// <summary>A point light of a frame: where, its linear colour, and its power (what it gives a surface facing it 1 m away).</summary>
public readonly record struct PointLight(Vector3 Position, Vector3 Colour, float Power);

/// <summary>
/// The lights of events in the frame being built (docs/rendering-redesign.md, phase 3, lights): frame systems
/// <see cref="Add"/> a light that shines while something happens (a beam firing: added each frame it fires) and
/// <see cref="Flash"/> one that fades by itself (a muzzle flash, a blast: its power falls to nothing over its seconds).
/// <see cref="Frame"/> gives the frame's lights to <c>MeshView.End</c>, which ranks them with the glowing blocks' under
/// the light budget; <c>GameLoop</c> starts each frame (<see cref="BeginFrame"/>) before the frame's systems run and
/// clears the flashes when a match ends. Main thread; allocates nothing.
/// </summary>
public sealed class PointLights
{
    /// <summary>Lights added in a frame, and flashes alive at once; more are dropped (<see cref="Dropped"/>).</summary>
    public const int MostLights = 256;

    private readonly PointLight[] _added = new PointLight[MostLights];
    private readonly Flaring[] _flashes = new Flaring[MostLights];
    private readonly PointLight[] _frame = new PointLight[2 * MostLights];
    private int _addedCount;
    private int _flashCount;
    private double _now;

    /// <summary>Lights and flashes dropped since the run began because the lists were full.</summary>
    public long Dropped { get; private set; }

    /// <summary>
    /// Starts the frame at <paramref name="nowSeconds"/>: the last frame's added lights go, and the flashes that have
    /// faded out.
    /// </summary>
    public void BeginFrame(double nowSeconds)
    {
        _now = nowSeconds;
        _addedCount = 0;
        var kept = 0;
        for (var i = 0; i < _flashCount; i++)
        {
            if (_flashes[i].End > nowSeconds)
            {
                _flashes[kept++] = _flashes[i];
            }
        }

        _flashCount = kept;
    }

    /// <summary>A light that shines this frame.</summary>
    public void Add(Vector3 position, Vector3 colour, float power)
    {
        if (_addedCount == MostLights || power <= 0)
        {
            Dropped += power > 0 ? 1 : 0;
            return;
        }

        _added[_addedCount++] = new PointLight(position, colour, power);
    }

    /// <summary>
    /// A light that flashes at <paramref name="power"/> and fades to nothing over <paramref name="seconds"/>, its power
    /// falling as the square of the time left (a quick drop, a short glow).
    /// </summary>
    public void Flash(Vector3 position, Vector3 colour, float power, float seconds)
    {
        if (_flashCount == MostLights || power <= 0 || seconds <= 0)
        {
            Dropped += power > 0 && seconds > 0 ? 1 : 0;
            return;
        }

        _flashes[_flashCount++] = new Flaring(new PointLight(position, colour, power), _now, _now + seconds);
    }

    /// <summary>
    /// Moves the flashes still alive with the match's origin: each one's place less <paramref name="metres"/>
    /// (<c>OriginShifts</c>). The lights a frame adds are made again each frame.
    /// </summary>
    public void Shift(Vector3 metres)
    {
        for (var i = 0; i < _flashCount; i++)
        {
            var flash = _flashes[i];
            _flashes[i] = flash with { Light = flash.Light with { Position = flash.Light.Position - metres } };
        }
    }

    /// <summary>The frame's lights: those added, then the flashes alive at <paramref name="nowSeconds"/>, faded.</summary>
    public ReadOnlySpan<PointLight> Frame(double nowSeconds)
    {
        _added.AsSpan(0, _addedCount).CopyTo(_frame);
        var count = _addedCount;
        for (var i = 0; i < _flashCount; i++)
        {
            ref readonly var flash = ref _flashes[i];
            var left = (float)((flash.End - nowSeconds) / (flash.End - flash.Start));
            if (left <= 0)
            {
                continue;
            }

            var share = Math.Min(left, 1);
            _frame[count++] = flash.Light with { Power = flash.Light.Power * share * share };
        }

        return _frame.AsSpan(0, count);
    }

    /// <summary>Every light and flash goes (a match ended).</summary>
    public void Clear()
    {
        _addedCount = 0;
        _flashCount = 0;
    }

    private readonly record struct Flaring(PointLight Light, double Start, double End);
}
