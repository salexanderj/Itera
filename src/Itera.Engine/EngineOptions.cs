using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine;

/// <summary>Engine configuration, bound from the "Engine" configuration section.</summary>
public sealed class EngineOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Engine";

    /// <summary>The game the engine runs (<c>Engine:Application</c>): its name and its folder of per-user files.</summary>
    public ApplicationSettings Application { get; set; } = new();

    /// <summary>Window settings.</summary>
    public WindowSettings Window { get; set; } = new();

    /// <summary>Renderer settings.</summary>
    public RendererSettings Renderer { get; set; } = new();

    /// <summary>Mod loading settings.</summary>
    public ModsSettings Mods { get; set; } = new();

    /// <summary>Log file and console settings. Levels are configured in the "Serilog" section.</summary>
    public LoggingSettings Logging { get; set; } = new();

    /// <summary>Voxel storage and streaming settings.</summary>
    public VoxelSettings Voxels { get; set; } = new();

    /// <summary>Camera settings.</summary>
    public CameraSettings Camera { get; set; } = new();

    /// <summary>The match's origin, which positions are measured from (<c>Engine:Origin</c>).</summary>
    public OriginSettings Origin { get; set; } = new();

    /// <summary>The navigation grid (<c>Engine:Navigation</c>).</summary>
    public NavigationSettings Navigation { get; set; } = new();

    /// <summary>Loose bodies (<c>Engine:Physics</c>).</summary>
    public PhysicsSettings Physics { get; set; } = new();

    /// <summary>The voxel objects a world places (<c>Engine:Objects</c>).</summary>
    public ObjectSettings Objects { get; set; } = new();

    /// <summary>The particles made by effects (<c>Engine:Particles</c>).</summary>
    public ParticleSettings Particles { get; set; } = new();

    /// <summary>Sound (<c>Engine:Audio</c>): made in code by recipes and played through OpenAL.</summary>
    public AudioSettings Audio { get; set; } = new();

    /// <summary>Hosting or joining a multiplayer session (<c>Engine:Session</c>).</summary>
    public SessionSettings Session { get; set; } = new();

    /// <summary>What a host sends each client of the entities it replicates, and how a client shows them.</summary>
    public ReplicationSettings Replication { get; set; } = new();

    /// <summary>Steam (<c>Engine:Steam</c>): internet play through Steam's networking, lobbies, and invites.</summary>
    public SteamSettings Steam { get; set; } = new();

    /// <summary>The named save a host or offline game starts from and writes at quit (<c>Engine:Save</c>).</summary>
    public SaveSettings Save { get; set; } = new();

    /// <summary>The UI drawn over the picture (<c>Engine:Ui</c>): its font size and the stats line.</summary>
    public UiSettings Ui { get; set; } = new();

    /// <summary>The in-game console (<c>Engine:Console</c>).</summary>
    public ConsoleSettings Console { get; set; } = new();

    /// <summary>The loading screen at start (<c>Engine:Loading</c>).</summary>
    public LoadingSettings Loading { get; set; } = new();

    /// <summary>The control port, for tools that drive a running game (<c>tools/control/</c>).</summary>
    public ControlSettings Control { get; set; } = new();

    /// <summary>The GPU benchmark (<c>tools/benchmark/</c>), which a game runs over its own views.</summary>
    public BenchmarkSettings Benchmark { get; set; } = new();

    /// <summary>Models a run reads beside the game's own (<c>Engine:Assets</c>).</summary>
    public AssetSettings Assets { get; set; } = new();

    /// <summary>Fixed simulation rate in steps per second.</summary>
    public int FixedStepHz { get; set; } = 60;

    /// <summary>
    /// Upper bound on fixed steps run in one main-loop iteration. Excess time is discarded to avoid a spiral after a
    /// stall. The name dates from when one iteration was one frame; it is kept so existing settings still work.
    /// </summary>
    public int MaxFixedStepsPerFrame { get; set; } = 8;

    /// <summary>
    /// True: the fixed steps run back to back without waiting for real time, <see cref="MaxFixedStepsPerFrame"/> a loop
    /// iteration, so minutes of play pass in seconds (as fast as the machine runs them). Only without graphics
    /// (<c>Engine:Window:Mode=None</c>).
    /// </summary>
    public bool FreeRun { get; set; }
}

/// <summary>The game the engine runs. The game's <c>appsettings.json</c> sets both values.</summary>
public sealed class ApplicationSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Application";

    /// <summary>The name players see: the text drawn on screen, the log header, and the application name drivers see.</summary>
    public string Name { get; set; } = "Itera";

    /// <summary>
    /// Folder under the per-user local application data folder that holds the logs and the shader cache: letters,
    /// digits, '-', and '_' only. Log files are named after it in lower case.
    /// </summary>
    public string DataFolder { get; set; } = "Itera";

    /// <summary>The full path of <see cref="DataFolder"/> under the per-user local application data folder.</summary>
    public string DataDirectory()
    {
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify
        );
        return Path.Combine(root, DataFolder);
    }
}

/// <summary>Window settings.</summary>
public sealed class WindowSettings
{
    /// <summary>Window title.</summary>
    public string Title { get; set; } = "Itera";

    /// <summary>Initial width in logical pixels.</summary>
    public int Width { get; set; } = 1280;

    /// <summary>Initial height in logical pixels.</summary>
    public int Height { get; set; } = 720;

    /// <summary>When true the swapchain uses FIFO presentation. When false it prefers mailbox.</summary>
    public bool VSync { get; set; } = true;

    /// <summary>When true the window covers its screen. The menu's settings change it (and keep it in settings.json).</summary>
    public bool Fullscreen { get; set; }

