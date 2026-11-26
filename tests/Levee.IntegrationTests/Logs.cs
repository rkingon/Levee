using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Levee.IntegrationTests;

internal sealed class Logs : ILoggerProvider
{
    public ConcurrentQueue<(string Message, Exception? Exception)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(Logs logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Entries.Enqueue((formatter(state, exception), exception));
    }
}
