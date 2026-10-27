using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Utility.UI.Services;

/// <summary>
/// The app's own log file.
/// <para>
/// One file per run: the name carries the moment the app started, so the file says
/// which run it belongs to, and every line in it belongs to that run. No rotation -
/// restarting the app starts a new file and leaves the old one exactly as it was.
/// </para>
/// <para>
/// Lines are flushed as they are written. The run worth reading is the one that
/// ended badly, and it ends without a clean shutdown - so a buffer would lose
/// exactly the lines that explain why.
/// </para>
/// </summary>
public sealed class FileLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    private FileLog(string? filePath, StreamWriter? writer)
    {
        FilePath = filePath;
        _writer = writer;
    }

    /// <summary>Path of this run's log file, or null when the file could not be opened.</summary>
    public string? FilePath { get; }

    /// <summary>True when the file is open and being written to.</summary>
    public bool IsEnabled => _writer is not null;

    /// <summary>
    /// Opens this run's log file.
    /// <para>
    /// Returns a disabled instance rather than throwing when the file cannot be
    /// opened: a log that cannot be written must never stop the app from starting.
    /// </para>
    /// </summary>
    public static FileLog TryOpen(string directory, DateTime startedAt)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var filePath = Path.Combine(
                directory, $"{startedAt:yyyy-MM-dd_HH-mm-ss}.log");

            var writer = new StreamWriter(
                new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            return new FileLog(filePath, writer);
        }
        catch (Exception)
        {
            return new FileLog(null, null);
        }
    }

    /// <summary>Writes one entry. Never throws.</summary>
    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}";

        lock (_gate)
        {
            try
            {
                writer.WriteLine(line);

                if (exception is not null)
                {
                    writer.WriteLine(exception.ToString());
                }
            }
            catch (Exception)
            {
                // A failed write must not take the app down with it.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }
}

/// <summary>
/// Feeds the app's logging pipeline into its own log file.
/// <para>
/// The pipeline carries everything worth keeping: the sync service's start, its
/// summary and its failure with the full exception chain, and Entity Framework's
/// own diagnostics - including the command that timed out and the SQL that caused
/// it. The level rules set in App.xaml.cs apply here exactly as they do to the
/// other providers.
/// </para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLog _log;

    public FileLoggerProvider(FileLog log) => _log = log;

    public ILogger CreateLogger(string categoryName) => new FileLogger(_log, categoryName);

    public void Dispose() => _log.Dispose();

    private sealed class FileLogger(FileLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            log.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