    /// <summary>
    /// <see cref="WindowMode.Window"/> (the default): an OS window. <see cref="WindowMode.Offscreen"/>: no window; the
    /// game draws as usual at <see cref="Width"/> x <see cref="Height"/> pixels into a headless surface
    /// (<c>VK_EXT_headless_surface</c>), which nothing shows, for screenshots and checks that must not open a window. Input
    /// comes only from the control port.
    /// </summary>
    public WindowMode Mode { get; set; } = WindowMode.Window;

    /// <summary>
    /// Frames a second drawn at most without a window (nothing paces a headless surface), 1 to 1000. The world's meshes
    /// are read and uploaded a share each frame, so a low cap slows loading: at 60 the starter's view did not load in 15 s.
    /// </summary>
    public int OffscreenFps { get; set; } = 240;
}

/// <summary>Whether the game opens a window (<c>Engine:Window:Mode</c>).</summary>
public enum WindowMode
{
    /// <summary>An OS window, as players get.</summary>
    Window = 0,

    /// <summary>No window: frames are drawn into a headless surface that nothing shows (screenshots, checks).</summary>
    Offscreen = 1,

    /// <summary>
    /// No graphics at all: no window, no Vulkan, no render thread. The fixed steps run, and the frame group after them,
    /// with nothing drawn (simulation checks, fast-forward runs with <c>Engine:FreeRun</c>, sessions with no windows).
    /// </summary>
    None = 2,
}

/// <summary>Renderer settings.</summary>
public sealed class RendererSettings
{
    /// <summary>
    /// The finest level the world is meshed and drawn at, 0 to 9: 0 by default, the game's 10 cm voxels. Finer levels
    /// than it still stream for the mover and physics; above the coarsest level streamed, the coarsest is meshed alone.
    /// </summary>
    public int MeshFinestLevel { get; set; }

    /// <summary>
    /// How far each level is drawn, as a share of its streaming reach (<c>Engine:Voxels:Level0ReachMetres</c>,
    /// <c>ReachGrowth</c>), 0.05 to 1; the rest of the reach is room to load before the level is drawn. At 0.5 (the
    /// default) the game's 10 cm voxels are drawn to 16.8 m, 168 voxels, where they are 5 to 7 pixels wide at 1440p, and
    /// each coarser level twice as far.
    /// </summary>
    public float MeshReachShare { get; set; } = 0.5f;

    /// <summary>
    /// The view distance in metres, where the fog is whole and the world's meshes stop: 256 by default, 0 to 8000; 0 for
    /// the coarsest streamed level's radius (473.6 m with four levels), which also ends a longer one. 8000 is the
    /// farthest measured (nine levels on the starter's 8 km map; ARCHITECTURE.md).
    /// </summary>
    public float ViewDistanceMetres { get; set; } = 256;

    /// <summary>
    /// Samples per pixel the world is drawn with: 1 (no MSAA), 2, 4 (the default), or 8, lowered to what the device
    /// offers. With more than one, SMAA is not run. The settings' Anti-aliasing changes it at once.
    /// </summary>
    public int MsaaSamples { get; set; } = 4;

    /// <summary>
    /// Megabytes of GPU memory for the meshes, the world's and the models', 64 to 4096, taken once at
    /// start: 512 by default. It is lowered to the device's storage buffer range, its largest buffer, and half its
    /// largest device-local memory heap. When it is full, the meshes of columns the view does not draw are freed first;
    /// past that, a block waits without a mesh (warned once a match).
    /// </summary>
    public int MeshMegabytes { get; set; } = 512;

    /// <summary>
    /// Megabytes of GPU memory a frame slot takes for the posed mesh assets' skinned vertices, 1 to 512, taken once at
    /// start and only when a skinned asset was read: 16 by default (about 466,000 vertices, 270 of the starter's fox). It is
    /// lowered to fit the device's storage buffer range beside every asset's own vertices. Posed instances past it are
    /// drawn unposed (warned once a run).
    /// </summary>
    public int SkinMegabytes { get; set; } = 16;

    /// <summary>
    /// Texels along each side of each sun shadow cascade's depth map, 512 to 4096: 2048 by
    /// default. Fewer texels draw and filter faster, with blurrier and blockier shadow edges.
    /// </summary>
    public int ShadowMapTexels { get; set; } = 2048;

    /// <summary>
    /// The view depth in metres where a third sun shadow cascade, past the far one's 200 m, ends: 0 (the default) leaves
    /// it out; else 400 to 8000, ended sooner by the view distance (with a view that ends before the far cascade does, it
    /// is never drawn, though its map's memory is still taken). It shows the shadows of hills and large shapes in the
    /// distance under a low sun: it is drawn only while the sun is under 30 degrees over the horizon, fading in down to
    /// 25 (a higher sun's shadows are too short to see that far), and only the terrain casts into it, not the models or
    /// mesh assets. Its map is one more layer of the shadow maps (<see cref="ShadowMapTexels"/> squared, 16 MB at 2048),
    /// its texels about 2.4 m wide at 2000 m with a 90 degree view.
    /// </summary>
    public float DistantShadowMetres { get; set; }

    /// <summary>
    /// The light budget: point lights a frame shades with, 1 to 256 (<c>Rendering.Meshes.MeshPass.MostLights</c>), 128 by
    /// default: the glowing blocks' and the events' (<c>Rendering.PointLights</c>) brightest at the camera. Each pixel
    /// loops over only the lights of its cluster of the view.
    /// </summary>
    public int MostLights { get; set; } = 128;

    /// <summary>Substring of a physical device name to prefer. Null selects by device type.</summary>
    public string? PreferredDevice { get; set; }

    /// <summary>Enable Vulkan validation layers. Null means "on in Debug builds, off otherwise".</summary>
    public bool? Validation { get; set; }

    /// <summary>Add synchronization validation (hazards between commands, queues, and the swapchain) when validation is on.</summary>
    public bool SyncValidation { get; set; } = true;

