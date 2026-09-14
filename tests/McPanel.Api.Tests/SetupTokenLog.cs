using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace McPanel.Api.Tests;

internal sealed class SetupTokenLog : ILoggerProvider, ILogger
{
    public ConcurrentQueue<string> Tokens { get; } = new();
    public string Latest => Tokens.Last();
    public ILogger CreateLogger(string categoryName) => this;
    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public void Dispose() { }
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
        foreach (var value in values)
            if (value is { Key: "SetupToken", Value: string token }) Tokens.Enqueue(token);
    }
}
