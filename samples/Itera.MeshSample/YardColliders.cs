namespace Itera.MeshSample;

/// <summary>
/// Which yard piece each of the match's mesh colliders is (<see cref="Systems.YardSystem"/> adds them), so a ray's hit
/// (<c>SurfaceHit.Collider</c>) names its piece. Match-scoped; allocates nothing once filled.
/// </summary>
public sealed class YardColliders
{
    private readonly int[] _colliders = new int[Yard.Pieces.Count];
    private readonly YardPiece[] _pieces = new YardPiece[Yard.Pieces.Count];
    private int _count;

    /// <summary>Records that <paramref name="collider"/> is <paramref name="piece"/>.</summary>
    public void Add(int collider, YardPiece piece)
    {
        if (_count == _colliders.Length)
        {
            throw new InvalidOperationException("Every yard piece has its collider already.");
        }

        _colliders[_count] = collider;
        _pieces[_count++] = piece;
    }

    /// <summary>The piece <paramref name="collider"/> is, when it is one of the yard's.</summary>
    public bool TryGet(int collider, out YardPiece piece)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_colliders[i] == collider)
            {
                piece = _pieces[i];
                return true;
            }
        }

        piece = default;
        return false;
    }
}
