using System.Reflection;
using Itera.Engine.Networking;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Ecs;

/// <summary>
/// A game's systems registered by type, in its composition's order (the order the groups run them): each is made in the
/// match's scope with every constructor parameter the match has a service for, an optional one too, so none is left at
/// its null default by a factory that forgot it. A <see cref="SessionRole"/> parameter takes the session's role, never
/// its default. A constructor with any other value-type parameter is refused at registration: such a system takes a value
/// the composition works out, and keeps a factory.
/// </summary>
public static class SystemRegistration
{
    /// <summary>Registers fixed system <typeparamref name="T"/> by type (<see cref="SystemRegistration"/>).</summary>
    public static IServiceCollection AddFixed<T>(this IServiceCollection services)
        where T : class, IFixedUpdateSystem
    {
        ArgumentNullException.ThrowIfNull(services);
        var role = TakesRole(typeof(T));
        services.AddScoped(sp => Create<T>(sp, role));
        services.AddScoped<IFixedUpdateSystem>(sp => sp.GetRequiredService<T>());
        return services;
    }

    /// <summary>Registers frame system <typeparamref name="T"/> by type (<see cref="SystemRegistration"/>).</summary>
    public static IServiceCollection AddFrame<T>(this IServiceCollection services)
        where T : class, IFrameUpdateSystem
    {
        ArgumentNullException.ThrowIfNull(services);
        var role = TakesRole(typeof(T));
        services.AddScoped(sp => Create<T>(sp, role));
        services.AddScoped<IFrameUpdateSystem>(sp => sp.GetRequiredService<T>());
        return services;
    }

    /// <summary>
    /// True when <paramref name="system"/>'s constructor takes the session's role; throws when it takes any other value
    /// type, or has more than one public constructor.
    /// </summary>
    private static bool TakesRole(Type system)
    {
        var constructors = system.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        if (constructors.Length != 1)
        {
            throw new InvalidOperationException(
                $"{system.Name} has {constructors.Length} public constructors; a system registered by type has one."
            );
        }

        var role = false;
        foreach (var parameter in constructors[0].GetParameters())
        {
            var type = parameter.ParameterType;
            if (type == typeof(SessionRole))
            {
                role = true;
            }
            else if (type.IsValueType)
            {
                throw new InvalidOperationException(
                    $"{system.Name} takes {parameter.Name}, a {type.Name}, which no service gives: register it with a factory."
                );
            }
        }

        return role;
    }

    private static T Create<T>(IServiceProvider services, bool role) =>
        role
            ? ActivatorUtilities.CreateInstance<T>(services, services.GetRequiredService<SessionService>().Role)
            : ActivatorUtilities.CreateInstance<T>(services);
}
