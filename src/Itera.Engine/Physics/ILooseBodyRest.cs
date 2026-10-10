using System.Numerics;
using BepuPhysics;

namespace Itera.Engine.Physics;

/// <summary>
/// What a game makes of a loose body (a thrown ball; a body cut off a character goes after resting) that has rested for
/// <c>Engine:Physics:SettleSeconds</c> in a match without voxel terrain, where there is no grid to settle it into.
/// <c>LooseBodySystem</c> asks the one a game registers in the match's scope. Without one, the body stays where it
/// rests, as with <see cref="LooseBodyRestChoice.Keep"/>; it is asked again once it has moved and rested again. Past
/// <c>Engine:Physics:MaxLooseBodies</c> the oldest bodies go as ever (<c>LooseBodyRoom</c>). Main thread, inside the
/// system's query of loose bodies: it may create entities, but records any add, remove, or destroy of components or
/// entities in a command buffer of its own and plays it back later (in a system of its own).
/// </summary>
public interface ILooseBodyRest
{
    /// <summary>What becomes of a body that rests as <paramref name="body"/> tells.</summary>
    LooseBodyRestChoice Rested(in RestedBody body);
}

/// <summary>What a game makes of a rested loose body (<see cref="ILooseBodyRest"/>).</summary>
public enum LooseBodyRestChoice : byte
{
    /// <summary>The body stays as it is.</summary>
    Keep = 0,

    /// <summary>The engine removes the body and its model (<see cref="LooseBodies.Remove"/>).</summary>
    Remove = 1,

    /// <summary>
    /// The engine removes the body and leaves its model to the game (<see cref="LooseBodies.Release"/>): the game draws
    /// it where it lay (<see cref="RestedBody.ModelCorner"/> and <see cref="RestedBody.Orientation"/>, through
    /// <c>ModelInstances</c>; voxel models are not drawn without terrain yet) or makes something else of it, and removes
    /// it (<c>VoxelModels.Remove</c>) when done; never frees its tree (<c>VoxelModels.Free</c> or <c>RemoveAndFree</c>),
    /// which every ball of its radius and material shares.
    /// </summary>
    TakeOver = 2,
}

/// <summary>A loose body at rest (<see cref="LooseBodies.Rested"/>).</summary>
/// <param name="Body">The Bepu body.</param>
/// <param name="Model">The voxel model that draws it (<c>VoxelModels</c>), at level 0.</param>
/// <param name="ModelCorner">Where the model's box's lowest corner lies, in metres.</param>
/// <param name="Orientation">The body's turn, about that corner as the model is drawn.</param>
/// <param name="PivotMetres">Where its piece's pivot lies, in metres: a ball's middle.</param>
/// <param name="BallRadius">A thrown ball's radius in centimetres; 0 for any other body.</param>
/// <param name="BallMaterial">A thrown ball's material.</param>
/// <param name="Mass">Its mass in kilograms.</param>
public readonly record struct RestedBody(
    BodyHandle Body,
    int Model,
    Vector3 ModelCorner,
    Quaternion Orientation,
    Vector3 PivotMetres,
    int BallRadius,
    byte BallMaterial,
    float Mass
);
