using Itera.Engine.Voxels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Configuration;

public sealed class EngineOptionsValidatorTests
{
    private readonly EngineOptionsValidator _validator = new();

    [Fact]
    public void Default_options_are_valid()
    {
        Assert.True(_validator.Validate(null, new EngineOptions()).Succeeded);
    }

    [Fact]
    public void The_menu_is_the_default_and_with_an_address_it_must_be_one_to_join()
    {
        Assert.Equal(SessionMode.Menu, new EngineOptions().Session.Mode);
        var steam = new EngineOptions
        {
            Session = new SessionSettings
            {
                Mode = SessionMode.Menu,
                Transport = SessionTransportKind.Steam,
                Address = "lobby:109775240000000001",
            },
        };
        Assert.True(_validator.Validate(null, steam).Succeeded);

        // Steam's join with Steam turned off still opens the menu, which shows why the join fails.
        steam.Steam.Enabled = false;
        Assert.True(_validator.Validate(null, steam).Succeeded);

        var bad = new EngineOptions
        {
            Session = new SessionSettings { Mode = SessionMode.Menu, Address = "nowhere" },
        };
        var failure = Assert.Single(_validator.Validate(null, bad).Failures!);
        Assert.StartsWith("Engine:Session:Address", failure, StringComparison.Ordinal);
    }

