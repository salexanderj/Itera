namespace Itera.Engine.Rendering.Shaders;

/// <summary>
/// A pass whose pipelines can be made again from its shaders compiled afresh, so a shader change shows in a running game
/// (<see cref="ShaderReloads"/>, the console's <c>shaders reload</c>). Every pass compiles first; only when all of them
/// compiled do they replace their pipelines, so a shader with an error leaves the old pipelines drawing. Render thread.
/// </summary>
public interface IReloadsShaders
{
    /// <summary>
    /// Compiles every shader the pass uses and returns them for <see cref="ReplacePipelines"/>. Changes nothing; throws
    /// <see cref="ShaderCompilationException"/> on an error.
    /// </summary>
    object CompileShaders();

    /// <summary>
    /// Destroys the pass's pipelines and makes them again from <paramref name="compiled"/>, what
    /// <see cref="CompileShaders"/> returned. Layouts, descriptor sets, and images stay. Device idle. Returns how many
    /// pipelines it made.
    /// </summary>
    int ReplacePipelines(object compiled);
}
