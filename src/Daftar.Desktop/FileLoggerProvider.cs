using System;
using System.IO;
using System.Text;
using Daftar.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Daftar.Desktop;

/// <summary>
/// Writes log lines next to the database, one rolling file per day, capped so a chatty failure
/// cannot fill the disk — a full disk prevents the app from taking its own backups and from
/// opening SQLite at all.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory = Path.Combine(DaftarPaths.AppDataDirectory, "logs");

    public ILogger CreateLogger(string categoryName) => new FileLogger(_logDirectory, categoryName);

    public void Dispose() { }

    private sealed class FileLogger(string directory, string category) : ILogger
    {
        private readonly object _gate = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            try
            {
                Directory.CreateDirectory(directory);

                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(ShortLevel(logLevel)).Append("] ")
                    .Append(category).Append(": ")
                    .Append(formatter(state, exception))
                    .AppendLine();

                if (exception is not null)
                    line.AppendLine(exception.ToString());

                var path = Path.Combine(directory, $"daftar-{DateTime.Now:yyyy-MM-dd}.log");

                lock (_gate)
                    File.AppendAllText(path, line.ToString());

                PruneOld(directory);
            }
            catch
            {
                // Logging must never be the reason the app dies.
            }
        }

        /// <summary>Keeps the last 14 days. Deliberately simple and best-effort.</summary>
        private static void PruneOld(string directory)
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var file in new DirectoryInfo(directory).GetFiles("daftar-*.log"))
            {
                if (file.LastWriteTime < cutoff)
                {
                    try { file.Delete(); } catch { /* a locked file is not worth surfacing */ }
                }
            }
        }

        private static string ShortLevel(LogLevel level) => level switch
        {
            LogLevel.Critical => "CRIT",
            LogLevel.Error => "ERROR",
            LogLevel.Warning => "WARN",
            LogLevel.Information => "INFO",
            _ => "DEBUG",
        };
    }
}