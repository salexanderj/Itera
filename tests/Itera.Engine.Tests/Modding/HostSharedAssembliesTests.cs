using Itera.Engine.Modding;

namespace Itera.Engine.Tests.Modding;

public sealed class HostSharedAssembliesTests
{
    [Fact]
    public void Host_set_includes_framework_and_itera_assemblies()
    {
        var shared = HostSharedAssemblies.FromHost();

        Assert.True(shared.Contains("System.Runtime"));
        Assert.True(shared.Contains("Itera.Modding"));
        Assert.True(shared.Contains("Itera.Engine"));
        Assert.True(shared.Contains("itera.modding"), "lookup must ignore case");
        Assert.False(shared.Contains("Some.Mod.Private.Dependency"));
    }
}