    /// <summary>Add the layer's best-practices checks when validation is on. Vendor-specific checks stay off.</summary>
    public bool BestPracticesValidation { get; set; } = true;

    /// <summary>
    /// Red component of the sky's colour at the horizon, linear, 0 to 1. The sky, with green and blue, of a game that
    /// sets no atmosphere of its own (<c>RenderView</c>'s clear sky). The engine's <c>AtmosphereSystem</c> sets one every
    /// frame from a game's <c>IAtmosphereSource</c>, so with a source these three show only on frames the loading screen
    /// covers.
    /// </summary>
    public float SkyRed { get; set; } = 0.55f;

    /// <summary>Green component of the sky's colour at the horizon, linear, 0 to 1 (see <see cref="SkyRed"/>).</summary>
    public float SkyGreen { get; set; } = 0.68f;

    /// <summary>Blue component of the sky's colour at the horizon, linear, 0 to 1 (see <see cref="SkyRed"/>).</summary>
    public float SkyBlue { get; set; } = 0.85f;

    /// <summary>
    /// Megabytes of staging memory per frame slot: the most data the CPU can hand the GPU to copy in one frame. Work
    /// that does not fit waits for the next frame.
    /// </summary>
    public int UploadMegabytesPerFrame { get; set; } = 16;

    /// <summary>
    /// Whether the sun casts shadows through the shadow maps. Off saves their drawing (0.5 to 1.8 ms at 2560x1440 on an
    /// RX 5700 XT, docs/rendering-redesign.md); every surface facing the sun is then lit.
    /// </summary>
    public bool Shadows { get; set; } = true;

    /// <summary>
    /// The share of the screen's size, on each axis, that the world and models are drawn at while
    /// <see cref="DynamicRenderScaling"/> is off, 0.5 to 1 (the benchmark and tests set a scale so); AMD's FSR 1 upscales
    /// and sharpens the picture to the screen. At 1 the picture is drawn straight onto the screen, with no upscale. The console's <c>scale</c> sets a scale for
    /// the run.
    /// </summary>
    public float RenderScale { get; set; } = 1;

    /// <summary>
    /// Whether the picture is anti-aliased with SMAA 1x before FSR 1 upscales it (<c>Rendering.SmaaPass</c>) when the world is drawn with one sample a pixel (<see cref="MsaaSamples"/> 1): 0.23 to
    /// 0.31 ms at 2560x1440. It smooths the steps along edges, not what is thinner than a pixel. The settings'
    /// Anti-aliasing changes it, and the console's <c>aa</c> for the run.
    /// </summary>
    public bool AntiAliasing { get; set; } = true;

    /// <summary>
    /// Dynamic render scaling: the render scale, 0.5 to 1, follows the GPU's time per
    /// frame so the frame rate holds the lower of <see cref="DynamicScalingThreshold"/> and the screen's refresh rate
    /// (<see cref="Rendering.DynamicRenderScale"/>); at 1 with time to spare it stays at 1. Off, the world is drawn at
    /// <see cref="RenderScale"/>. The settings' Dynamic render scaling changes it.
    /// </summary>
    public bool DynamicRenderScaling { get; set; } = true;

    /// <summary>
    /// The frame rate dynamic render scaling holds, 30 to 360 frames a second; the settings' Dynamic scaling threshold
    /// offers the common refresh rates (<see cref="Rendering.DynamicRenderScale.Thresholds"/>).
    /// </summary>
    public float DynamicScalingThreshold { get; set; } = 120;

    /// <summary>Starts with the debug lines on (outlines of loose bodies and their collision cells); the console's <c>lines</c> turns them off and on.</summary>
    public bool DebugLines { get; set; }

    /// <summary>
    /// The folder the shaders are compiled from; null for <c>Shaders/</c> next to the executable. Pointed at the
    /// repository's <c>src/Itera.Engine/Rendering/Shaders</c>, the console's <c>shaders reload</c> shows a shader edit in
    /// the running game.
    /// </summary>
    public string? ShaderSourceDirectory { get; set; }

    /// <summary>
    /// The folder a game's own shaders are compiled from (the paths under <c>Game/</c>); null for <c>Shaders/Game/</c>
    /// next to the executable. Pointed at the game's <c>Shaders</c> folder in the repository, <c>shaders reload</c> shows
    /// an edit of a game's shader too.
    /// </summary>
    public string? GameShaderSourceDirectory { get; set; }
}

/// <summary>
/// The particles effects make (<c>Engine:Particles</c>): a pool on the GPU, where they move, meet the world, and are
/// drawn (<c>ParticlePass</c>); game code queues bursts of them (<c>ParticleEmitter</c>).
/// </summary>
public sealed class ParticleSettings
{
    /// <summary>Whether particles are made and drawn; the console's <c>particles off</c> stops them too.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Particles the pool holds, 64 bytes each on the GPU (16 MB at the default). When more are made, the oldest are
    /// overwritten.
    /// </summary>
    public int Capacity { get; set; } = 262_144;

    /// <summary>The most bursts one frame takes; more wait for no frame, they are dropped.</summary>
    public int MaxBurstsPerFrame { get; set; } = 4096;

    /// <summary>The most particles one frame makes (at most <see cref="Capacity"/>).</summary>
    public int MaxSpawnsPerFrame { get; set; } = 65_536;
}

/// <summary>
/// Sound (<c>Engine:Audio</c>): recipes make it in code (<c>SoundRecipes</c>), and a thread of its own plays it through
/// OpenAL (<c>AudioService</c>).
/// </summary>
public sealed class AudioSettings
{
    /// <summary>Whether sound plays; false opens no device. The console's <c>sound off</c> silences it too.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The master volume, 0 to 1 (the console's <c>sound volume</c>): 0.1 by default (rather too quiet than too loud). The
    /// player's volume setting overrides it.
    /// </summary>
    public float Volume { get; set; } = 0.1f;

