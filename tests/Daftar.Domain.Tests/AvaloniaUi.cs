using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Threading;

namespace Daftar.Domain.Tests;

/// <summary>
/// Runs view code on the thread that owns the Avalonia UI thread.
///
/// Two constraints shaped this. The headless platform binds its dispatcher to whichever thread
/// set it up, and every UI call afterwards has to come from that same thread -- xUnit hands a
/// fresh pool thread to each test, so the thread is created here. And Avalonia refuses to be set
/// up twice in a process, so the setup happens once and the tests queue work onto it rather than
/// each starting their own.
///
/// This exists instead of Avalonia.Headless.XUnit because that package drags in xunit v3, which
/// cannot coexist with the v2 the rest of this suite runs on. Migrating 300+ tests to v3 to
/// render two screens would be a far bigger change than it looks.
/// </summary>
internal static class AvaloniaUi
{
    private sealed record Job(Action<Window> Body, TaskCompletionSource<Exception?> Done);

    private static readonly BlockingCollection<Job> Queue = new();
    private static readonly Lock StartLock = new();
    private static Thread? _thread;

    public static void Run(Action<Window> body)
    {
        EnsureStarted();

        var job = new Job(body, new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously));

        Queue.Add(job);

        var failure = job.Done.Task.GetAwaiter().GetResult();
        if (failure is not null)
            throw new InvalidOperationException("The view failed on the UI thread.", failure);
    }

    /// <summary>Pumps the dispatcher and settles layout, so a rendered frame means something.</summary>
    public static void Settle()
    {
        // Already on the UI thread -- which is the case whenever this is called from inside a
        // Run body. Queueing here and then waiting for the result would ask the UI thread to
        // wait for itself, which is a deadlock rather than a slow test. Pump directly instead.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
            return;
        }

        EnsureStarted();
        var job = new Job(_ => { }, new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously));

        Queue.Add(job);

        var failure = job.Done.Task.GetAwaiter().GetResult();
        if (failure is not null)
            throw new InvalidOperationException("Pumping the dispatcher failed.", failure);
    }

    public static void EnsureStarted()
    {
        if (_thread is { IsAlive: true })
            return;

        lock (StartLock)
        {
            if (_thread is { IsAlive: true })
                return;

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "avalonia-ui",
            };

            // STA only matters on Windows, where the app ships. Calling it elsewhere throws
            // rather than degrading, so it is guarded rather than swallowed.
            if (OperatingSystem.IsWindows())
                _thread.SetApartmentState(ApartmentState.STA);

            _thread.Start();
        }
    }

    private static void Loop()
    {
        try
        {
            // The real App, the real Fluent theme, the real Arabic font registration -- only the
            // surface differs from what ships.
            //
            // UseHeadlessDrawing is false on purpose: that option skips rendering altogether,
            // which is right for interaction tests and useless here because every frame comes
            // back null. Leaving it off runs the real Skia renderer against an offscreen target.
            Program.ConfigureBuilder()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .AfterSetup(_ => Daftar.Fonts.Register())
                .SetupWithoutStarting();
        }
        catch (Exception ex)
        {
            // Nothing can run without a platform; fail every waiting job rather than hanging.
            while (Queue.TryTake(out var pending))
                pending.Done.SetResult(ex);

            return;
        }

        foreach (var job in Queue.GetConsumingEnumerable())
        {
            Exception? failure = null;
            try
            {
                job.Body(new Window { Width = 1400, Height = 950 });
                Dispatcher.UIThread.RunJobs();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            job.Done.SetResult(failure);
        }
    }
}
