using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Daftar.Views;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// A screen that cannot load says so, and the application lives through it.
///
/// <para>
/// Eleven screens load themselves from a <c>async void</c> handler, and nothing on the dispatcher
/// was catching. One unreadable row in the ledger escaped into the dispatcher loop and ended the
/// process: the window went black and the cashier was left looking at it. Found by planting a bad
/// row and photographing the result, which no test had done.
/// </para>
/// </summary>
public sealed class ScreenLoadTests : IDisposable
{
    private readonly List<string> _notified = [];
    private readonly Action<string> _originalNotifier;

    public ScreenLoadTests()
    {
        ScreenLoadFailure.Reset();
        _originalNotifier = ScreenLoad.Notifier;
        ScreenLoad.Notifier = _notified.Add;
    }

    public void Dispose()
    {
        ScreenLoad.Notifier = _originalNotifier;
        ScreenLoadFailure.Reset();
    }

    [Fact]
    public async Task A_Load_That_Fails_Is_Reported_And_Does_Not_Throw()
    {
        // The throwing load is the whole point: unwrapped, this is what kills the process.
        var thrown = new FormatException("String '1' was not recognized as a valid DateTime.");

        var ok = await ScreenLoad.Run(() => throw thrown);

        Assert.False(ok);
        Assert.Same(thrown, ScreenLoadFailure.Last);
    }

    [Fact]
    public async Task A_Screen_With_No_Status_Line_Still_Tells_Someone()
    {
        // Six of the eleven have no status line. Silence there would leave the cashier looking at
        // an empty screen with nothing to act on, which is the same failure wearing a nicer coat.
        await ScreenLoad.Run(() => throw new InvalidOperationException("bad row"));

        var message = Assert.Single(_notified);
        Assert.Contains("bad row", message);
    }

    [Fact]
    public async Task The_Reason_Reaches_The_Status_Line_Not_Just_The_Log()
    {
        // A failure nobody can see is the failure this exists to prevent.
        string? shown = null;

        await ScreenLoad.Run(() => throw new InvalidOperationException("boom"), m => shown = m);

        Assert.NotNull(shown);
        Assert.Contains("boom", shown);
        Assert.StartsWith(ScreenLoad.FailedMessage[..20], shown);
    }

    [Fact]
    public async Task A_Status_Line_That_Throws_Does_Not_Take_The_Process_Down()
    {
        // Catching the load and then throwing from the reporter would be the same crash wearing a
        // different hat.
        var ok = await ScreenLoad.Run(
            () => throw new InvalidOperationException("load"),
            _ => throw new NotSupportedException("reporter"));

        Assert.False(ok);
        Assert.IsType<InvalidOperationException>(ScreenLoadFailure.Last);
    }

    [Fact]
    public async Task A_Load_That_Works_Reports_Nothing()
    {
        var ok = await ScreenLoad.Run(() => Task.CompletedTask, _ => throw new Exception("reported"));

        Assert.True(ok);
        Assert.Null(ScreenLoadFailure.Last);
    }
}