    /// <summary>Music's volume under <see cref="Volume"/>, 0 to 1: a game's default for the player's Music setting.</summary>
    public float MusicVolume { get; set; } = 1;

    /// <summary>
    /// The effects' volume under <see cref="Volume"/> (recipes, file sounds, the menu's clicks), 0 to 1: a game's default
    /// for the player's Effects setting.
    /// </summary>
    public float EffectsVolume { get; set; } = 1;

    /// <summary>
    /// The music track the engine's menu plays while there is no match (a name a game declared in its
    /// <c>IRunStartup</c>, <c>MusicPlayer.Declare</c>); null for none. A game's own menu plays its music itself.
    /// </summary>
    public string? MenuMusic { get; set; }

    /// <summary>
    /// The most sounds playing at once (OpenAL sources; at most 254, so with the music's two OpenAL Soft's 256 suffice).
    /// When all play, a louder new sound takes the quietest one's place and a quieter one is not played.
    /// </summary>
    public int MaxVoices { get; set; } = 64;

    /// <summary>The rate recipes render at, in samples a second; OpenAL converts it to the device's.</summary>
    public int SampleRate { get; set; } = 48_000;

    /// <summary>
    /// Whether one-shot recipes' rendered variations are kept in <c>SoundCache/</c> in the data folder, so later starts
    /// read them instead of rendering them (<see cref="Audio.SoundCache"/>).
    /// </summary>
    public bool Cache { get; set; } = true;
}

/// <summary>
/// The loading screen (<c>Engine:Loading</c>): drawn instead of the world from the first frame until every loading task
/// (<see cref="Loading.ILoadingTask"/>) is done or <see cref="MaxSeconds"/> have passed.
/// </summary>
public sealed class LoadingSettings
{
    /// <summary>Whether the loading screen is drawn; false draws the world from the first frame, as it loads.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The longest the loading screen stays, in seconds; past it, play starts with a Warning naming what had not
    /// finished (15 s, above the 11 to 14 s a Debug build takes).
    /// </summary>
    public double MaxSeconds { get; set; } = 15;
}

/// <summary>Models a run reads beside the game's own.</summary>
public sealed class AssetSettings
{
    /// <summary>
    /// glTF models read at the run's mesh assets step after the game's and the mods' own, so a tool shows a model
    /// without a rebuild. Each has a Name no other model has, a Path (from the folder the run starts in), and
    /// MetresPerUnit (1, or 0.01 for centimetres), as in <c>--Engine:Assets:Extra:0:Name=crate
    /// --Engine:Assets:Extra:0:Path=crate.glb</c>. The console's <c>meshes place</c> shows one. None by default.
    /// </summary>
    public ExtraModel[] Extra { get; set; } = [];
}

/// <summary>A model a run reads beside the game's own (<see cref="AssetSettings.Extra"/>).</summary>
public sealed class ExtraModel
{
    /// <summary>The name the run knows it by: no other mesh asset may have it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The glTF file (.glb or .gltf); a relative path is from the folder the run starts in.</summary>
    public string Path { get; set; } = "";

    /// <summary>Metres a unit of the file: 1 for glTF's metres, 0.01 for a file in centimetres.</summary>
    public float MetresPerUnit { get; set; } = 1f;
}

/// <summary>Mod loading settings.</summary>
public sealed class ModsSettings
{
    /// <summary>
    /// False to load no mods: a game that does not take code mods yet runs no code it did not ship, whatever lies in
    /// <see cref="Directory"/>. True, the default, loads them.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Folder that holds one sub-folder per mod. Null means "Mods" next to the executable.</summary>
    public string? Directory { get; set; }
}

/// <summary>Log sink settings. Minimum levels per category live in the "Serilog" configuration section.</summary>
public sealed class LoggingSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Logging";

    /// <summary>Folder that receives one log file per run. Null means "Logs" in the application's data folder.</summary>
    public string? Directory { get; set; }

    /// <summary>When false no log file is written. The console sink still runs.</summary>
    public bool FileEnabled { get; set; } = true;

    /// <summary>Lowest level shown on the console. The file receives every level the global minimum allows.</summary>
    public LogLevel ConsoleLevel { get; set; } = LogLevel.Information;

    /// <summary>Size at which the current file rolls over to a numbered continuation file.</summary>
    public int FileSizeLimitMegabytes { get; set; } = 50;

    /// <summary>Newest log files kept when the game starts. Older files beyond this count are deleted.</summary>
    public int RetainedFiles { get; set; } = 20;

    /// <summary>Log files older than this many days are deleted when the game starts. Zero disables the age check.</summary>
    public int RetainedDays { get; set; } = 30;

    /// <summary>Seconds between loop health summaries. Zero disables them.</summary>
    public double StatsIntervalSeconds { get; set; } = 10;
}

/// <summary>Voxel storage and streaming settings.</summary>
public sealed class VoxelSettings
{
    /// <summary>
    /// Columns that reach within this horizontal distance of a streaming centre are loaded at full detail (level 0,
    /// the world grid's voxels: 10 cm in the game), unless <see cref="Level0ReachMetres"/> sets level 0's distance. 0
    /// (the default) takes it from the grid (<see cref="StreamingRadiusMetresFor"/>): 59.2 m, or 4.625 level-0 columns
    /// where that is farther (voxels over 10 cm: 592 m at 1 m); at 10 cm level 0 then reaches 33.6 m, two 12.8 m
    /// columns less, and is drawn to half that (<c>Engine:Renderer:MeshReachShare</c>). The streamed world reaches this
    /// times 2^(<see cref="Levels"/> - 1); <c>Engine:Renderer:ViewDistanceMetres</c> ends the view sooner.
    /// </summary>
    public double StreamingRadiusMetres { get; set; }

