using Itera.Engine.Diagnostics;

namespace Itera.Engine.Tests.Diagnostics;

public sealed class SessionInfoTests
{
    [Fact]
    public void Capture_fills_every_fact()
    {
        var info = SessionInfo.Capture();

        Assert.False(string.IsNullOrWhiteSpace(info.ProductVersion));
        Assert.False(string.IsNullOrWhiteSpace(info.BuildConfiguration));
        Assert.False(string.IsNullOrWhiteSpace(info.OperatingSystem));
        Assert.False(string.IsNullOrWhiteSpace(info.Runtime));
        Assert.Equal(Environment.ProcessId, info.ProcessId);
        Assert.True(info.ProcessorCount > 0);
        Assert.True(info.TotalMemoryBytes > 0);
        Assert.Equal(AppContext.BaseDirectory, info.BaseDirectory);
        Assert.NotNull(info.CommandLine);
    }
}
