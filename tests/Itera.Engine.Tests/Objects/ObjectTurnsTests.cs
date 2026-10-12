using System.Numerics;
using Itera.Engine.Objects;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// The headings' turns (<see cref="ObjectTurns"/>): quarter and half turns exact in the world's integers, and the
/// integer turn into an object the inverse of the float turn drawing uses, within a centimetre.
/// </summary>
public sealed class ObjectTurnsTests
{
    [Fact]
    public void Quarter_and_half_turns_are_exact()
    {
        Assert.Equal((123L, -45L), ObjectTurns.ToObject(0, 123, -45));
        // A quarter turn takes the object's own -z to the world's +x: a world offset along +x is along its own -z.
        Assert.Equal((0L, -100L), ObjectTurns.ToObject(1024, 100, 0));
        Assert.Equal((-45L, -123L), ObjectTurns.ToObject(1024, 123, -45));
        Assert.Equal((-123L, 45L), ObjectTurns.ToObject(2048, 123, -45));
        Assert.Equal((45L, 123L), ObjectTurns.ToObject(3072, 123, -45));
    }

    /// <summary>
    /// At any heading, a point of the object turned into the world by <see cref="ObjectTurns.Rotation"/> and back by
    /// <see cref="ObjectTurns.ToObject"/> lands within a centimetre of where it started.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(1500)]
    [InlineData(2900)]
    [InlineData(4095)]
    public void The_integer_turn_undoes_the_drawn_one(int heading)
    {
        var turn = ObjectTurns.Rotation(heading);
        foreach (var own in (Vector3[])[new(250, 0, 0), new(0, 300, -180), new(-77, 0, 412), new(1_000, 0, 1_000)])
        {
            var world = Vector3.Transform(own, turn);
            var (x, z) = ObjectTurns.ToObject(heading, (long)Math.Round(world.X), (long)Math.Round(world.Z));
            Assert.InRange(x - own.X, -1.5, 1.5);
            Assert.InRange(z - own.Z, -1.5, 1.5);
        }
    }
}
