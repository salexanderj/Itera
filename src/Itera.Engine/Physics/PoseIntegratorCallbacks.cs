using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuUtilities;

namespace Itera.Engine.Physics;

/// <summary>
/// What the integrator applies to every dynamic body, which a game may change between steps (<see cref="PhysicsWorld"/>):
/// gravity, the shares of speed and spin a body loses each second, and the solver's substeps. Written on the main
/// thread between steps; read by Bepu as it integrates.
/// </summary>
internal sealed class IntegrationSettings
{
    /// <summary>Gravity, in metres per second squared.</summary>
    public Vector3 Gravity;

    /// <summary>The share of its linear velocity a body loses each second.</summary>
    public float LinearDamping;

    /// <summary>The share of its angular velocity a body loses each second.</summary>
    public float AngularDamping;

    /// <summary>The solver's substeps a step: above 1, bodies are integrated over durations of their own.</summary>
    public int Substeps = 1;
}

/// <summary>
/// Applies gravity and damping to every dynamic body, from <see cref="IntegrationSettings"/>. With one substep every body
/// is integrated once over the step, and the step's values are worked out once (<see cref="PrepareForIntegration"/>);
/// with more, Bepu integrates a body with constraints once a substep and one without once over the step, so each lane's
/// own duration is used.
/// </summary>
internal struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    private readonly IntegrationSettings _settings;
    private float _preparedDt;
    private bool _substepped;
    private Vector3Wide _gravityWide;
    private Vector3Wide _gravityWideDt;
    private Vector<float> _linearDampingDt;
    private Vector<float> _angularDampingDt;
    private Vector<float> _linearDampingLog;
    private Vector<float> _angularDampingLog;

    public PoseIntegratorCallbacks(IntegrationSettings settings)
    {
        _settings = settings;
        _preparedDt = 0;
        _substepped = false;
        _gravityWide = default;
        _gravityWideDt = default;
        _linearDampingDt = default;
        _angularDampingDt = default;
        _linearDampingLog = default;
        _angularDampingLog = default;
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

    public readonly bool AllowSubstepsForUnconstrainedBodies => false;

    public readonly bool IntegrateVelocityForKinematics => false;

    public readonly void Initialize(Simulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        var settings = _settings;
        var linear = MathHelper.Clamp(1 - settings.LinearDamping, 0, 1);
        var angular = MathHelper.Clamp(1 - settings.AngularDamping, 0, 1);
        _preparedDt = dt;
        _substepped = settings.Substeps > 1;
        _linearDampingDt = new Vector<float>(MathF.Pow(linear, dt));
        _angularDampingDt = new Vector<float>(MathF.Pow(angular, dt));
        _gravityWideDt = Vector3Wide.Broadcast(settings.Gravity * dt);
        _gravityWide = Vector3Wide.Broadcast(settings.Gravity);
        // For durations of a lane's own: d^t = e^(t ln d); a damping of 1 stops the body (ln 0 is -infinity, e^-inf 0).
        _linearDampingLog = new Vector<float>(MathF.Log(linear));
        _angularDampingLog = new Vector<float>(MathF.Log(angular));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void IntegrateVelocity(
        Vector<int> bodyIndices,
        Vector3Wide position,
        QuaternionWide orientation,
        BodyInertiaWide localInertia,
        Vector<int> integrationMask,
        int workerIndex,
        Vector<float> dt,
        ref BodyVelocityWide velocity
    )
    {
        if (!_substepped)
        {
            velocity.Linear = (velocity.Linear + _gravityWideDt) * _linearDampingDt;
            velocity.Angular *= _angularDampingDt;
            return;
        }

        Vector3Wide.Scale(_gravityWide, dt, out var gravityDt);
        velocity.Linear = (velocity.Linear + gravityDt) * Vector.Exp(_linearDampingLog * dt);
        velocity.Angular *= Vector.Exp(_angularDampingLog * dt);
    }
}
