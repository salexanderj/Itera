using Arch.Core;
using Arch.System;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Itera.Engine.Tests.Matches;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Ecs;

/// <summary>
/// <see cref="SystemRegistration"/>: a system registered by type, in a host's match, takes the match's world, a service
/// the match has, an optional one it has, and the host's role, while an optional one it lacks stays null; it runs in its
/// registration's place among the factories' systems, and resolves as itself and as its group's system alike; a system
/// that takes any other value is refused at registration.
/// </summary>
public sealed class SystemRegistrationTests
{
    [Fact]
    public void A_system_by_type_takes_the_matchs_services_and_the_sessions_role_in_its_registrations_place()
    {
        using var provider = EngineComposition.Build(
            terrain: false,
            configure: services =>
            {
                services.AddSingleton<ISessionTransports>(new Transports(new FakeTransport(new FakeNetwork())));
                services.AddScoped<Needed>();
                services.AddFixed<ByType>();
                services.AddFrame<FrameByType>();
            }
        );
        using var match = new HeadlessRun(provider).Host(seed: 1);

        var fixedSystems = match.Services.GetServices<IFixedUpdateSystem>().ToArray();
        var system = match.Services.GetRequiredService<ByType>();

        Assert.Equal(["StepCounter", "ByType"], fixedSystems.Select(s => s.GetType().Name));
        Assert.Same(system, fixedSystems[1]);
        Assert.Same(match.World, system.World);
        Assert.Same(match.Services.GetRequiredService<Needed>(), system.Needed);
        Assert.Same(match.Services.GetRequiredService<WorldOrigin>(), system.Origin);
        Assert.Null(system.Missing);
        Assert.Equal(SessionRole.Host, system.Role);
        Assert.Equal(
            ["FrameCounter", "FrameByType"],
            match.Services.GetServices<IFrameUpdateSystem>().Select(s => s.GetType().Name)
        );
    }

    [Fact]
    public void A_system_that_takes_another_value_is_refused_at_its_registration()
    {
        var services = new ServiceCollection();

        var refused = Assert.Throws<InvalidOperationException>(() => services.AddFixed<TakesSeconds>());

        Assert.Contains("TakesSeconds takes seconds", refused.Message, StringComparison.Ordinal);
    }

    private sealed class Needed;

    private sealed class Unregistered;

    private sealed class ByType(
        World world,
        Needed needed,
        WorldOrigin? origin = null,
        Unregistered? missing = null,
        SessionRole role = SessionRole.Offline
    ) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
    {
        public Needed Needed { get; } = needed;

        public WorldOrigin? Origin { get; } = origin;

        public Unregistered? Missing { get; } = missing;

        public SessionRole Role { get; } = role;
    }

    private sealed class FrameByType(World world) : BaseSystem<World, FrameTime>(world), IFrameUpdateSystem;

    private sealed class TakesSeconds(World world, float seconds = 1)
        : BaseSystem<World, FixedStepTime>(world),
            IFixedUpdateSystem
    {
        public float Seconds { get; } = seconds;
    }

    private sealed class Transports(ISessionTransport transport) : ISessionTransports
    {
        public ISessionTransport Transport(SessionTransportKind kind) => transport;
    }
}