    /// <summary>Each level must be drawn farther than the one before, the coarsest too.</summary>
    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(16, 1.5, true)]
    [InlineData(16, 1.25, true)]
    [InlineData(16, 2, true)]
    [InlineData(30, 1.5, true)]
    [InlineData(100, 1.5, false)]
    [InlineData(100, 2, false)]
    [InlineData(250, 1.25, false)]
    [InlineData(16, 1.2, false)]
    [InlineData(16, 2.1, false)]
    [InlineData(-1, 2, false)]
    [InlineData(double.NaN, 2, false)]
    public void Each_level_reaches_farther_than_the_one_before(double level0Reach, double growth, bool valid)
    {
        var options = new EngineOptions
        {
            // A 16 m radius and five levels of 1 cm voxels: with more, the coarsest reaches far enough for these level 0
            // reaches.
            Voxels = new VoxelSettings
            {
                StreamingRadiusMetres = 16,
                Levels = 5,
                Level0ReachMetres = level0Reach,
                ReachGrowth = growth,
            },
        };

        var result = _validator.Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            var key = growth is >= 1.25 and <= 2 ? "Voxels:Level0ReachMetres" : "Voxels:ReachGrowth";
            Assert.Contains(result.Failures!, failure => failure.Contains(key, StringComparison.Ordinal));
        }
    }

    /// <summary>Ten levels and an 8 km view on an 8 km map of 10 cm voxels pass; a view past 8 km does not.</summary>
    [Theory]
    [InlineData(8_000, true)]
    [InlineData(8_001, false)]
    public void Ten_levels_and_an_8_km_view_pass_on_an_8_km_map(float viewMetres, bool valid)
    {
        var validator = new EngineOptionsValidator([
            new Itera.Engine.Tests.Voxels.GridShapesGenerator(VoxelGrid.Sized(10, 4_000, -51.2, 102.4)),
        ]);
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings { Levels = 10 },
            Renderer = new RendererSettings { ViewDistanceMetres = viewMetres },
        };

        Assert.Equal(valid, validator.Validate(null, options).Succeeded);
    }

    /// <summary>The distant shadow cascade is left out (0) or ends 400 m to 8 km out.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(400, true)]
    [InlineData(8_000, true)]
    [InlineData(200, false)]
    [InlineData(8_001, false)]
    [InlineData(-1, false)]
    [InlineData(float.NaN, false)]
    public void The_distant_shadows_end_400_m_to_8_km_out_or_are_left_out(float metres, bool valid)
    {
        var options = new EngineOptions { Renderer = new RendererSettings { DistantShadowMetres = metres } };

        var result = new EngineOptionsValidator([]).Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.Contains(
                result.Failures!,
                failure => failure.StartsWith("Engine:Renderer:DistantShadowMetres", StringComparison.Ordinal)
            );
        }
    }

    /// <summary>
    /// On the grid of the registered generator: 10 cm voxels' columns are 12.8 m, so a 16 m radius leaves level 0 no
    /// reach (two columns are 25.6 m), while the shipped 59.2 m draws it to 33.6 m.
    /// </summary>
    [Theory]
    [InlineData(16, false)]
    [InlineData(25.6, false)]
    [InlineData(59.2, true)]
    public void On_a_10_cm_grid_level_0_must_reach_past_two_of_its_columns(double radius, bool valid)
    {
        var validator = new EngineOptionsValidator([
            new Itera.Engine.Tests.Voxels.GridShapesGenerator(new VoxelGrid(10)),
        ]);
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings { StreamingRadiusMetres = radius, Levels = 4 },
        };

        var result = validator.Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.Contains(
                result.Failures!,
                failure => failure.Contains("draws level 0 nowhere", StringComparison.Ordinal)
            );
        }
    }

    /// <summary>Levels 0 to <c>VoxelConstants.MaxLevel</c> (9): ten at most.</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(0, false)]
    [InlineData(11, false)]
    public void Levels_stay_within_the_chunk_keys_levels(int levels, bool valid)
    {
        var options = new EngineOptions { Voxels = new VoxelSettings { Levels = levels } };

        Assert.Equal(valid, _validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void A_zero_step_rate_fails_and_names_the_key()
    {
        var result = _validator.Validate(null, new EngineOptions { FixedStepHz = 0 });

        var failure = Assert.Single(result.Failures!);
        Assert.StartsWith("Engine:FixedStepHz must be between 1 and 1000", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_failure_is_reported()
    {
        var options = new EngineOptions
        {
            MaxFixedStepsPerFrame = 0,
            Window = new WindowSettings { Width = 0 },
            Renderer = new RendererSettings { SkyRed = float.NaN },
            Logging = new LoggingSettings { RetainedFiles = 0 },
        };

        var failures = _validator.Validate(null, options).Failures!.ToArray();

        Assert.Equal(4, failures.Length);
        Assert.Contains(failures, f => f.StartsWith("Engine:Renderer:SkyRed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.5f, true)]
    [InlineData(1f, true)]
    [InlineData(0.49f, false)]
    [InlineData(1.01f, false)]
    [InlineData(float.NaN, false)]
    public void The_render_scale_lies_between_a_half_and_one(float scale, bool valid)
    {
        var options = new EngineOptions { Renderer = new RendererSettings { RenderScale = scale } };

        var result = _validator.Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.StartsWith("Engine:Renderer:RenderScale", Assert.Single(result.Failures!), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(30f, true)]
    [InlineData(360f, true)]
    [InlineData(29f, false)]
    [InlineData(361f, false)]
    [InlineData(float.NaN, false)]
    public void The_dynamic_scaling_threshold_lies_between_30_and_360(float fps, bool valid)
    {
        var options = new EngineOptions { Renderer = new RendererSettings { DynamicScalingThreshold = fps } };

        var result = _validator.Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.StartsWith(
                "Engine:Renderer:DynamicScalingThreshold",
                Assert.Single(result.Failures!),
                StringComparison.Ordinal
            );
        }
    }

    [Fact]
    public void Session_options_out_of_range_fail_and_name_their_keys()
    {
        var options = new EngineOptions
        {
            Session = new SessionSettings
            {
                Mode = (SessionMode)7,
                Port = 80,
                MaxPlayers = 1,
                ConnectTimeoutSeconds = 0,
                DisconnectTimeoutSeconds = double.NaN,
                MaxJoinMegabytes = 0,
                PlayerName = "",
            },
        };

        var failures = _validator.Validate(null, options).Failures!.ToArray();

        Assert.Equal(7, failures.Length);
        Assert.All(failures, f => Assert.StartsWith("Engine:Session:", f, StringComparison.Ordinal));
    }

    /// <summary>A join payload is held in one stream, whose length is an int.</summary>
    [Theory]
    [InlineData(2047, true)]
    [InlineData(2048, false)]
    public void The_largest_join_fits_one_stream(int megabytes, bool valid)
    {
        var options = new EngineOptions { Session = new SessionSettings { MaxJoinMegabytes = megabytes } };

        Assert.Equal(!valid, _validator.Validate(null, options).Failed);
    }

    /// <summary>The testing options that add delay, jitter, and loss to a peer's links have ranges of their own.</summary>
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(2000, 1000, 100, true)]
    [InlineData(2001, 0, 0, false)]
    [InlineData(-1, 0, 0, false)]
    [InlineData(0, 1001, 0, false)]
    [InlineData(0, -1, 0, false)]
    [InlineData(0, 0, 100.5, false)]
    [InlineData(0, 0, -0.5, false)]
    [InlineData(0, 0, double.NaN, false)]
    public void The_added_network_conditions_have_ranges(int delay, int jitter, double loss, bool valid)
    {
        var options = new EngineOptions
        {
            Session = new SessionSettings
            {
                AddedDelayMilliseconds = delay,
                AddedJitterMilliseconds = jitter,
                AddedLossPercent = loss,
            },
        };

        var result = _validator.Validate(null, options);

        Assert.Equal(!valid, result.Failed);
        if (!valid)
        {
            Assert.StartsWith("Engine:Session:Added", Assert.Single(result.Failures!), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("192.168.1.20:0", false)]
    [InlineData("192.168.1.20:65536", false)]
    [InlineData("::1:47600", false)]
    [InlineData("[]:47600", false)]
    [InlineData("192.168.1.20:47600", true)]
    [InlineData("friend.example.org:47600", true)]
    [InlineData("[::1]:47600", true)]
    public void Joining_needs_an_address_of_host_and_port(string? address, bool valid)
    {
        var options = new EngineOptions
        {
            Session = new SessionSettings { Mode = SessionMode.Join, Address = address },
        };

        var result = _validator.Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.StartsWith("Engine:Session:Address", Assert.Single(result.Failures!), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("lobby:109775242961254393", true)]
    [InlineData("steam:76561198059821977", true)]
    [InlineData("127.0.0.1:47600", false)]
    [InlineData("lobby:", false)]
    public void Joining_through_steam_needs_a_lobby_or_a_steam_id(string address, bool valid)
    {
        var options = new EngineOptions
        {
            Session = new SessionSettings
            {
                Mode = SessionMode.Join,
                Transport = SessionTransportKind.Steam,
                Address = address,
            },
            Steam = new SteamSettings { AppId = 480 },
        };

        var result = _validator.Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
    }

    [Theory]
    [InlineData("Sample Game", "SampleGame", true)]
    [InlineData("Itera", "sample-game_2", true)]
    [InlineData(" ", "SampleGame", false)]
    [InlineData("Sample Game", "", false)]
    [InlineData("Sample Game", "Sample Game", false)]
    [InlineData("Sample Game", "../SampleGame", false)]
    [InlineData("Sample Game", "Sample:Game", false)]
    public void The_application_needs_a_name_and_a_plain_data_folder(string name, string dataFolder, bool valid)
    {
        var options = new EngineOptions
        {
            Application = new ApplicationSettings { Name = name, DataFolder = dataFolder },
        };

        var result = _validator.Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.StartsWith("Engine:Application:", Assert.Single(result.Failures!), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("castle", true)]
    [InlineData("My castle 2", true)]
    [InlineData("north-keep_1", true)]
    [InlineData("", false)]
    [InlineData(" castle", false)]
    [InlineData("castle ", false)]
    [InlineData("../castle", false)]
    [InlineData("saves/castle", false)]
    [InlineData("castle.save", false)]
    [InlineData("a123456789b123456789c123456789d123456789e123456789f123456789g1234", false)]
    // Names Windows keeps for devices, in any case.
    [InlineData("CON", false)]
    [InlineData("nul", false)]
    [InlineData("Com1", false)]
    [InlineData("lpt9", false)]
    [InlineData("console", true)]
    [InlineData("COM10", true)]
    public void Save_names_are_checked(string name, bool valid)
    {
        var options = new EngineOptions { Save = new SaveSettings { Name = name } };

        var result = _validator.Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.StartsWith("Engine:Save:Name", Assert.Single(result.Failures!), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Steam_sessions_need_steam_and_an_app_id()
    {
        var options = new EngineOptions
        {
            Session = new SessionSettings { Mode = SessionMode.Host, Transport = SessionTransportKind.Steam },
            Steam = new SteamSettings { Enabled = false, AppId = 0 },
        };

        var failures = _validator.Validate(null, options).Failures!.ToArray();

        Assert.Equal(2, failures.Length);
        Assert.Contains(failures, f => f.StartsWith("Engine:Session:Transport is Steam", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Engine:Steam:AppId is 0", StringComparison.Ordinal));
    }

    [Fact]
    public void The_window_mode_and_the_offscreen_frame_cap_are_checked()
    {
        var options = new EngineOptions
        {
            Window = new WindowSettings { Mode = (WindowMode)7, OffscreenFps = 0 },
        };

        var failures = _validator.Validate(null, options).Failures!.ToArray();

        Assert.Equal(2, failures.Length);
        Assert.Contains(failures, f => f.StartsWith("Engine:Window:Mode must be", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Engine:Window:OffscreenFps", StringComparison.Ordinal));
    }

    [Fact]
    public void Free_running_steps_need_a_run_without_graphics_and_physics_threads_are_checked()
    {
        var windowed = new EngineOptions
        {
            FreeRun = true,
            Physics = new PhysicsSettings { Threads = 17 },
        };
        var headless = new EngineOptions
        {
            FreeRun = true,
            Window = new WindowSettings { Mode = WindowMode.None },
            Physics = new PhysicsSettings { Threads = 1 },
        };

        var failures = _validator.Validate(null, windowed).Failures!.ToArray();

        Assert.Equal(2, failures.Length);
        Assert.Contains(
            failures,
            f => f.StartsWith("Engine:FreeRun needs Engine:Window:Mode=None", StringComparison.Ordinal)
        );
        Assert.Contains(failures, f => f.StartsWith("Engine:Physics:Threads", StringComparison.Ordinal));
        Assert.True(_validator.Validate(null, headless).Succeeded);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void The_collision_cell_without_terrain_is_1_to_10_cm(int centimetres, bool valid)
    {
        var options = new EngineOptions { Physics = new PhysicsSettings { CollisionCentimetres = centimetres } };

        var result = _validator.Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        Assert.Equal(
            !valid,
            result.Failures?.Any(f => f.StartsWith("Engine:Physics:CollisionCentimetres", StringComparison.Ordinal))
                ?? false
        );
    }

    [Fact]
    public void A_console_run_file_must_exist_and_an_empty_one_means_none()
    {
        var missing = new EngineOptions { Console = new ConsoleSettings { RunFile = "/no/such/itera-script.txt" } };
        var empty = new EngineOptions { Console = new ConsoleSettings { RunFile = " " } };

        var failures = _validator.Validate(null, missing).Failures!.ToArray();

        Assert.Single(failures);
        Assert.StartsWith(
            "Engine:Console:RunFile names /no/such/itera-script.txt",
            failures[0],
            StringComparison.Ordinal
        );
        Assert.True(_validator.Validate(null, empty).Succeeded);
    }

    [Fact]
    public void An_app_id_of_0_is_allowed_without_steam_sessions()
    {
        var options = new EngineOptions { Steam = new SteamSettings { AppId = 0 } };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Voxel_options_out_of_range_fail_and_name_their_keys()
    {
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings
            {
                StreamingRadiusMetres = 1,
                MemoryBudgetMegabytes = 1,
                WorkerThreads = -1,
                DebugWalkSpeed = double.NaN,
            },
        };

        var failures = _validator.Validate(null, options).Failures!.ToArray();

        Assert.Equal(4, failures.Length);
        Assert.All(failures, f => Assert.StartsWith("Engine:Voxels:", f, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(257)]
    public void Staging_size_out_of_range_fails_and_names_its_key(int megabytes)
    {
        var options = new EngineOptions { Renderer = new RendererSettings { UploadMegabytesPerFrame = megabytes } };

        var failure = Assert.Single(_validator.Validate(null, options).Failures!);
        Assert.StartsWith("Engine:Renderer:UploadMegabytesPerFrame", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(63, 2048, "Engine:Renderer:MeshMegabytes")]
    [InlineData(4097, 2048, "Engine:Renderer:MeshMegabytes")]
    [InlineData(1024, 511, "Engine:Renderer:ShadowMapTexels")]
    [InlineData(1024, 4097, "Engine:Renderer:ShadowMapTexels")]
    public void Mesh_heap_and_shadow_map_sizes_out_of_range_fail_and_name_their_keys(
        int meshMegabytes,
        int shadowMapTexels,
        string key
    )
    {
        var options = new EngineOptions
        {
            Renderer = new RendererSettings { MeshMegabytes = meshMegabytes, ShadowMapTexels = shadowMapTexels },
        };

        var failure = Assert.Single(_validator.Validate(null, options).Failures!);
        Assert.StartsWith(key, failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void A_light_budget_out_of_range_fails_and_names_its_key(int mostLights)
    {
        var options = new EngineOptions { Renderer = new RendererSettings { MostLights = mostLights } };

        var failure = Assert.Single(_validator.Validate(null, options).Failures!);
        Assert.StartsWith("Engine:Renderer:MostLights", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Camera_options_out_of_range_fail_and_name_their_keys()
    {
        var options = new EngineOptions
        {
            Camera = new CameraSettings
            {
                FieldOfViewDegrees = 179,
                FlySpeed = 0,
                MouseSensitivity = double.NaN,
            },
        };

        var failures = _validator.Validate(null, options).Failures!.ToArray();

        Assert.Equal(3, failures.Length);
        Assert.All(failures, f => Assert.StartsWith("Engine:Camera:", f, StringComparison.Ordinal));
    }

    [Fact]
    public void Physics_options_out_of_range_fail_and_name_their_keys()
    {
        var options = new EngineOptions
        {
            Physics = new PhysicsSettings
            {
                SettleSeconds = 0.1,
                MaxLooseBodies = 0,
                Substeps = 9,
                VelocityIterations = 0,
                LinearDamping = 1.5f,
                AngularDamping = -0.1f,
            },
        };

        var failures = _validator.Validate(null, options).Failures!.ToArray();

        Assert.Equal(6, failures.Length);
        foreach (var key in new[] { "Substeps", "VelocityIterations", "LinearDamping", "AngularDamping" })
        {
            Assert.Contains(failures, f => f.StartsWith("Engine:Physics:" + key, StringComparison.Ordinal));
        }

        Assert.All(failures, f => Assert.StartsWith("Engine:Physics:", f, StringComparison.Ordinal));
    }

    [Fact]
    public void Reading_invalid_options_through_di_throws()
    {
        using var provider = new ServiceCollection()
            .Configure<EngineOptions>(o => o.FixedStepHz = -60)
            .AddSingleton<IValidateOptions<EngineOptions>, EngineOptionsValidator>()
            .BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<EngineOptions>>().Value
        );
        Assert.Contains("Engine:FixedStepHz", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_control_port_and_the_shader_folder_are_checked()
    {
        var options = new EngineOptions();
        options.Control.Port = 80;
        options.Renderer.ShaderSourceDirectory = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid());

        var failures = _validator.Validate(null, options).Failures!.ToArray();

        Assert.Contains(failures, failure => failure.Contains("Control:Port", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("ShaderSourceDirectory", StringComparison.Ordinal));
        options.Control.Port = 47650;
        options.Renderer.ShaderSourceDirectory = Path.GetTempPath();
        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(10, 500, true)]
    [InlineData(10, 600, false)]
    [InlineData(100, 5_000, true)]
    [InlineData(100, 6_000, false)]
    public void The_streaming_radius_and_level_0_reach_may_reach_39_columns_on_voxels_over_10_cm(
        int baseCentimetres,
        double metres,
        bool valid
    )
    {
        var validator = new EngineOptionsValidator([
            new Itera.Engine.Tests.Voxels.GridShapesGenerator(new VoxelGrid(baseCentimetres)),
        ]);
        var radius = new EngineOptions
        {
            Voxels = new VoxelSettings { StreamingRadiusMetres = metres, Levels = 1 },
        };
        var reach = new EngineOptions
        {
            Voxels = new VoxelSettings { Level0ReachMetres = metres, Levels = 1 },
        };

        Assert.Equal(valid, validator.Validate(null, radius).Succeeded);
        Assert.Equal(valid, validator.Validate(null, reach).Succeeded);
    }

    [Theory]
    [InlineData(1, 59.2)]
    [InlineData(10, 59.2)]
    [InlineData(20, 118.4)]
    [InlineData(100, 592)]
    public void A_streaming_radius_of_0_is_59_2_m_or_4_625_level_0_columns_where_farther(
        int baseCentimetres,
        double metres
    )
    {
        var grid = new VoxelGrid(baseCentimetres);
        var validator = new EngineOptionsValidator([new Itera.Engine.Tests.Voxels.GridShapesGenerator(grid)]);
        var options = new EngineOptions();

        Assert.Equal(0, options.Voxels.StreamingRadiusMetres);
        Assert.Equal(metres, options.Voxels.StreamingRadiusMetresFor(grid), 9);
        Assert.Equal(25.5, new VoxelSettings { StreamingRadiusMetres = 25.5 }.StreamingRadiusMetresFor(grid));
        Assert.True(validator.Validate(null, options).Succeeded);
        Assert.Equal(metres * 8, VoxelWorld.ViewRadiusMetres(options.Voxels, grid), 6);
    }
}