    /// <summary>The level-0 streaming radius of <see cref="StreamingRadiusMetres"/> 0 on voxels of 10 cm and finer, in metres.</summary>
    public const double DefaultStreamingRadiusMetres = 59.2;

    /// <summary>
    /// The level-0 streaming radius of <see cref="StreamingRadiusMetres"/> 0 in the grid's level-0 columns, where that
    /// is farther than <see cref="DefaultStreamingRadiusMetres"/> (voxels over 10 cm).
    /// </summary>
    public const double DefaultStreamingRadiusColumns = 4.625;

    /// <summary>The level-0 streaming radius in a world of <paramref name="grid"/>, in metres.</summary>
    public double StreamingRadiusMetresFor(VoxelGrid grid) =>
        StreamingRadiusMetres > 0
            ? StreamingRadiusMetres
            : Math.Max(
                DefaultStreamingRadiusMetres,
                DefaultStreamingRadiusColumns * grid.ColumnCentimetres(0) / VoxelConstants.CentimetresPerMetre
            );

    /// <summary>
    /// Levels of detail streamed, 1 to 10: level k has voxels 2^k times the grid's (10, 20, 40, and 80 cm in the game)
    /// and loads columns within <see cref="StreamingRadiusMetres"/> times 2^k (at the default
    /// <see cref="ReachGrowth"/>). Four (the default) reach 473.6 m at the default radius on 10 cm voxels, the coarsest
    /// giving the far meshes; the sky field needs the level below the one nearest 64 cm (level 2 in the game).
    /// </summary>
    public int Levels { get; set; } = 4;

    /// <summary>
    /// The radius of the level-0 disc a session host streams around each remote player, in metres (2 to 256): the host
    /// moves a client's player from its commands, and the mover waits where the voxels round the feet are not loaded, so
    /// the disc holds what the player can reach before the columns ahead load (16 m: two seconds at a sprint).
    /// </summary>
    public double RemotePlayerRadiusMetres { get; set; } = 16;

    /// <summary>
    /// How far the view draws level 0, in metres; its columns load two of them (25.6 m in the game) farther. 0 draws it
    /// to the streaming radius less those two columns (33.6 m at 59.2 m). Other streaming centres (a
    /// host's discs round remote players and bodies) keep their own radii.
    /// </summary>
    public double Level0ReachMetres { get; set; }

    /// <summary>
    /// How many times farther the view draws each level than the one before (<see cref="Voxels.LevelDistances"/>), from 1.25
    /// to 2: below 2 the detail falls off faster with distance, and levels 1 up to the coarsest load less. The coarsest
    /// level is drawn to the end of the view whatever this is.
    /// </summary>
    public double ReachGrowth { get; set; } = 2;

    /// <summary>
    /// Native memory voxel storage may take. At the cap, streaming stops loading and logs a warning; it loads again
    /// once 5 % of the cap has been freed.
    /// </summary>
    public int MemoryBudgetMegabytes { get; set; } = 1024;

    /// <summary>Threads that generate chunks. Zero means half the processors, at least one.</summary>
    public int WorkerThreads { get; set; }

    /// <summary>World seed of a match that does not name one (a save's seed or a start command's seed argument overrides it). A joining client takes its host's.</summary>
    public ulong Seed { get; set; } = 1;

    /// <summary>
    /// Debug aid for unattended runs: when above zero, the debug camera ignores input and flies a circle of 100 m
    /// radius around the origin, 12 m up, at this many metres per second, and voxel streaming follows it. Zero leaves
    /// the camera to the keyboard and mouse.
    /// </summary>
    public double DebugWalkSpeed { get; set; }

    /// <summary>
    /// Columns each fixed step applies large edits (a 4 m sphere, an explosion) to; the rest wait for the next steps
    /// (<c>VoxelWorld.ApplyPendingEdits</c>). A count, not a time, so every game applies them at the same pace and a slow
    /// client's ground follows its host's (the limit was a time of 8 ms before). Measured in Release
    /// in the real world: a 4 m dig reaches about 78 columns at about 1.4 ms each, a 4 m place about 73 at about 2.6 ms,
    /// so at 4 a step takes about 5 to 6 ms for a dig and 10 for a place, near the old 8 ms.
    /// </summary>
    public int EditColumnsPerStep { get; set; } = 4;
}

/// <summary>Camera settings.</summary>
public sealed class CameraSettings
{
    /// <summary>
    /// Horizontal field of view in degrees on a 16:9 screen (90 gives about 59 degrees vertically). The vertical
    /// angle stays the same on every screen, so a wider window sees more to the sides and a narrower one less.
    /// </summary>
    public double FieldOfViewDegrees { get; set; } = 90;

    /// <summary>The vertical field of view in radians that <see cref="FieldOfViewDegrees"/> gives.</summary>
    public double VerticalFieldOfViewRadians() => 2 * Math.Atan(Math.Tan(FieldOfViewDegrees * Math.PI / 360) * 9 / 16);

    /// <summary>Starting speed of the debug fly camera in metres per second. The mouse wheel changes it.</summary>
    public double FlySpeed { get; set; } = 10;

    /// <summary>Mouse look: degrees the camera turns per pixel of mouse movement.</summary>
    public double MouseSensitivity { get; set; } = 0.1;

    /// <summary>
    /// Height of the automatic circle the debug camera flies (<c>Engine:Voxels:DebugWalkSpeed</c>, and the benchmark's
    /// flight), in metres: it must clear the game's ground along a 100 m circle round the origin.
    /// </summary>
    public double CircleHeightMetres { get; set; } = 20;
}

