using System;
using Daftar.Domain.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Daftar.Desktop.Diagnostics;

/// <summary>
/// Records the faults that are the app's own, and only those.
///
/// <para>
/// The view models hand almost every exception to a status field and go no further, which reads
/// fine until a posting fails at nine in the evening: the user sees one Arabic line, closes the
/// window, and the cause — a locked database, a full disk, a broken query — is gone. That is the
/// question the log file exists to answer, and with only three startup lines ever written to it,
/// it could not.
/// </para>
///
/// <para>
/// What does not go in is a <see cref="DomainException"/>. Those are the app refusing on purpose,
/// carrying a message written for the user that they are already looking at. Logging them would
/// bury the faults worth finding under a line for every unpriced item and every balance over its
/// limit — the noise would cost more than the record is worth.
/// </para>
///
/// <para>
/// Reporting must never itself fail. A view model reaches this from inside a catch block, where a
/// second exception would replace the real error with one about the logger, and a container built
/// without logging at all must simply get nothing.
/// </para>
/// </summary>
internal static class FaultLog
{
    public static void Report(this IServiceProvider services, string activity, Exception ex)
    {
        if (ex is DomainException)
            return;

        try
        {
            services.GetService<ILoggerFactory>()?
                .CreateLogger("Daftar")
                .LogError(ex, "{Activity}", activity);
        }
        catch
        {
            // Deliberately empty: see the note above.
        }
    }
}