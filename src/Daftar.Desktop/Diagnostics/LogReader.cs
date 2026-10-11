using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Daftar.Desktop.Diagnostics;

/// <summary>One line the app wrote to its log, plus whatever followed it.</summary>
public sealed record LogEntry(
    DateTimeOffset At,
    string Level,
    string Category,
    string Message,
    string Detail)
{
    public string TimeText => At.ToString("dd/MM HH:mm:ss");

    /// <summary>
    /// Whether anything followed the message. The provider appends the exception after the line,
    /// so this is the stack trace — the part that actually names the cause.
    /// </summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>
/// Reads back what <see cref="FileLoggerProvider"/> wrote.
///
/// <para>
/// It parses the files rather than keeping a parallel list in memory, for two reasons. The log is
/// already bounded to fourteen days and survives a restart, so a fault from last Tuesday is still
/// there when it is finally reported; and it also carries Entity Framework's own diagnostics, which
/// never pass through <see cref="FaultLog"/> and which explain a lot of the failures worth seeing.
/// </para>
///
/// <para>
/// Anything after the first line of an entry becomes its detail. A logged message containing a
/// newline cannot be told apart from a stack trace once it is on disk, and folding the remainder in
/// is the harmless direction of that error: the message line stays readable and the extra lands
/// below it.
/// </para>
/// </summary>
internal static class LogReader
{
    /// <summary>Enough to fill several screens without reading a fortnight of files to do it.</summary>
    internal const int DefaultMaxEntries = 500;

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    public static IReadOnlyList<LogEntry> Read(string directory, int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries <= 0 || !Directory.Exists(directory))
            return [];

        // The file name carries the day, so ordering by name orders by age without opening any.
        var files = Directory.GetFiles(directory, "daftar-*.log")
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        var entries = new List<LogEntry>(Math.Min(maxEntries, 256));

        foreach (var file in files)
        {
            if (entries.Count >= maxEntries)
                break;

            foreach (var entry in ReadFile(file).Reverse())
            {
                if (entries.Count >= maxEntries)
                    break;

                entries.Add(entry);
            }
        }

        return entries;
    }

    private static IEnumerable<LogEntry> ReadFile(string file)
    {
        LogEntry? current = null;
        var detail = new List<string>();

        // Opened shared, read-only: the app is running and appending to this very file, and a
        // crash because the screen could not read the log would be the final irony.
        using var reader = new StreamReader(
            new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

        while (reader.ReadLine() is { } line)
        {
            if (TryParse(line, out var entry))
            {
                if (current is not null)
                    yield return current with { Detail = string.Join(Environment.NewLine, detail) };

                current = entry;
                detail.Clear();
            }
            else if (current is not null)
            {
                detail.Add(line);
            }
            // A line before any header belongs to no entry: a truncated write, or a file whose
            // first flush was interrupted. Dropping it is better than showing a row with no time.
        }

        if (current is not null)
            yield return current with { Detail = string.Join(Environment.NewLine, detail) };
    }

    private static bool TryParse(string line, out LogEntry entry)
    {
        entry = null!;

        // 2026-10-11 14:23:45.123 [ERROR] Daftar: Post sale
        var open = line.IndexOf(" [", StringComparison.Ordinal);
        if (open <= 0)
            return false;

        if (!DateTimeOffset.TryParseExact(
                line[..open],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var at))
            return false;

        var close = line.IndexOf(']', open + 2);
        if (close < 0)
            return false;

        var level = line[(open + 2)..close];

        var colon = line.IndexOf(": ", close + 1, StringComparison.Ordinal);
        if (colon < 0)
            return false;

        // The writer emits "] " before the category, so trimming here rather than skipping a
        // fixed number of characters keeps the reader right if that spacing ever moves.
        var category = line[(close + 1)..colon].Trim();
        var message = line[(colon + 2)..];

        entry = new LogEntry(at, level, category, message, string.Empty);
        return true;
    }
}