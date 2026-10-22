using Itera.Engine;
using Itera.Engine.Diagnostics;
using Itera.Engine.Settings;
using Itera.Tools.ModelView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// As the mesh sample's Program.cs: a bootstrap logger, the host with the engine and the stage, every registration
// checked, then the loop on this thread. The stage registers no generator and does not call AddIteraVoxels.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

IHost? host = null;
try
{
    var builder = Host.CreateApplicationBuilder(
        new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory }
    );
    builder.Services.AddSingleton(new CommandLineKeys(args));
    builder.Services.AddIteraEngine(builder.Configuration);
    builder.Services.AddModelStage();
    builder.ConfigureContainer(
        new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true })
    );

    host = builder.Build();
    var crashLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ModelView.Crash");
    using var crashHandlers = UnhandledExceptionLogger.Install(crashLogger, Log.CloseAndFlush);

    return host.Services.GetRequiredService<GameLoop>().Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "The model stage terminated unexpectedly");
    return 1;
}
finally
{
    host?.Dispose();
    Log.CloseAndFlush();
}
