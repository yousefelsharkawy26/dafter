using System;
using Daftar.Desktop;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// A fault that repeats does not open a second dialog.
/// </summary>
/// <para>
/// The dispatcher net marks unhandled exceptions handled so one bad row cannot kill the process.
/// Handled is right once and fatal in a loop. An exception thrown inside a render pass fires every
/// frame, and the handler opened a modal window each time — so the cashier got an uncloseable
/// flood, which is a worse failure than the black window the net was added to stop. Found by
/// reading the handler, not by a test: nothing was failing, the program was simply unusable.
/// </para>
/// </summary>
public sealed class RepeatedFaultTests : IDisposable
{
    public RepeatedFaultTests() => RepeatedFault.Reset();

    public void Dispose() => RepeatedFault.Reset();

    [Fact]
    public void The_First_Occurrence_Speaks()
    {
        Assert.True(RepeatedFault.ShouldSpeak(new InvalidOperationException("boom")));
    }

    [Fact]
    public void The_Same_Fault_Again_Does_Not()
    {
        var first = RepeatedFault.ShouldSpeak(new InvalidOperationException("boom"));
        var second = RepeatedFault.ShouldSpeak(new InvalidOperationException("boom"));
        var sixtieth = RepeatedFault.ShouldSpeak(new InvalidOperationException("boom"));

        Assert.True(first);
        Assert.False(second);
        Assert.False(sixtieth);
    }

    [Fact]
    public void A_Different_Fault_Still_Speaks_For_Itself()
    {
        // The other half: collapsing every exception into one silence would hide a second, unrelated
        // problem behind the first.
        Assert.True(RepeatedFault.ShouldSpeak(new InvalidOperationException("boom")));
        Assert.True(RepeatedFault.ShouldSpeak(new InvalidOperationException("different message")));
        Assert.True(RepeatedFault.ShouldSpeak(new FormatException("boom")));
    }

    [Fact]
    public void Sixty_Frames_Worth_Of_One_Fault_Produce_One_Dialog()
    {
        // The shape of the real thing: a render pass throwing every frame.
        var dialogs = 0;

        for (var frame = 0; frame < 60; frame++)
        {
            if (RepeatedFault.ShouldSpeak(new InvalidOperationException("NullReference in Render")))
                dialogs++;
        }

        Assert.Equal(1, dialogs);
    }
}
