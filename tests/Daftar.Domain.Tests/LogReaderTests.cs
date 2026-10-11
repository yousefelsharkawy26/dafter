using System;
using System.IO;
using System.Linq;
using Daftar.Desktop;
using Daftar.Desktop.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// The errors screen reads the files the logger writes rather than keeping a parallel list, so it
/// can show a fault from last Tuesday and can show Entity Framework's own diagnostics, which never
/// pass through <c>FaultLog</c>. The price of reading files is that the parser has to match a
/// format somebody else owns.
///
/// These tests close that gap by writing with the real provider and reading with the real reader.
/// A hand-written log string would have kept passing after the writer changed its line format,
/// which is exactly the failure this screen would not survive.
/// </summary>
public sealed class LogReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"daftar-log-{Guid.NewGuid():N}");

    public LogReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);

        GC.SuppressFinalize(this);
    }

    /// <summary>Writes through the shipping logger, at the level it would really be written.</summary>
    private void Write(string category, string message, Exception? fault = null, LogLevel level = LogLevel.Error)
    {
        using var factory = LoggerFactory.Create(b => b.AddProvider(new FileLoggerProvider(_dir)));
        factory.CreateLogger(category).Log(level, new EventId(1), message, fault, (m, _) => m);
    }

    [Fact]
    public void A_Fault_Comes_Back_With_Its_Cause_Attached()
    {
        Write("Daftar", "Post sale", new InvalidOperationException("database is locked"));

        var entry = Assert.Single(LogReader.Read(_dir));

        Assert.Equal("ERROR", entry.Level);
        Assert.Equal("Daftar", entry.Category);
        Assert.Equal("Post sale", entry.Message);
        Assert.True(entry.HasDetail);
        Assert.Contains("database is locked", entry.Detail, StringComparison.Ordinal);
        Assert.NotEqual(default, entry.At);
    }

    [Fact]
    public void The_Newest_Fault_Is_The_One_On_Top()
    {
        // Written a moment apart, so this rests on append order rather than on clock resolution.
        Write("Daftar", "first");
        Write("Daftar", "second");
        Write("Daftar", "third");

        var entries = LogReader.Read(_dir);

        Assert.Equal(new[] { "third", "second", "first" },
                     entries.Select(e => e.Message).ToArray());
    }

    [Fact]
    public void The_Newest_Day_Is_Read_Before_The_Older_One()
    {
        Write("Daftar", "today's fault");
        File.WriteAllText(
            Path.Combine(_dir, "daftar-2020-01-01.log"),
            "2020-01-01 09:00:00.000 [WARN] Daftar: an older fault\n");

        var entries = LogReader.Read(_dir);

        Assert.Equal("today's fault", entries[0].Message);
        Assert.Equal("an older fault", entries[1].Message);
    }

    [Fact]
    public void The_List_Stops_Rather_Than_Filling_Screens_For_Fortnight()
    {
        for (var i = 0; i < 12; i++)
            Write("Daftar", $"fault {i}");

        var entries = LogReader.Read(_dir, maxEntries: 4);

        Assert.Equal(4, entries.Count);
        Assert.Equal("fault 11", entries[0].Message);
    }

    [Fact]
    public void A_Quiet_App_Shows_An_Empty_Screen_Rather_Than_A_Crash()
    {
        // No directory at all: the screen is opened before anything has ever failed.
        var missing = Path.Combine(_dir, "never-created");

        Assert.Empty(LogReader.Read(missing));
    }

    [Fact]
    public void Routine_Noise_Is_Not_Worth_A_Row()
    {
        // The provider's floor is Warning. Information-level chatter stays out of the way, which
        // is the whole reason the sink was given a threshold in the first place.
        Write("Daftar", "just a note", level: LogLevel.Information);
        Write("Daftar", "a real fault", level: LogLevel.Error);

        var entry = Assert.Single(LogReader.Read(_dir));

        Assert.Equal("a real fault", entry.Message);
    }

    [Fact]
    public void A_File_Cut_Short_Mid_Line_Still_Yields_What_Survived()
    {
        // The app can be killed between the message and its stack trace; the screen must still
        // open rather than refusing to parse.
        File.WriteAllText(
            Path.Combine(_dir, "daftar-2020-01-01.log"),
            "2020-01-01 09:00:00.000 [ERROR] Daftar: half a rec\n2020-01-01 09:00:01.00\n");

        var entry = Assert.Single(LogReader.Read(_dir));

        Assert.Equal("half a rec", entry.Message);
    }

    [Fact]
    public void Lines_Before_Any_Fault_Are_Ignored_Rather_Than_Shown_Without_A_Time()
    {
        File.WriteAllText(
            Path.Combine(_dir, "daftar-2020-01-01.log"),
            "garbage from an interrupted write\n2020-01-01 09:00:00.000 [ERROR] Daftar: a real one\n");

        var entry = Assert.Single(LogReader.Read(_dir));

        Assert.Equal("a real one", entry.Message);
    }

    [Fact]
    public void The_File_Is_Read_While_The_App_Is_Still_Appending_To_It()
    {
        Write("Daftar", "one");
        Write("Daftar", "two");

        // Held open the way the provider holds it. A reader that demands exclusive access would
        // work in every test and fail on the one screen that matters.
        using var hold = new FileStream(
            LogReader.Read(_dir).Count > 0
                ? Directory.GetFiles(_dir, "daftar-*.log")[0]
                : throw new InvalidOperationException("no log was written"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal(2, LogReader.Read(_dir).Count);
    }
}