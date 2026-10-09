using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuUtilities;

namespace Itera.Engine.Physics;

/// <summary>Applies gravity and simple damping to every dynamic body.</summary>
public struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    private readonly Vector3 _gravity;
    private readonly float _linearDamping;
    private readonly float _angularDamping;
    private Vector3Wide _gravityWideDt;
    private Vector<float> _linearDampingDt;
    private Vector<float> _angularDampingDt;

    /// <param name="gravity">Gravity in metres per second squared.</param>
    /// <param name="linearDamping">Fraction of linear velocity removed per second.</param>
    /// <param name="angularDamping">Fraction of angular velocity removed per second.</param>
    public PoseIntegratorCallbacks(Vector3 gravity, float linearDamping = 0.03f, float angularDamping = 0.03f)
    {
        _gravity = gravity;
        _linearDamping = linearDamping;
        _angularDamping = angularDamping;
        _gravityWideDt = default;
        _linearDampingDt = default;
        _angularDampingDt = default;
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

    public readonly bool AllowSubstepsForUnconstrainedBodies => false;

    public readonly bool IntegrateVelocityForKinematics => false;

    public readonly void Initialize(Simulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        _linearDampingDt = new Vector<float>(MathF.Pow(MathHelper.Clamp(1 - _linearDamping, 0, 1), dt));
        _angularDampingDt = new Vector<float>(MathF.Pow(MathHelper.Clamp(1 - _angularDamping, 0, 1), dt));
        _gravityWideDt = Vector3Wide.Broadcast(_gravity * dt);
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
        velocity.Linear = (velocity.Linear + _gravityWideDt) * _linearDampingDt;
        velocity.Angular *= _angularDampingDt;
    }
}
