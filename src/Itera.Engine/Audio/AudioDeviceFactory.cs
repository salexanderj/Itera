using Microsoft.Extensions.Logging;

namespace Itera.Engine.Audio;

/// <summary>Creates the best available audio device, falling back to silence.</summary>
public static partial class AudioDeviceFactory
{
    /// <summary>Tries OpenAL and returns a <see cref="NullAudioDevice"/> if it cannot be used.</summary>
    public static IAudioDevice Create(ILogger logger)
    {
        try
        {
            var device = OpenAlAudioDevice.OpenDefault();
            LogOpened(logger, device.Name);
            return device;
        }
        catch (Exception ex)
        {
            // Any failure to open sound leaves the game silent, not stopped (a native library's failures of any kind, such as a
            // MarshalDirectiveException, are caught).
            LogFallback(logger, ex.GetType().Name, ex.Message);
            return new NullAudioDevice();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audio: OpenAL device \"{Name}\"")]
    private static partial void LogOpened(ILogger logger, string name);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Audio: OpenAL unavailable ({Exception}: {Reason}); using silent device"
    )]
    private static partial void LogFallback(ILogger logger, string exception, string reason);
}
