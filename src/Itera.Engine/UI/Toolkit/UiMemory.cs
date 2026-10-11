namespace Itera.Engine.UI.Toolkit;

/// <summary>What a <see cref="Ui"/> keeps for an id across layouts: a scroll area's offset and a tween's run.</summary>
internal struct UiMemory
{
    public bool Scrolls;
    public float Scroll;
    public bool Tweens;
    public float From;
    public float Target;
    public double Start;
    public float Seconds;
}

/// <summary>An element of the last layout as the input is matched against it: in the order it is drawn.</summary>
internal struct UiHit
{
    public int Index;
    public int Id;
    public UiKind Kind;
    public UiMarks Marks;
    public SkiaSharp.SKRect Rect;
    public SkiaSharp.SKRect Clip;
    public int Depth;
    public float Least;
    public float Most;

    /// <summary>The pointer stops at it (<see cref="UiBox.Blocks"/>).</summary>
    public bool Blocks;

    /// <summary>A scroll area's content height, the height it shows, and how far it is scrolled, in pixels.</summary>
    public float Content;
    public float View;
    public float Offset;
}
