using System.Numerics;
using BepuPhysics;

namespace Itera.Engine.Components;

/// <summary>
/// A loose body: a ball or piece that Bepu moves until it rests, when it goes back into the grid, or a body that never
/// does (<see cref="CutPiece"/>). <c>LooseBodies</c> owns the Bepu body and what it is made of; the entity holds its
/// handle.
/// </summary>
public struct LooseBody
{
    /// <summary>The Bepu body. Removed from the simulation before the entity is destroyed.</summary>
    public BodyHandle Body;

    /// <summary>
    /// The body's birth stamp (<c>LooseBodies.BornOf</c>), which no other body shares: Bepu gives a removed body's handle
    /// to the next, so a handle whose body has another stamp is not this entity's (<c>PhysicsReadSystem</c> checks it).
    /// 0 checks the handle only.
    /// </summary>
    public long Born;

    /// <summary>A ball's radius in centimetres (<c>LooseBodies.BallRadiusOf</c>); 0 for any other body.</summary>
    public int BallRadius;

    /// <summary>Fixed steps in a row that the body has been at rest.</summary>
    public int RestTicks;

    /// <summary>Fixed steps since a <see cref="CutPiece"/> was made (<c>LooseBodySystem</c> counts them).</summary>
    public int AgeTicks;
}

/// <summary>
/// Where a loose body's piece is after the last two physics steps, for drawing between them: its pivot and its
/// orientation (the piece's own axes were the world's when it was made).
/// </summary>
public struct BodyPose
{
    public Vector3 Position;
    public Vector3 PreviousPosition;
    public Quaternion Orientation;
    public Quaternion PreviousOrientation;
}

/// <summary>The box of a loose body's piece around its pivot, in the piece's own axes, in metres.</summary>
public struct BodyBox
{
    public Vector3 Min;
    public Vector3 Max;
}

/// <summary>
/// A loose body that never goes back into the grid (for example the parts of a model cut apart, joined as a ragdoll):
/// <c>LooseBodySystem</c> removes it some seconds after it comes to rest, or when it is old enough.
/// </summary>
public struct CutPiece;

/// <summary>
/// A session client's copy of a loose body its host simulates: its pose comes from the host's messages, not from Bepu,
/// and it goes when the host says. Systems that rest, settle, or remove bodies leave it alone.
/// </summary>
public struct RemoteBody;

/// <summary>
/// A character's stand-in: an upright Bepu cylinder of its size and mass (<c>PhysicsWorld.AddStandIn</c>) that exists
/// while a loose body is near. It is placed at the character every step, pushes loose bodies, and is pushed by them.
/// </summary>
public struct CharacterStandIn
{
    /// <summary>The Bepu body, while <see cref="Present"/>.</summary>
    public BodyHandle Body;

    /// <summary>True while the stand-in exists.</summary>
    public bool Present;

    /// <summary>The character's height the stand-in was made for, in metres; another height makes it again.</summary>
    public float HeightMetres;

    /// <summary>The velocity the stand-in was placed with before the last physics step, in metres per second.</summary>
    public Vector3 Placed;

    /// <summary>The change of velocity loose bodies gave the stand-in in the last step, in metres per second.</summary>
    public Vector3 Push;

    /// <summary>The size of that push times the character's mass, in newton seconds: how hard the character was hit.</summary>
    public float Impulse;
}
