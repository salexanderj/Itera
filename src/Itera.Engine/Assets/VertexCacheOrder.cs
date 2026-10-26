namespace Itera.Engine.Assets;

/// <summary>
/// Orders a mesh's triangles and vertices for the GPU's vertex caches, at import (<see cref="GltfImport"/>). The
/// triangles follow Tom Forsyth's "Linear-Speed Vertex Cache Optimisation" (2006) with a cache of 32 and its published
/// scores, so a vertex the post-transform cache still holds is used again soon. The vertices then follow the order the
/// triangles first use them, so the vertex fetch and skinning read memory in order. What is drawn does not change: the
/// same triangles, each with its winding, over the same vertices.
/// <para>
/// The result is the same on every machine. The scores are whole numbers, made once from tables that use only
/// multiplication, division, and square roots of doubles, which IEEE 754 rounds the same everywhere; ties go to the
/// triangle first in the input. Allocates a few arrays a call, none a triangle.
/// </para>
/// </summary>
internal static class VertexCacheOrder
{
    /// <summary>The cache the triangle order is scored for, in vertices.</summary>
    public const int CacheSize = 32;

    /// <summary>One in a score's fixed point.</summary>
    private const int ScoreOne = 1 << 16;

    /// <summary>The remaining triangles past which a vertex's valence score stays the same.</summary>
    private const int MaxScoredValence = 255;

    /// <summary>Each cache position's score: 0.75 for the last triangle's three, then (1 - (p - 3) / 29)^1.5.</summary>
    private static readonly int[] s_cacheScores = CacheScores();

    /// <summary>Each count of remaining triangles' score: 2 / sqrt(n), which favours a vertex with few left to draw.</summary>
    private static readonly int[] s_valenceScores = ValenceScores();

    /// <summary>
    /// Orders <paramref name="indices"/> (three a triangle into <paramref name="vertexCount"/> vertices) in place for the
    /// vertex caches (<see cref="OrderTriangles"/>), then numbers the vertices in the order the triangles first use
    /// them (<see cref="OrderVertices"/>). Returns each new vertex's old number: the caller moves every attribute of
    /// vertex <c>order[i]</c> to <c>i</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The index count is not a multiple of 3, or an index is out of range.
    /// </exception>
    public static int[] Reorder(Span<uint> indices, int vertexCount)
    {
        OrderTriangles(indices, vertexCount);
        return OrderVertices(indices, vertexCount);
    }

    /// <summary>
    /// Orders the triangles of <paramref name="indices"/> in place by Forsyth's method: each step draws the triangle
    /// with the best score among those that use a vertex in the simulated cache, or, when none is left there, the first
    /// triangle not yet drawn in the input's order. Each triangle keeps its corners in their order.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The index count is not a multiple of 3, or an index is out of range.
    /// </exception>
    public static void OrderTriangles(Span<uint> indices, int vertexCount)
    {
        Check(indices, vertexCount);
        var triangleCount = indices.Length / 3;
        if (triangleCount < 2)
        {
            return;
        }

        // Each vertex's triangles not yet drawn: adjacent[starts[v] .. starts[v] + live[v]].
        var live = new int[vertexCount];
        foreach (var index in indices)
        {
            live[index]++;
        }

        var starts = new int[vertexCount + 1];
        for (var v = 0; v < vertexCount; v++)
        {
            starts[v + 1] = starts[v] + live[v];
        }

        var adjacent = new int[indices.Length];
        var filled = new int[vertexCount];
        for (var i = 0; i < indices.Length; i++)
        {
            var v = (int)indices[i];
            adjacent[starts[v] + filled[v]++] = i / 3;
        }

        var cacheScores = s_cacheScores;
        var valenceScores = s_valenceScores;
        var scores = new int[vertexCount];
        for (var v = 0; v < vertexCount; v++)
        {
            scores[v] = valenceScores[Math.Min(live[v], MaxScoredValence)];
        }

        // Each triangle's score, the sum of its corners', kept up to date as they change.
        var triangleScores = new int[triangleCount];
        var best = -1;
        var bestScore = -1;
        for (var t = 0; t < triangleCount; t++)
        {
            var score = scores[indices[t * 3]] + scores[indices[t * 3 + 1]] + scores[indices[t * 3 + 2]];
            triangleScores[t] = score;
            if (score > bestScore)
            {
                (best, bestScore) = (t, score);
            }
        }

        var drawn = new bool[triangleCount];
        var output = new uint[indices.Length];
        // The cache, newest first, and the next one built from it; three more for the corners pushed in.
        var cache = new int[CacheSize + 3];
        var next = new int[CacheSize + 3];
        var cached = 0;
        var firstUndrawn = 0;
        for (var written = 0; written < triangleCount; written++)
        {
            if (best < 0)
            {
                while (drawn[firstUndrawn])
                {
                    firstUndrawn++;
                }

                best = firstUndrawn;
            }

            drawn[best] = true;
            var a = (int)indices[best * 3];
            var b = (int)indices[best * 3 + 1];
            var c = (int)indices[best * 3 + 2];
            output[written * 3] = (uint)a;
            output[written * 3 + 1] = (uint)b;
            output[written * 3 + 2] = (uint)c;
            Remove(adjacent, starts, live, a, best);
            Remove(adjacent, starts, live, b, best);
            Remove(adjacent, starts, live, c, best);

            // The corners go to the front of the cache, the rest move back; past its end they fall out.
            var count = 0;
            next[count++] = a;
            if (b != a)
            {
                next[count++] = b;
            }

            if (c != a && c != b)
            {
                next[count++] = c;
            }

            for (var i = 0; i < cached; i++)
            {
                var v = cache[i];
                if (v != a && v != b && v != c)
                {
                    next[count++] = v;
                }
            }

            for (var i = 0; i < count; i++)
            {
                var v = next[i];
                var left = live[v];
                var score =
                    left == 0
                        ? 0
                        : (i < CacheSize ? cacheScores[i] : 0) + valenceScores[Math.Min(left, MaxScoredValence)];
                var change = score - scores[v];
                scores[v] = score;
                if (change != 0)
                {
                    for (var k = starts[v]; k < starts[v] + left; k++)
                    {
                        triangleScores[adjacent[k]] += change;
                    }
                }
            }

            // The next triangle: the best among those of the vertices just scored, the first in the input on a tie.
            best = -1;
            bestScore = -1;
            for (var i = 0; i < count; i++)
            {
                var v = next[i];
                for (var k = starts[v]; k < starts[v] + live[v]; k++)
                {
                    var t = adjacent[k];
                    var score = triangleScores[t];
                    if (score > bestScore || (score == bestScore && t < best))
                    {
                        (best, bestScore) = (t, score);
                    }
                }
            }

            (cache, next) = (next, cache);
            cached = Math.Min(count, CacheSize);
        }

        output.CopyTo(indices);
    }

