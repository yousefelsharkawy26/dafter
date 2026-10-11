using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daftar.Desktop.Diagnostics;
using Daftar.ViewModels;
using Daftar.Domain.Shared;
using Daftar.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Thirty-eight handlers across the view models write the exception message into a status field
/// and swallow it, so a posting that failed at nine in the evening left nothing behind but a line
/// of Arabic on screen, and the cause was gone once the window closed. The file sink was already
/// there and almost unused — only three startup lines ever reached it.
///
/// What belongs in the log is the faults that are the app's own: a locked database, a full disk,
/// a broken query. A <see cref="DomainException"/> is the app refusing on purpose, with a message
/// written for the user that they are already looking at, so recording it would only bury the real
/// faults under routine noise.
/// </summary>
public sealed class FaultLogTests
{
    private sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    private sealed class Capture : ILoggerProvider
    {
        public List<Entry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Entries, categoryName);

        public void Dispose() { }

        private sealed class Logger(List<Entry> entries, string category) : ILogger
        {
            // Explicit, because the interface carries the notNull constraint and an implicit
            // implementation here would not match it.
            IDisposable? ILogger.BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Add(new Entry(logLevel, $"{formatter(state, exception)} | {category}", exception));
        }
    }

    private static (ServiceProvider Services, Capture Capture) Build()
    {
        var capture = new Capture();
        var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(capture))
            .BuildServiceProvider();
        return (services, capture);
    }

    [Fact]
    public void A_System_Fault_Leaves_A_Trace_Behind()
    {
        var (services, capture) = Build();

        // What a locked or full disk actually raises when a posting goes wrong.
        var fault = new InvalidOperationException("database is locked");

        services.Report("Post sale", fault);

        var entry = Assert.Single(capture.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("Post sale", entry.Message, StringComparison.Ordinal);
        Assert.Same(fault, entry.Exception);
    }

    [Fact]
    public void A_Rule_Refusal_Is_Left_Out()
    {
        var (services, capture) = Build();

        // Already on screen, in Arabic, saying exactly what to do about it.
        services.Report("Post sale", new DomainException("الرصيد أكبر من حد الائتمان."));

        Assert.Empty(capture.Entries);
    }

    [Fact]
    public void A_Store_With_No_Logger_Is_Survived_Quietly()
    {
        // The view models are also built by tests and by headless rendering, where nothing
        // registered logging. Reporting a fault must not turn a caught error into a crash.
        // Reaching the next line is the whole assertion.
        var bare = new ServiceCollection().BuildServiceProvider();

        bare.Report("Post sale", new InvalidOperationException("no logger here"));
    }

    [Fact]
    public async Task A_View_Model_That_Fails_Actually_Reaches_The_Log()
    {
        // The other tests here call the helper directly, which proves nothing about the
        // forty-five call sites. This one drives a real view model over a database that cannot
        // be opened — the shape of the fault the log exists for — and asks the log whether it
        // heard about it.
        var capture = new Capture();
        var services = new ServiceCollection()
            .AddDaftarPersistence(Path.Combine(Path.GetTempPath(), "no-such-dir-", "x.db"))
            .AddLogging(b => b.AddProvider(capture))
            .BuildServiceProvider();

        var vm = new DashboardViewModel(services);
        await vm.LoadAsync();

        // Still reported to the user, which is what the catch was already doing.
        Assert.Contains("خطأ", vm.BusyText, StringComparison.Ordinal);

        // EF logs its own connection retries, so pick out the one the view model wrote.
        var mine = capture.Entries
            .Where(e => e.Message.EndsWith("| Daftar", StringComparison.Ordinal))
            .ToArray();

        var entry = Assert.Single(mine);
        Assert.Contains("Dashboard.LoadAsync", entry.Message, StringComparison.Ordinal);
        Assert.IsType<SqliteException>(entry.Exception);
    }
}
