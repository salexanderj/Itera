using Itera.Engine.UI;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="WheelSteps"/>: parts of a step add up to whole ones and the rest is kept, either way; a turn that is not a
/// number moves nothing.
/// </summary>
public sealed class WheelStepsTests
{
    [Fact]
    public void Parts_add_up_and_the_rest_is_kept()
    {
        var wheel = new WheelSteps();

        Assert.Equal(0, wheel.Take(0.375f));
        Assert.Equal(0, wheel.Take(0.375f));
        Assert.Equal(1, wheel.Take(0.375f));
        Assert.Equal(2, wheel.Take(1.875f));
        Assert.Equal(-2, wheel.Take(-2.5f));
        Assert.Equal(0, wheel.Take(float.NaN));
        Assert.Equal(0, wheel.Take(float.PositiveInfinity));
        Assert.Equal(-1, wheel.Take(-0.5f));
    }
}
