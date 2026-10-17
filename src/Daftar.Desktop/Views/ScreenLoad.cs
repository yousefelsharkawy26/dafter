using System;
using System.Threading.Tasks;

namespace Daftar.Views;

/// <summary>
/// Runs a screen's initial load so a failure is reported instead of ending the process.
///
/// <para>
/// Eleven screens load themselves from a <c>DataContextChanged</c> handler written as
/// <c>async void</c>, and there was no unhandled-exception handler on the dispatcher. A single
/// unreadable row anywhere in the ledger therefore escaped into the dispatcher loop and took the
/// whole application down: the window went black, the cashier was left staring at it, and the
/// only trace was a line in a log file nobody opens.
/// </para>
///
/// <para>
/// The load still fails. What changes is that it fails on screen, with the reason, and the rest of
/// the application keeps running — which matters here because a screen that cannot show its
/// products is an inconvenience and a dead window is a closed shop.
/// </para>
/// </summary>
internal static class ScreenLoad
{
    /// <summary>The message shown when a screen cannot load. Egyptian Arabic, like the rest.</summary>
    public const string FailedMessage =
        "مقدرناش نفتح البيانات ديال الشاشة ديالك. اتقفلت الصفحة ديالها بس باقي التطبيق شغال.";

    /// <summary>The dialog title used when a screen has no status line to write on.</summary>
    public const string DialogTitle = "الصفحة ديالها ما فتحتش";

    /// <summary>
    /// What a screen without a status line falls back to.
    ///
    /// <para>
    /// Indirection rather than a direct call to <see cref="Desktop.StartupError"/>: touching the
    /// dispatcher from a thread that does not own it throws, and that throw here would initialise
    /// Avalonia on the wrong thread and take every headless UI test down with it. The screen helper
    /// should not know which dialog it opens.
    /// </para>
    /// </summary>
    public static Action<string> Notifier { get; set; } =
        message => Desktop.StartupError.Show(DialogTitle, message);

    /// <summary>
    /// Awaits <paramref name="load"/>, and returns false if it threw.
    /// </summary>
    /// <param name="load">The screen's load.</param>
    /// <param name="report">
    /// Where to put the reason. The ViewModels that have a status line pass their Status setter so
    /// the message stays on screen after this returns; the ones that do not get a dialog, because a
    /// failure nobody can see is the failure this exists to prevent.
    /// </param>
    public static async Task<bool> Run(Func<Task> load, Action<string>? report = null)
    {
        try
        {
            await load();
            return true;
        }
        catch (Exception ex)
        {
            ScreenLoadFailure.Record(ex);

            var message = $"{FailedMessage}{Environment.NewLine}{Environment.NewLine}{ex.Message}";

            try
            {
                if (report is not null)
                    report(message);
                else
                    Notifier(message);
            }
            catch (Exception)
            {
                // A status setter that throws would take the process down here, which is the
                // very thing this exists to stop.
            }

            return false;
        }
    }
}
