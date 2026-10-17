using System;
using System.Collections.Generic;

namespace Daftar.Desktop;

/// <summary>
/// Decides whether a repeated failure deserves another dialog.
///
/// <para>
/// The dispatcher safety net marks exceptions handled so one bad row cannot take the process down.
/// That is right once and disastrous in a loop: an exception thrown inside a render pass fires
/// every frame, and each firing opened a fresh modal window. The cashier got an uncloseable flood
/// instead of the black window the net was meant to prevent, and the app became less usable than
/// if it had simply died.
/// </para>
///
/// <para>
/// So the first occurrence speaks and the repeats stay in the log. Same exception, same message:
/// saying it again teaches the user nothing, because they cannot act differently the second time.
/// A different failure is a different problem and does get its own dialog.
/// </para>
/// </summary>
internal static class RepeatedFault
{
    private static readonly HashSet<string> Spoken = [];

    /// <summary>
    /// True the first time this exact failure is seen, false for every repeat.
    /// </summary>
    public static bool ShouldSpeak(Exception ex)
    {
        // The type alone is too coarse (two different bad rows are both FormatException) and the
        // message alone too fine (every row carries its own value). Together they name one fault.
        return Spoken.Add(ex.GetType().FullName + "\n" + ex.Message);
    }

    /// <summary>Test seam. Not called by the application.</summary>
    internal static void Reset() => Spoken.Clear();
}
