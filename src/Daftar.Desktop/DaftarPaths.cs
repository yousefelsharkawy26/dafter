using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Daftar;

/// <summary>
/// Where the database file lives. Per-user application data, so two Windows users on the same
/// machine do not share a ledger.
/// </summary>
public static class DaftarPaths
{
    public static string AppDataDirectory { get; } = ResolveAppDataDirectory();

    public static string DatabasePath { get; } = Path.Combine(AppDataDirectory, "daftar.db");

    /// <summary>Where snapshots are written. Shared by the manual backup and the pre-migration one.</summary>
    public static string BackupDirectory { get; } = Path.Combine(AppDataDirectory, "Backups");

    /// <summary>Where the rolling log files are kept.</summary>
    public static string LogDirectory { get; } = Path.Combine(AppDataDirectory, "logs");

    private static string ResolveAppDataDirectory()
    {
        var root = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_DATA_HOME")
              ?? Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                  ".local", "share");

        var directory = Path.Combine(root, "Daftar");
        Directory.CreateDirectory(directory);
        return directory;
    }
}