/// <summary>The voxel objects a world places (trees, bushes, rocks of finer voxels than the world's; <c>ObjectWorld</c>).</summary>
public sealed class ObjectSettings
{
    /// <summary>
    /// The level of the world from which objects are drawn as coarse copies in its voxels; nearer, where the world is
    /// drawn finer than it, they are drawn as their own models. Objects are kept in areas a level-K column wide. 2 (about
    /// 106 m in a world of 10 cm voxels with the engine's reaches); 1 to 9.
    /// </summary>
    public int HandOverLevel { get; set; } = 2;

    /// <summary>
    /// The most placed objects that keep a copy of their damaged voxels at once (<c>ObjectDamage</c>). Past it, the copy
    /// of the damaged object farthest from every streaming centre goes, rebuilt from the edit log when it is among the
    /// nearest again. 256; 1 to 65,536.
    /// </summary>
    public int MostDamaged { get; set; } = 256;
}

/// <summary>The navigation grid (<c>NavigationService</c>).</summary>
public sealed class NavigationSettings
{
    /// <summary>The window's most cells across, by default.</summary>
    public const int DefaultWindowCells = 1024;

    /// <summary>
    /// The most cells of 50 cm the navigation grid's window is across: 1024 (512 m) by default, 256 to 4096 (under 256 a
    /// window moving by 64-cell tiles would move only once its focus had left it). A map that fits is covered whole and
    /// the window never moves; on a larger one it moves with the players. About 6 MB a grid (two are kept) and 2 MB a
    /// field and a size at 1024.
    /// </summary>
    public int WindowCells { get; set; } = DefaultWindowCells;
}

/// <summary>The match's origin, which positions are measured from (<c>OriginShifts</c>).</summary>
public sealed class OriginSettings
{
    /// <summary>
    /// How far, in metres along any axis, the active camera may go from the match's origin before the origin moves to
    /// it (<c>OriginShiftSystem</c>): 512 by default, 8 to 4096. Floats near the camera stay fine (a step of 31
    /// micrometres just under 512 m, 61 just over).
    /// </summary>
    public double ShiftMetres { get; set; } = 512;
}

/// <summary>
/// Physics: loose bodies (pieces of voxels that Bepu moves until they rest and go back into the grid), Bepu's solver, and
/// how bodies lose speed.
/// </summary>
public sealed class PhysicsSettings
{
    /// <summary>Seconds a loose body must stay at rest before it goes back into the voxel grid.</summary>
    public double SettleSeconds { get; set; } = 3;

    /// <summary>
    /// The most loose bodies at once. A jointed figure (a ragdoll) is one body for each of its parts. A game removes the
    /// oldest to make room for new ones; the engine refuses bodies past it.
    /// </summary>
    public int MaxLooseBodies { get; set; } = 384;

    /// <summary>
    /// Bepu's worker threads, 1 to 16; 0 (the default) for a quarter of the logical processors, 1 to 6
    /// (<c>PhysicsWorld.ThreadCount</c>). Physics spread over threads does not repeat exactly from run to run; 1 makes the
    /// steps repeat on one machine (tests, fast-forward runs).
    /// </summary>
    public int Threads { get; set; }

    /// <summary>
    /// The edge of the character mover's cells in a match without voxel terrain, in centimetres, 1 to
    /// <see cref="MostCollisionCentimetres"/>: it reads the mesh colliders as columns of these cells, and on cells
    /// coarser than 1 cm walks their slopes as ramps. With terrain the cells are the world's level-0 voxels.
    /// </summary>
    public int CollisionCentimetres { get; set; } = 10;

    /// <summary>The largest <see cref="CollisionCentimetres"/>: a mesh is read no coarser than this.</summary>
    public const int MostCollisionCentimetres = 10;

    /// <summary>
    /// The edge of the character mover's cells over voxel terrain, in centimetres: 0 (the default) for the world's
    /// level-0 voxels; else a whole divisor of them, 1 to <see cref="MostTerrainCellCentimetres"/> (a match whose world's
    /// voxels it does not divide does not start). Finer than the voxels, the mover meets voxel objects, solid models,
    /// mesh colliders, and loose bodies at their own size (a 10 cm wall blocks 10 cm, not a whole voxel's column), and
    /// reads the voxels as cells of ground under their surfaces, blended between the voxels' columns as coarse cells are
    /// (<c>FineGround</c>): on smooth hills of 1 m voxels the feet keep within about 2 cm of the 1 m cells' path, where a
    /// ledge under a voxel high is a step (no higher than the body's) rather than a ramp, and a walking move costs about
    /// 2.4 times as much at 10 cm (41 against 17 microseconds in Release).
    /// </summary>
    public int TerrainCellCentimetres { get; set; }

    /// <summary>The largest <see cref="TerrainCellCentimetres"/>.</summary>
    public const int MostTerrainCellCentimetres = 100;

    /// <summary>
    /// The solver's substeps in each fixed step, 1 to <see cref="MostSubsteps"/>: more keep fast, stacked, or jointed
    /// bodies steadier, at more cost (Bepu solves the constraints, and moves the bodies that have any, once a substep).
    /// A game may change it during a match (<c>PhysicsWorld.SetSolver</c>).
    /// </summary>
    public int Substeps { get; set; } = 1;

    /// <summary>The solver's velocity iterations in each substep, 1 to <see cref="MostVelocityIterations"/>.</summary>
    public int VelocityIterations { get; set; } = 8;

    /// <summary>
    /// The share of its speed a body loses each second, 0 to 1 (1 stops every body at each step). A game may change it
    /// during a match (<c>PhysicsWorld.SetDamping</c>).
    /// </summary>
    public float LinearDamping { get; set; } = 0.03f;

    /// <summary>The share of its spin a body loses each second, 0 to 1.</summary>
    public float AngularDamping { get; set; } = 0.03f;

