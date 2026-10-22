using System.Numerics;
using Itera.Engine.Components;
using Itera.Engine.Rendering;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Rendering;

public sealed class RenderViewTests
{
    [Fact]
    public void The_atmosphere_starts_as_a_clear_sky_of_the_options_colour_and_keeps_what_is_set()
    {
        var view = new RenderView(Options.Create(new EngineOptions()));
        var set = new Atmosphere(new Vector3(0.3f, 0.2f, 0.2f), new Vector3(0.7f, 0.4f, 0.3f), 0.5f, 0.8f, 0.9f);

        var first = view.Atmosphere;
        view.SetAtmosphere(in set);

        var renderer = new EngineOptions().Renderer;
        Assert.Equal(Atmosphere.Clear(new Vector3(renderer.SkyRed, renderer.SkyGreen, renderer.SkyBlue)), first);
        Assert.Equal(set, view.Atmosphere);
    }

    [Fact]
    public void A_match_ends_with_the_camera_sky_and_dim_as_a_run_starts()
    {
        var view = new RenderView(Options.Create(new EngineOptions()));
        var start = view.Atmosphere;
        view.SetCamera(new CameraView(new Vector3(1, 2, 3), Quaternion.Identity, 1, 0.01f));
        view.SetAtmosphere(new Atmosphere(Vector3.One, Vector3.One, 0.5f, 0.5f, 0.5f));
        view.ScreenDim = 0.7f;

        view.ResetAfterMatch();

        Assert.Equal(CameraView.Default, view.Camera);
        Assert.Equal(start, view.Atmosphere);
        Assert.Equal(0, view.ScreenDim);
    }

    [Fact]
    public void The_camera_override_draws_from_its_place_through_the_cameras_lens_or_the_override_lens()
    {
        var view = new RenderView(Options.Create(new EngineOptions()));
        var own = new CameraView(new Vector3(1, 2, 3), Quaternion.Identity, 1.2f, 0.05f);
        view.SetCamera(in own);
        var place = new CameraPlace(new Vector3(10, 5, -4), MathF.PI / 2, -0.3f);

        view.CameraOverride = place;
        var drawn = view.DrawnCamera;
        view.LensOverride = CameraLens.Orthographic(8, -20, 20);
        var orthographic = view.DrawnCamera;

        Assert.Equal(own, view.Camera);
        Assert.Equal(own with { Position = place.Position, Orientation = place.Orientation }, drawn);
        Assert.Equal((place.Position, CameraProjection.Orthographic), (orthographic.Position, orthographic.Projection));
    }

    [Fact]
    public void The_camera_override_moves_with_the_origin_and_a_match_end_clears_it()
    {
        var view = new RenderView(Options.Create(new EngineOptions()));
        view.CameraOverride = new CameraPlace(new Vector3(10, 5, -4), 0, 0);

        view.ShiftCamera(new Vector3(8, 0, -8));
        var shifted = view.CameraOverride;
        view.ResetAfterMatch();

        Assert.Equal(new Vector3(2, 5, 4), shifted?.Position);
        Assert.Null(view.CameraOverride);
    }
}
