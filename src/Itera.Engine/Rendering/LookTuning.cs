using System.Numerics;

namespace Itera.Engine.Rendering;

/// <summary>
/// One value of <see cref="RenderLook"/> by name, for the console's <c>look</c>: a number (one component, in X) or a
/// colour (three), read and set without reflection.
/// </summary>
public sealed record LookField(
    string Name,
    int Components,
    Func<RenderLook, Vector3> Get,
    Func<RenderLook, Vector3, RenderLook> Set
);

/// <summary>
/// The console's look tuning (<c>look</c>): values of <see cref="RenderLook"/> set by name for the rest of the run, laid
/// over every frame's look (<see cref="RenderView.DrawnLook"/>), so a game's look can be tried live and the values
/// copied into its code. <see cref="Fields"/> names every value. Main thread.
/// </summary>
public sealed class LookTuning
{
    private readonly Vector3[] _values = new Vector3[Fields.Count];
    private readonly bool[] _set = new bool[Fields.Count];

    /// <summary>Every value of <see cref="RenderLook"/>, in its order.</summary>
    public static IReadOnlyList<LookField> Fields { get; } =
    [
        Number("WorldSaturation", static l => l.WorldSaturation, static (l, v) => l with { WorldSaturation = v }),
        Number("WorldValue", static l => l.WorldValue, static (l, v) => l with { WorldValue = v }),
        Number("WorldSideShade", static l => l.WorldSideShade, static (l, v) => l with { WorldSideShade = v }),
        Number("WorldTopShade", static l => l.WorldTopShade, static (l, v) => l with { WorldTopShade = v }),
        Number("SideShadeBelow", static l => l.SideShadeBelow, static (l, v) => l with { SideShadeBelow = v }),
        Number("TopShadeAbove", static l => l.TopShadeAbove, static (l, v) => l with { TopShadeAbove = v }),
        Colour("SunWarmth", static l => l.SunWarmth, static (l, v) => l with { SunWarmth = v }),
        Colour("ShadeCool", static l => l.ShadeCool, static (l, v) => l with { ShadeCool = v }),
        Colour("SkyLightShare", static l => l.SkyLightShare, static (l, v) => l with { SkyLightShare = v }),
        Colour("GroundLight", static l => l.GroundLight, static (l, v) => l with { GroundLight = v }),
        Number("GroundBounce", static l => l.GroundBounce, static (l, v) => l with { GroundBounce = v }),
        Number("OcclusionFloor", static l => l.OcclusionFloor, static (l, v) => l with { OcclusionFloor = v }),
        Number("SkyFloor", static l => l.SkyFloor, static (l, v) => l with { SkyFloor = v }),
        Number("GlowStrength", static l => l.GlowStrength, static (l, v) => l with { GlowStrength = v }),
        Number("FogStart", static l => l.FogStart, static (l, v) => l with { FogStart = v }),
        Number("HazeHalfMetres", static l => l.HazeHalfMetres, static (l, v) => l with { HazeHalfMetres = v }),
        Colour("ZenithClear", static l => l.ZenithClear, static (l, v) => l with { ZenithClear = v }),
        Colour("ZenithOvercast", static l => l.ZenithOvercast, static (l, v) => l with { ZenithOvercast = v }),
        Number("BelowHorizon", static l => l.BelowHorizon, static (l, v) => l with { BelowHorizon = v }),
        Number("SunDiscSharpness", static l => l.SunDiscSharpness, static (l, v) => l with { SunDiscSharpness = v }),
        Number("SunDiscStrength", static l => l.SunDiscStrength, static (l, v) => l with { SunDiscStrength = v }),
        Number("SunDiscOvercast", static l => l.SunDiscOvercast, static (l, v) => l with { SunDiscOvercast = v }),
        Number("Exposure", static l => l.Exposure, static (l, v) => l with { Exposure = v }),
        Number("GradeDesaturate", static l => l.GradeDesaturate, static (l, v) => l with { GradeDesaturate = v }),
        Number("GradeDarken", static l => l.GradeDarken, static (l, v) => l with { GradeDarken = v }),
        Colour("GradeShadows", static l => l.GradeShadows, static (l, v) => l with { GradeShadows = v }),
        Colour("GradeLights", static l => l.GradeLights, static (l, v) => l with { GradeLights = v }),
        Number("ShadowsBelow", static l => l.ShadowsBelow, static (l, v) => l with { ShadowsBelow = v }),
        Number("LightsAbove", static l => l.LightsAbove, static (l, v) => l with { LightsAbove = v }),
    ];

    /// <summary>How many values are set.</summary>
    public int Count { get; private set; }

    /// <summary>The index in <see cref="Fields"/> of the value named <paramref name="name"/> (any case), or -1.</summary>
    public static int Find(string name)
    {
        for (var i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>True when field <paramref name="field"/> is set.</summary>
    public bool IsSet(int field) => _set[field];

    /// <summary>Sets field <paramref name="field"/> to <paramref name="value"/> (a number's in X) for the rest of the run.</summary>
    public void Set(int field, Vector3 value)
    {
        if (!_set[field])
        {
            _set[field] = true;
            Count++;
        }

        _values[field] = value;
    }

    /// <summary>Sets nothing: every frame's look is drawn as given.</summary>
    public void Reset()
    {
        Array.Clear(_set);
        Count = 0;
    }

    /// <summary><paramref name="look"/> with the values set over it. Allocates nothing.</summary>
    public RenderLook Apply(in RenderLook look)
    {
        var result = look;
        if (Count == 0)
        {
            return result;
        }

        for (var i = 0; i < _set.Length; i++)
        {
            if (_set[i])
            {
                result = Fields[i].Set(result, _values[i]);
            }
        }

        return result;
    }

    private static LookField Number(
        string name,
        Func<RenderLook, float> get,
        Func<RenderLook, float, RenderLook> set
    ) => new(name, 1, look => new Vector3(get(look), 0, 0), (look, value) => set(look, value.X));

    private static LookField Colour(
        string name,
        Func<RenderLook, Vector3> get,
        Func<RenderLook, Vector3, RenderLook> set
    ) => new(name, 3, get, set);
}