    /// <summary>
    /// The match's gravity on every body, in metres per second squared, each part -100 to 100 (the Earth's, 9.81 down, by
    /// default). Characters fall by it as a share of 9.81 times their own gravity, and particles by it times their own
    /// share. A game may change it during a match (<c>PhysicsWorld.SetGravity</c>).
    /// </summary>
    public GravitySettings Gravity { get; set; } = new();

    /// <summary>The most <see cref="Substeps"/>.</summary>
    public const int MostSubsteps = 8;

    /// <summary>The most <see cref="VelocityIterations"/>.</summary>
    public const int MostVelocityIterations = 32;
}

/// <summary>A gravity vector of the options (<see cref="PhysicsSettings.Gravity"/>), in metres per second squared.</summary>
public sealed class GravitySettings
{
    public float X { get; set; }

    public float Y { get; set; } = -9.81f;

    public float Z { get; set; }

    /// <summary>The most a part may be, either way.</summary>
    public const float Most = 100;
}

/// <summary>What this game does in a session.</summary>
public enum SessionMode
{
    /// <summary>
    /// The menu (the default): the player picks offline, host, or join. With an <see cref="SessionSettings.Address"/>
    /// (Steam's "Join game" gives the friend's lobby) the menu joins it at once and comes back with the reason if it
    /// cannot.
    /// </summary>
    Menu,

    /// <summary>Plays alone. No socket is opened.</summary>
    Offline,

    /// <summary>Hosts a session on <see cref="SessionSettings.Port"/>: others join it, and this game owns the world.</summary>
    Host,

    /// <summary>Joins the session at <see cref="SessionSettings.Address"/> before the world starts.</summary>
    Join,
}

/// <summary>What carries a session's messages.</summary>
public enum SessionTransportKind
{
    /// <summary>LiteNetLib over UDP: LAN, a forwarded port, or two games on one machine.</summary>
    Direct,

    /// <summary>Steam's networking sockets and relays: internet play between Steam friends, through a lobby.</summary>
    Steam,
}

/// <summary>
/// Multiplayer sessions: one player's game hosts, others join it, by address or through Steam, until the UI exists.
/// The host owns the world; a client takes the host's seed, settings, and edits.
/// </summary>
public sealed class SessionSettings
{
    /// <summary>
    /// Menu (the default), or a match at once without the menu: Offline, Host, or Join. A run given one of those ends
    /// when that match's start fails or its session ends (so scripts see the exit code); a run from the menu goes back to
    /// it.
    /// </summary>
    public SessionMode Mode { get; set; } = SessionMode.Menu;

    /// <summary>Direct (LiteNetLib) or Steam.</summary>
    public SessionTransportKind Transport { get; set; } = SessionTransportKind.Direct;

    /// <summary>UDP port a Direct host listens on.</summary>
    public int Port { get; set; } = 47600;

    /// <summary>
    /// The host to join. Direct: <c>host:port</c> (a name, an IPv4 address, or <c>[IPv6]:port</c>). Steam:
    /// <c>lobby:ID</c> (the id the host logs; a friend's invite passes it) or <c>steam:ID</c> (the host's Steam id).
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// The name this player gives the host. Null (the default) takes the Steam name when Steam runs, and "Player"
    /// otherwise.
    /// </summary>
    public string? PlayerName { get; set; }

    /// <summary>Players in a session, the host included; the host refuses more.</summary>
    public int MaxPlayers { get; set; } = 4;

    /// <summary>
    /// How long a client waits to be welcomed, or for the next part of the host's world while it downloads, before it
    /// gives up, and how long a host waits for a new connection's hello before it drops it, in seconds. (LiteNetLib
    /// itself stops trying to reach a host after about 5 s.)
    /// </summary>
    public double ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>How long a peer may stay silent before the connection counts as lost, in seconds.</summary>
    public double DisconnectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// The largest join payload (the host's edit log with its pieces) a client accepts, in megabytes, 1 to 2047 (the
    /// payload is held in one stream, whose length is an int). A host that announces more is refused before anything is
    /// downloaded.
    /// </summary>
    public int MaxJoinMegabytes { get; set; } = 256;

    /// <summary>
    /// For testing: milliseconds of delay this peer adds to every message on its links, in both directions (0 to 2000;
    /// set on a client alone, it delays what the client sends and what it receives). 0 adds none.
    /// </summary>
    public int AddedDelayMilliseconds { get; set; }

    /// <summary>
    /// For testing: up to this many extra milliseconds of delay, drawn at random for each message (0 to 1000). A
    /// sequenced or unreliable message sent by this peer may arrive out of order; what this peer receives stays in order.
    /// 0 adds none.
    /// </summary>
    public int AddedJitterMilliseconds { get; set; }

    /// <summary>
    /// For testing: the share of the sequenced and unreliable messages this peer sends that are dropped, in percent (0 to
    /// 100).
    /// Reliable messages and what this peer receives are never dropped. 0 drops none.
    /// </summary>
    public double AddedLossPercent { get; set; }
}

/// <summary>
/// Replication (<c>Engine:Replication</c>): how often a host sends each client the entities it replicates, how many
/// bytes a second it may, and how far behind the host a client shows them.
/// </summary>
public sealed class ReplicationSettings
{
    /// <summary>
    /// Sends a second to each client (1 to 60): every few fixed steps (30 at 60 steps a second: every second step). A
    /// client sends its reply every step whatever this is.
    /// </summary>
    public int SendHz { get; set; } = 30;

    /// <summary>
    /// Bytes a second a host sends each client of replicated state, over time (4,096 to 1,048,576; 64 KB/s, so a home
    /// connection's upload holds seven clients). What it does not spend it keeps, up to a send's share of
    /// <see cref="PeakBytesPerSecond"/>.
    /// </summary>
    public int BytesPerSecond { get; set; } = 65536;

