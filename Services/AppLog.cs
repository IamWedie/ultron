using Microsoft.Extensions.Logging;

namespace Ultron.Services;

/// <summary>App-wide logging facade. Routes every category through a single
/// Microsoft.Extensions.Logging pipeline (file provider, level filter from
/// AppSettings.LogLevel, secret redaction). Never throws.</summary>
public static class AppLog
{
    public enum Level
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3,
    }

    public static Level Verbosity { get; private set; } = Level.Info;

    private static readonly object Lock = new();
    private static ILoggerFactory _factory = null!;
    private static readonly Dictionary<string, ILogger> Loggers = new();
    private static LogLevel _minLevel = LogLevel.Information;

    /// <summary>Map AppLog granular verbosity onto MEL levels, setting the
    /// factory's minimum so lower-priority records are dropped early.</summary>
    public static void SetVerbosity(string? name, bool rebuild = true)
    {
        var parsed = Enum.TryParse<Level>(name, ignoreCase: true, out var lv)
            ? lv
            : Level.Info;
        Verbosity = parsed;
        _minLevel = parsed switch
        {
            Level.Error => LogLevel.Error,
            Level.Warn => LogLevel.Warning,
            Level.Info => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        if (rebuild) RebuildFactory();
    }

    private static void RebuildFactory()
    {
        lock (Lock)
        {
            _factory?.Dispose();
            Loggers.Clear();
            _factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(_minLevel);
                builder.AddFilter(level => level >= _minLevel);
                builder.AddProvider(new RedactingFileProvider(
                    Path.Combine(AppSettings.DataDir(), "ultron.log")));
            });
        }
    }

    private static ILogger For(string category)
    {
        if (_factory is null) RebuildFactory();
        ILogger log;
        lock (Lock)
        {
            if (!Loggers.TryGetValue(category, out log!))
            {
                log = _factory!.CreateLogger(category);
                Loggers[category] = log;
            }
        }
        return log;
    }

    public static void Write(string category, string message, Level level = Level.Info)
    {
        if (level > Verbosity) return;
        var lvl = level switch
        {
            Level.Error => LogLevel.Error,
            Level.Warn => LogLevel.Warning,
            Level.Info => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        try
        {
            For(category).Log(lvl, "{Msg}", message);
        }
        catch { }
    }

    public static string Redact(string? s) => Secrets.Redact(s);

    /// <summary>MEL provider that writes UTC-ish timestamped, redacted lines to
    /// %LOCALAPPDATA%\Ultron\ultron.log (one writer via text lock).</summary>
    private sealed class RedactingFileProvider : ILoggerProvider
    {
        private readonly string _path;
        public RedactingFileProvider(string path) => _path = path;
        public ILogger CreateLogger(string categoryName) => new RedactingFileLogger(_path, categoryName);
        public void Dispose() { }
    }

    private sealed class RedactingFileLogger : ILogger
    {
        private static readonly object Gate = new();
        private readonly string _path;
        private readonly string _category;

        public RedactingFileLogger(string path, string category)
        {
            _path = path;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter is null) return;
            try
            {
                var line = Secrets.Redact(formatter(state, exception));
                if (exception is not null)
                    line += $" :: {exception.GetType().Name}: {Secrets.Redact(exception.Message)}";
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.AppendAllText(_path,
                        $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] [{_category}] {line}\n");
                }
            }
            catch { }
        }
    }
}