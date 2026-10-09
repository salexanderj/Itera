using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Itera.Engine;

/// <summary>A start-up step of the engine: a service's initialisation and the action that shuts it down.</summary>
/// <param name="Name">What the log calls it.</param>
/// <param name="Initialize">Starts it.</param>
/// <param name="Shutdown">Stops it; copes with a step whose initialisation failed part way.</param>
/// <param name="Graphics">True for a step the shutdown skips once the graphics are abandoned (a stuck render thread).</param>
internal sealed record EngineStep(string Name, Action Initialize, Action Shutdown, bool Graphics = false);

/// <summary>
/// Runs the engine's start-up steps (<c>GameLoop.CreateSteps</c> writes their order): <see cref="Initialize"/> walks them
/// forward; a step that throws stops the start there, and every step that started, the failing one included, is left
/// for <see cref="Shutdown"/>, which walks them back, skips graphics steps once <see cref="GraphicsAbandoned"/>, and goes
/// on past a step that throws (logged at Error). Main thread.
/// </summary>
internal sealed partial class EngineSteps
{
    private readonly IReadOnlyList<EngineStep> _steps;
    private readonly ILogger _logger;
    private int _started;

    /// <param name="steps">The steps in start-up order.</param>
    /// <param name="logger">The game loop's logger, so the lines keep its category.</param>
    public EngineSteps(IReadOnlyList<EngineStep> steps, ILogger logger)
    {
        _steps = steps;
        _logger = logger;
    }

    /// <summary>Steps whose initialisation started and that the next <see cref="Shutdown"/> shuts down.</summary>
    public int Started => _started;

    /// <summary>Set when the render thread could not be stopped: the shutdown then leaves the graphics steps alone.</summary>
    public bool GraphicsAbandoned { get; set; }

    /// <summary>
    /// Initialises every step in order, calling <paramref name="starting"/> with each one's name first (the phase the
    /// loop reports if it throws). Throws what a step throws.
    /// </summary>
    public void Initialize(Action<string> starting)
    {
        foreach (var step in _steps)
        {
            starting(step.Name);
            // Counted before the call: a step that fails half way still gets its shutdown action, and every
            // shutdown action copes with a partly initialised service.
            _started++;
            var stopwatch = Stopwatch.StartNew();
            step.Initialize();
            LogInitialised(step.Name, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Shuts down every step whose initialisation started, in reverse order, so the render thread stops first and the
    /// GPU is idle before anything it uses is destroyed. A step that never started is skipped; the DI container still
    /// disposes its service when the host is disposed. Doing it again does nothing.
    /// </summary>
    public void Shutdown()
    {
        for (var i = _started - 1; i >= 0; i--)
        {
            var step = _steps[i];
            if (GraphicsAbandoned && step.Graphics)
            {
                LogShutdownStepSkipped(step.Name);
                continue;
            }

            try
            {
                step.Shutdown();
                LogShutdownStep(step.Name);
            }
            catch (Exception ex)
            {
                LogShutdownStepFailed(ex, step.Name);
            }
        }

        _started = 0;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Initialised {Step} in {Ms:F1} ms")]
    private partial void LogInitialised(string step, double ms);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Shut down {Step}")]
    private partial void LogShutdownStep(string step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped shutting down {Step}: the render thread still uses it")]
    private partial void LogShutdownStepSkipped(string step);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Shutdown step {Step} failed; continuing with the remaining steps"
    )]
    private partial void LogShutdownStepFailed(Exception exception, string step);
}
