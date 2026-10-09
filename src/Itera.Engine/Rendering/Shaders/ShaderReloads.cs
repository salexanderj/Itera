namespace Itera.Engine.Rendering.Shaders;

/// <summary>
/// One reload of the shaders asked for, then what came of it: the render thread compiles every pass's shaders from
/// <see cref="ShaderCompiler.SourceDirectory"/> and, when all compiled, makes the pipelines again
/// (<see cref="IReloadsShaders"/>). Its result is read once <see cref="IsDone"/> is true, from any thread.
/// </summary>
public sealed class ShaderReloadRequest
{
    private int _done;

    /// <summary>True once the pipelines are made again or the reload failed.</summary>
    public bool IsDone => Volatile.Read(ref _done) != 0;

    /// <summary>The compiler's message, or null when the reload succeeded.</summary>
    public string? Error { get; private set; }

    /// <summary>Pipelines made again.</summary>
    public int Pipelines { get; private set; }

    /// <summary>Milliseconds the reload held the render thread.</summary>
    public double Milliseconds { get; private set; }

    internal void Succeed(int pipelines, double milliseconds)
    {
        Pipelines = pipelines;
        Milliseconds = milliseconds;
        Volatile.Write(ref _done, 1);
    }

    internal void Fail(string error, double milliseconds)
    {
        if (IsDone)
        {
            return;
        }

        Error = error;
        Milliseconds = milliseconds;
        Volatile.Write(ref _done, 1);
    }
}

/// <summary>
/// Reloads of the shaders asked for by the console's <c>shaders reload</c> and the control port's <c>reload</c>: one at
/// a time, handed to the render thread in the next frame built (<see cref="TakeForFrame"/>). Main thread.
/// </summary>
public sealed class ShaderReloads
{
    private readonly ShaderCompiler _compiler;
    private ShaderReloadRequest? _pending;
    private ShaderReloadRequest? _last;

    public ShaderReloads(ShaderCompiler compiler)
    {
        _compiler = compiler;
    }

    /// <summary>The folder shaders are compiled from.</summary>
    public string SourceDirectory => _compiler.SourceDirectory;

    /// <summary>Queues <paramref name="request"/> for the next frame. Returns why not, or null.</summary>
    public string? Request(ShaderReloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_last is { IsDone: false })
        {
            return "a reload of the shaders is already on its way";
        }

        _pending = request;
        _last = request;
        return null;
    }

    /// <summary>The reload the frame being built makes, once; null when none waits.</summary>
    public ShaderReloadRequest? TakeForFrame()
    {
        var request = _pending;
        _pending = null;
        return request;
    }
}
