using System.Numerics;

namespace Game.Components;

/// <summary>
/// Where the fox is in its round (<c>FoxSystem</c>): the centre of the circle it walks and runs round, its angle about
/// that centre (radians, from +x towards +z), and the seconds into its cycle of looking about, walking, and running.
/// </summary>
public struct FoxRoute
{
    public Vector3 Centre;
    public float Angle;
    public float Seconds;
}
