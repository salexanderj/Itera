using Game;
using Itera.Engine;
using Itera.Engine.Diagnostics;
using Itera.Engine.Networking;
using Itera.Engine.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// A bootstrap logger covers the time before the host exists, so configuration and DI failures are still reported.
// AddIteraEngine replaces it with the configured console and file logger once the host is built.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

IHost? host = null;
try
{
    // The host builder provides configuration (appsettings.json, environment variables, --Engine:Key=Value args),
    // logging, and dependency injection. The game loop itself runs on this thread because GLFW requires window creation
    // and event polling on the thread that initialised it. ContentRootPath is pinned to the executable's folder so
    // appsettings.json and Mods/ are found from any working directory. Steam starts the game with "+connect_lobby ID"
    // when a friend joins from their friends list.
    var builder = Host.CreateApplicationBuilder(
        new HostApplicationBuilderSettings
        {
            Args = SteamLaunchArguments.Translate(args),
            ContentRootPath = AppContext.BaseDirectory,
        }
    );
    // The keys the command line gives win over the player's settings.json for the run.
    builder.Services.AddSingleton(new CommandLineKeys(SteamLaunchArguments.Translate(args)));
    builder.Services.AddIteraEngine(builder.Configuration);
    builder.Services.AddGame(builder.Configuration);
    // Always, not only in Development: a run-wide service that takes a match's (scoped) service would keep the first
    // match's for the whole run, so resolving one from the root throws.
    builder.ConfigureContainer(
        new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true })
    );

    host = builder.Build();
    GameServiceCollectionExtensions.ValidateOptions(host.Services);

    var crashLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Game.Crash");
    using var crashHandlers = UnhandledExceptionLogger.Install(crashLogger, Log.CloseAndFlush);

    return host.Services.GetRequiredService<GameLoop>().Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "The game terminated unexpectedly");
    return 1;
}
finally
{
    host?.Dispose();
    Log.CloseAndFlush();
}
