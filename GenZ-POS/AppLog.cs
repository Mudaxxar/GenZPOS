using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GenZPOS;

/// <summary>
/// Tiny append-only error log: %LocalAppData%\GenZ-POS\logs\yyyy-MM-dd.log, one file per day, kept 60 days.
/// Never throws (logging must not break the app).
/// </summary>
public static class AppLog
{
    public const int KeepDays = 60;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenZ-POS", "logs");

    private static readonly object Gate = new();

    // The same exception object is often reported twice (RunAsync + FriendlyMessage, ErrorBoundary re-renders).
    private static readonly ConditionalWeakTable<Exception, object> Logged = new();

    public static string TodayFile => Path.Combine(Folder, $"{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null)
    {
        if (ex is not null)
        {
            lock (Logged)
            {
                if (Logged.TryGetValue(ex, out _))
                    return; // already in the log
                Logged.Add(ex, Gate);
            }
        }
        Write("ERROR", message, ex);
    }

    public static void Error(Exception ex, string? context = null) =>
        Error(context ?? "Unexpected error", ex);

    public static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
                .Append(level).Append(' ')
                .Append(message);
            if (ex is not null)
                sb.AppendLine().Append(ex);
            sb.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(TodayFile, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Disk full / no access: nothing sensible to do.
        }
    }

    /// <summary>Deletes log files older than <see cref="KeepDays"/>. Call once at startup (cheap).</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in new DirectoryInfo(Folder).GetFiles("*.log"))
                if (file.LastWriteTime < cutoff)
                    file.Delete();
        }
        catch
        {
            // Best effort.
        }
    }
}

/// <summary>Routes Microsoft.Extensions.Logging (Blazor WebView, EF Core) errors into <see cref="AppLog"/>.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

    public void Dispose() { }

    private sealed class FileLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = $"[{category}] {formatter(state, exception)}";
            if (exception is not null)
                AppLog.Error(message, exception);
            else
                AppLog.Write(logLevel == LogLevel.Critical ? "CRIT " : "ERROR", message, null);
        }
    }
}
