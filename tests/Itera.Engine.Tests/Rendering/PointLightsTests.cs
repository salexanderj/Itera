using System.Numerics;
using Itera.Engine.Rendering;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// <see cref="PointLights"/>: a light added shines for its frame only, a flash fades as the square of its time left and
/// goes when it ends, the lists drop what does not fit, a match's end clears the flashes, and a frame allocates nothing.
/// </summary>
public sealed class PointLightsTests
{
    private static readonly Vector3 s_white = Vector3.One;

    [Fact]
    public void An_added_light_shines_for_its_frame_only()
    {
        var lights = new PointLights();
        lights.BeginFrame(1);
        lights.Add(new Vector3(1, 2, 3), s_white, 5);
        lights.Add(Vector3.Zero, s_white, 0);

        var frame = lights.Frame(1);
        Assert.Equal([new PointLight(new Vector3(1, 2, 3), s_white, 5)], frame.ToArray());

        lights.BeginFrame(1.02);
        Assert.Equal(0, lights.Frame(1.02).Length);
    }

    [Fact]
    public void A_flash_fades_as_the_square_of_its_time_left_and_goes_at_its_end()
    {
        var lights = new PointLights();
        lights.BeginFrame(10);
        lights.Flash(Vector3.UnitY, s_white, 100, 0.5f);

        Assert.Equal(100, lights.Frame(10)[0].Power, 3);
        lights.BeginFrame(10.25);
        Assert.Equal(25, lights.Frame(10.25)[0].Power, 3);
        lights.BeginFrame(10.5);
        Assert.Equal(0, lights.Frame(10.5).Length);

        lights.Flash(Vector3.UnitY, s_white, 100, 1);
        lights.Clear();
        lights.BeginFrame(10.6);
        Assert.Equal(0, lights.Frame(10.6).Length);
    }

    [Fact]
    public void What_does_not_fit_is_dropped_and_a_frame_allocates_nothing()
    {
        var lights = new PointLights();
        for (var round = 0; round < 2; round++)
        {
            var start = SystemAllocation.StartMeasurement();
            lights.BeginFrame(round);
            for (var i = 0; i < PointLights.MostLights + 3; i++)
            {
                lights.Add(new Vector3(i, 0, 0), s_white, 1);
                lights.Flash(new Vector3(0, i, 0), s_white, 1, 0.5f);
            }

            var count = lights.Frame(round).Length;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(2 * PointLights.MostLights, count);
            if (round == 1)
            {
                Assert.Equal(0, allocated);
            }

            lights.Clear();
        }

        Assert.Equal(12, lights.Dropped);
    }
}
