using Microsoft.Extensions.Logging;

namespace Urlaubstool.Infrastructure.Logging;

/// <summary>
/// Simple console logger for the app.
/// </summary>
public sealed class ConsoleLogger<T> : ILogger<T>
{
    private readonly string _categoryName = typeof(T).Name;

    public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var timestamp = DateTimeOffset.Now.ToString("HH:mm:ss");
        var level = logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };

        Console.WriteLine($"[{timestamp}] [{level}] [{_categoryName}] {message}");

        if (exception != null)
        {
            Console.WriteLine($"  Exception: {exception.GetType().Name}: {exception.Message}");
            if (logLevel >= LogLevel.Error)
            {
                Console.WriteLine($"  Stack: {exception.StackTrace}");
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Simple logger factory.
/// </summary>
public sealed class ConsoleLoggerFactory : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider) { }

    public ILogger CreateLogger(string categoryName)
    {
        return (ILogger)Activator.CreateInstance(
            typeof(ConsoleLogger<>).MakeGenericType(Type.GetType(categoryName) ?? typeof(object)))!;
    }

    public void Dispose() { }
}

public static class LoggerFactoryExtensions
{
    public static ILogger<T> CreateLogger<T>(this ILoggerFactory factory)
    {
        return new ConsoleLogger<T>();
    }
}
