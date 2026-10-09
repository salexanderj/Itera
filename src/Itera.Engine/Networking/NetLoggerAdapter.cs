using System.Globalization;
using LiteNetLib;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Networking;

/// <summary>Routes LiteNetLib's internal log to an <see cref="ILogger"/>. Assign an instance to <see cref="NetDebug.Logger"/>.</summary>
public sealed partial class NetLoggerAdapter : INetLogger
{
    private readonly ILogger _logger;

    public NetLoggerAdapter(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Maps a LiteNetLib level to the logging level it is written at.</summary>
    public static LogLevel Map(NetLogLevel level)
    {
        return level switch
        {
            NetLogLevel.Trace => LogLevel.Trace,
            NetLogLevel.Info => LogLevel.Debug,
            NetLogLevel.Warning => LogLevel.Warning,
            NetLogLevel.Error => LogLevel.Error,
            _ => LogLevel.Debug,
        };
    }

    public void WriteNet(NetLogLevel level, string str, params object[] args)
    {
        var mapped = Map(level);
        if (!_logger.IsEnabled(mapped))
        {
            return;
        }

        var text = args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, str, args) : str;
        LogNet(mapped, text);
    }

    [LoggerMessage(Message = "[LiteNetLib] {Text}")]
    private partial void LogNet(LogLevel level, string text);
}
