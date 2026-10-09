namespace Itera.Engine.Tests.Modding;

internal sealed class EmptyServiceProvider : IServiceProvider
{
    public static readonly EmptyServiceProvider Instance = new();

    public object? GetService(Type serviceType)
    {
        return null;
    }
}