    /// <summary>
    /// Numbers the vertices in the order <paramref name="indices"/> first uses them and rewrites the indices to match;
    /// vertices no triangle uses come last, in their order. Returns each new vertex's old number.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The index count is not a multiple of 3, or an index is out of range.
    /// </exception>
    public static int[] OrderVertices(Span<uint> indices, int vertexCount)
    {
        Check(indices, vertexCount);
        var newOf = new int[vertexCount];
        Array.Fill(newOf, -1);
        var order = new int[vertexCount];
        var numbered = 0;
        for (var i = 0; i < indices.Length; i++)
        {
            var old = (int)indices[i];
            if (newOf[old] < 0)
            {
                newOf[old] = numbered;
                order[numbered++] = old;
            }

            indices[i] = (uint)newOf[old];
        }

        for (var v = 0; v < vertexCount; v++)
        {
            if (newOf[v] < 0)
            {
                order[numbered++] = v;
            }
        }

        return order;
    }

    private static void Check(ReadOnlySpan<uint> indices, int vertexCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException("The index count must be a multiple of 3.", nameof(indices));
        }

        foreach (var index in indices)
        {
            if (index >= (uint)vertexCount)
            {
                throw new ArgumentException(
                    $"Index {index} is out of range for {vertexCount} vertices.",
                    nameof(indices)
                );
            }
        }
    }

    /// <summary>Takes triangle <paramref name="triangle"/> out of vertex <paramref name="vertex"/>'s triangles not yet drawn.</summary>
    private static void Remove(int[] adjacent, int[] starts, int[] live, int vertex, int triangle)
    {
        var start = starts[vertex];
        var last = start + live[vertex] - 1;
        for (var k = start; k <= last; k++)
        {
            if (adjacent[k] == triangle)
            {
                adjacent[k] = adjacent[last];
                live[vertex]--;
                return;
            }
        }
    }

    private static int[] CacheScores()
    {
        var scores = new int[CacheSize];
        for (var p = 0; p < CacheSize; p++)
        {
            double score;
            if (p < 3)
            {
                score = 0.75;
            }
            else
            {
                var x = 1.0 - (p - 3) / (double)(CacheSize - 3);
                score = x * Math.Sqrt(x);
            }

            scores[p] = (int)(score * ScoreOne + 0.5);
        }

        return scores;
    }

    private static int[] ValenceScores()
    {
        var scores = new int[MaxScoredValence + 1];
        for (var n = 1; n <= MaxScoredValence; n++)
        {
            scores[n] = (int)(2.0 / Math.Sqrt(n) * ScoreOne + 0.5);
        }

        return scores;
    }
}
