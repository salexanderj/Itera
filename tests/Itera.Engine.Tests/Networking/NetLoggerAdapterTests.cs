using Itera.Engine.Networking;
using LiteNetLib;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Tests.Networking;

public sealed class NetLoggerAdapterTests
{
    [Theory]
    [InlineData(NetLogLevel.Trace, LogLevel.Trace)]
    [InlineData(NetLogLevel.Info, LogLevel.Debug)]
    [InlineData(NetLogLevel.Warning, LogLevel.Warning)]
    [InlineData(NetLogLevel.Error, LogLevel.Error)]
    public void Maps_each_level(NetLogLevel input, LogLevel expected)
    {
        Assert.Equal(expected, NetLoggerAdapter.Map(input));
    }

    [Fact]
    public void Formats_arguments_and_writes_at_the_mapped_level()
    {
        var logger = new RecordingLogger();
        var adapter = new NetLoggerAdapter(logger);

        adapter.WriteNet(NetLogLevel.Warning, "peer {0} timed out after {1} ms", 7, 1500);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("[LiteNetLib] peer 7 timed out after 1500 ms", entry.Message);
    }

    [Fact]
    public void Skips_formatting_when_the_level_is_disabled()
    {
        var logger = new RecordingLogger(minimum: LogLevel.Warning);
        var adapter = new NetLoggerAdapter(logger);

        adapter.WriteNet(NetLogLevel.Info, "ignored {0}", 1);

        Assert.Empty(logger.Entries);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly LogLevel _minimum;

        public RecordingLogger(LogLevel minimum = LogLevel.Trace)
        {
            _minimum = minimum;
        }

        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= _minimum;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
