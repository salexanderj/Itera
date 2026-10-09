using System.Numerics;
using Itera.Engine.UI.Menu;
using SkiaSharp;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="RadialRing"/>: a ring at another centre and size hits as the menu's does, scaled and moved, with the
/// middle's top half and two lower quarters and nothing on the lines between them; its paths cover the points the hit
/// test gives their slot and leave the gaps empty; and the menu in the middle of the screen gives the same slots as the
/// ring at the frame's middle and 1.3 times its height.
/// </summary>
public sealed class RadialRingTests
{
    [Fact]
    public void A_smaller_ring_elsewhere_hits_as_the_menu_does_scaled_and_moved()
    {
        var centre = new Vector2(300, 220);
        const float scale = 400;
        for (var slot = 0; slot < RadialMenu.RingSlots; slot++)
        {
            Assert.Equal(slot, RadialRing.Hit(RadialRing.Toward(centre, slot, 0.2f * scale), centre, scale));
        }

        Assert.Equal(RadialMenu.Top, RadialRing.Hit(centre + new Vector2(0, -0.05f * scale), centre, scale));
        Assert.Equal(RadialMenu.LowerLeft, RadialRing.Hit(centre + new Vector2(-0.04f, 0.04f) * scale, centre, scale));
        Assert.Equal(RadialMenu.LowerRight, RadialRing.Hit(centre + new Vector2(0.04f, 0.04f) * scale, centre, scale));
        Assert.Equal(-1, RadialRing.Hit(centre, centre, scale));
        // On the line across the middle, and on the line down between the quarters.
        Assert.Equal(-1, RadialRing.Hit(centre + new Vector2(-0.05f * scale, 0), centre, scale));
        Assert.Equal(-1, RadialRing.Hit(centre + new Vector2(0, 0.05f * scale), centre, scale));
        Assert.Equal(-1, RadialRing.Hit(centre + new Vector2(0, -0.3f * scale), centre, scale));
    }

    [Fact]
    public void The_menu_in_the_middle_of_the_screen_is_the_ring_at_the_frames_middle_and_height()
    {
        var middle = new Vector2(960, 540);
        for (var x = 600; x <= 1320; x += 37)
        {
            for (var y = 200; y <= 880; y += 41)
            {
                var at = new Vector2(x, y);
                Assert.Equal(RadialRing.Hit(at, middle, RadialMenu.ScaleShare * 1080), RadialMenu.Hit(at, 1920, 1080));
            }
        }
    }

    [Fact]
    public void Each_path_covers_the_points_its_slot_is_hit_at()
    {
        var centre = new Vector2(250, 250);
        const float scale = 600;
        var paths = new SKPath?[RadialMenu.Slots];
        try
        {
            RadialRing.Build(paths, centre, scale);
            for (var slot = 0; slot < RadialMenu.RingSlots; slot++)
            {
                var at = RadialRing.Toward(centre, slot, 0.2f * scale);
                Assert.True(paths[slot]!.Contains(at.X, at.Y));
                Assert.False(paths[(slot + 1) % RadialMenu.RingSlots]!.Contains(at.X, at.Y));
            }

            // Each middle button holds its own points and none of the others'.
            var middle = new[]
            {
                (RadialMenu.Top, centre + new Vector2(0, -0.05f * scale)),
                (RadialMenu.LowerLeft, centre + new Vector2(-0.04f, 0.04f) * scale),
                (RadialMenu.LowerRight, centre + new Vector2(0.04f, 0.04f) * scale),
            };
            foreach (var (slot, at) in middle)
            {
                Assert.Equal(slot, RadialRing.Hit(at, centre, scale));
                foreach (var (other, _) in middle)
                {
                    Assert.Equal(other == slot, paths[other]!.Contains(at.X, at.Y));
                }
            }

            // The gaps between them are empty.
            for (var slot = RadialMenu.LowerLeft; slot <= RadialMenu.Top; slot++)
            {
                Assert.False(paths[slot]!.Contains(centre.X, centre.Y));
                Assert.False(paths[slot]!.Contains(centre.X - 0.05f * scale, centre.Y));
                Assert.False(paths[slot]!.Contains(centre.X, centre.Y + 0.05f * scale));
            }

            var bounds = RadialRing.Bounds(centre, scale);
            Assert.True(bounds.Contains(paths[0]!.Bounds));
        }
        finally
        {
            RadialRing.Free(paths);
        }

        Assert.All(paths, Assert.Null);
    }
}
