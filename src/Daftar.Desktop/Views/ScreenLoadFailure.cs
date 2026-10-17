using System;
using System.IO;
using Daftar.Infrastructure;

namespace Daftar.Views;

/// <summary>
/// Where a screen-load failure goes. Split out from <see cref="ScreenLoad"/> so the screen helper
/// has no opinion about logging and the tests can see what was recorded.
/// </summary>
internal static class ScreenLoadFailure
{
    /// <summary>Raised for each failure, so tests can observe one without touching the log file.</summary>
    public static event Action<Exception>? Recorded;

    /// <summary>The failure that has happened, for a test to read back.</summary>
    public static Exception? Last { get; private set; }

    public static void Record(Exception ex)
    {
        Last = ex;

        try
        {
            var dir = DaftarPaths.LogDirectory;
            Directory.CreateDirectory(dir);

            File.AppendAllText(
                Path.Combine(dir, $"daftar-{DateTime.Now:yyyy-MM-dd}.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERROR] Screen failed to load" +
                Environment.NewLine + ex + Environment.NewLine);
        }
        catch
        {
            // A log that cannot be written must not become the reason the screen dies.
        }

        Recorded?.Invoke(ex);
    }

    /// <summary>Test seam. Not called by the application.</summary>
    internal static void Reset()
    {
        Last = null;
        Recorded = null;
    }
}