    /// <summary>The most bytes a second a host sends one client in a burst (at least <see cref="BytesPerSecond"/>, at most 4 MB/s).</summary>
    public int PeakBytesPerSecond { get; set; } = 131072;

    /// <summary>
    /// How far behind the host a client shows what it replicates, in milliseconds (0 to 1,000): three sends at 30 a
    /// second, so a lost send or two leaves states to blend between. The console's <c>replication delay</c> changes it
    /// during a match.
    /// </summary>
    public int InterpolationMilliseconds { get; set; } = 100;
}

/// <summary>
/// Steam: when the Steam client runs, the game starts the Steam API at startup, and sessions may use Steam
/// (<see cref="SessionSettings.Transport"/>). Without the Steam client, or on a platform without Steam's library, the
/// game runs without it (a Warning) and plays offline or over Direct.
/// </summary>
public sealed class SteamSettings
{
    /// <summary>When false the Steam API is not started.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The game's Steam App ID (the game's <c>appsettings.json</c> sets it), for runs that Steam did not start (it writes
    /// <c>steam_appid.txt</c> next to the game). 0, the default, leaves Steam off: a game without one of its own has none.
    /// </summary>
    public uint AppId { get; set; }
}

/// <summary>
/// The named save: a host or offline game with a <see cref="Name"/> starts from
/// <c>Saves/NAME.save</c> in the data folder when it exists, or starts a new world and creates it, and writes the world
/// back to it when it quits. A client never saves. No name, no saving.
/// </summary>
public sealed class SaveSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Save";

    /// <summary>The save's name: 1 to 64 letters, digits, spaces (not first or last), '-', or '_'; null for none.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// How often a host or offline match with a save writes it while it plays, in minutes of its steps (a paused match
    /// waits), 0 to 1,440; 0 for only at the end. Written on a background thread, so no step waits for the disk.
    /// </summary>
    public double AutosaveMinutes { get; set; } = 5;

    /// <summary>The folder that holds the saves: <c>Saves</c> in the data folder.</summary>
    public static string Folder(ApplicationSettings application) => Path.Combine(application.DataDirectory(), "Saves");
}

/// <summary>The UI drawn over the picture: its font, the stats line, and whether the menu holds an offline match.</summary>
public sealed class UiSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ui";

    /// <summary>Height of the UI's text in pixels, 8 to 64.</summary>
    public float FontPixels { get; set; } = 18;

    /// <summary>
    /// The UI's size, 0.75 to 1.5 (1: as designed): every toolkit layer's units are multiplied by it
    /// (<see cref="UI.Toolkit.Ui.UnitPixelsFor"/>), the default for the player's own UI scale.
    /// </summary>
    public float Scale { get; set; } = 1;

    /// <summary>Starts with the stats line shown (the <c>stats</c> command and F1 turn it on and off).</summary>
    public bool Stats { get; set; }

    /// <summary>
    /// The menu, while open over an offline match, holds its steps (bodies, characters, clips, and time stop), so the game
    /// waits for the player. False: the match goes on behind it. Hosted and joined matches never wait, as peers would
    /// stall.
    /// </summary>
    public bool MenuPausesOffline { get; set; }
}

/// <summary>The in-game console: ` opens and closes it.</summary>
public sealed class ConsoleSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Console";

    /// <summary>False: ` does nothing and no command runs, not even <see cref="Run"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lines the console keeps to scroll back through, 100 to 10,000.</summary>
    public int ScrollbackLines { get; set; } = 1000;

    /// <summary>
    /// Commands to run once the game's first match has started, separated by ';', as if typed; null for none. With
    /// <c>wait</c> among them, the commands after it wait (the startup script).
    /// </summary>
    public string? Run { get; set; }

    /// <summary>
    /// A file of commands run after <see cref="Run"/>'s, a line at a time (lines starting with '#' and empty lines
    /// skipped); null for none. The file must exist.
    /// </summary>
    public string? RunFile { get; set; }

    /// <summary>
    /// True: a command of the startup script (<see cref="Run"/> and <see cref="RunFile"/>) that fails, or a wait of it
    /// that runs out of time, ends the script and the run, with exit code 1. For unattended runs.
    /// </summary>
    public bool ExitOnFailure { get; set; }
}

/// <summary>
/// The control port (<c>ControlService</c>): a TCP port on 127.0.0.1 through which a tool runs console commands, reads
/// the log, takes screenshots, and sends input to a running game. Off unless asked for; a development tool.
/// </summary>
public sealed class ControlSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Control";

    /// <summary>True: listen on <see cref="Port"/>. Any program on this machine can then send the game commands.</summary>
    public bool Enabled { get; set; }

    /// <summary>TCP port on 127.0.0.1, 1024 to 65,535.</summary>
    public int Port { get; set; } = 47650;
}

/// <summary>
/// The GPU benchmark (<c>BenchmarkSystem</c>, <c>tools/benchmark/</c>): the camera visits the game's fixed list of views
/// of the seed's world, waits at each until every column is loaded and on the GPU, measures, then flies the automatic
/// circle, and the game exits. The log marks each measurement; <c>Engine:Logging:StatsIntervalSeconds</c> of 1 gives
/// one GPU time per second to average.
/// </summary>
public sealed class BenchmarkSettings
{
    /// <summary>When true, the game runs the benchmark instead of being played.</summary>
    public bool Enabled { get; set; }

    /// <summary>Seconds to wait at each view after it has loaded, before measuring.</summary>
    public double SettleSeconds { get; set; } = 2;

    /// <summary>Seconds to measure at each view.</summary>
    public double MeasureSeconds { get; set; } = 8;

    /// <summary>Seconds to fly the automatic circle at 5 m/s after the views; 0 leaves the flight out.</summary>
    public double FlightSeconds { get; set; } = 60;
}
