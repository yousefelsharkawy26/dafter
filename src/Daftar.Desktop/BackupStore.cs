using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daftar.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Daftar;

/// <summary>One snapshot on disk, ready to show in the backup list.</summary>
public sealed record BackupFile(string Name, string Path, DateTime CreatedAt, long SizeBytes)
{
    public string CreatedDisplay => CreatedAt.ToString("dd/MM/yyyy - HH:mm", CultureInfo.InvariantCulture);

    public string SizeDisplay => SizeBytes >= 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{SizeBytes / (1024.0 * 1024.0):0.#} ميجا")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, SizeBytes / 1024)} كيلوبايت");
}

/// <summary>What <see cref="BackupStore.ApplyPendingRestore"/> did, in words the user can read.</summary>
public sealed record RestoreOutcome(bool Restored, string Message, string? SafetyCopyPath = null);

/// <summary>
/// Snapshots of the ledger, and the only way back into one.
///
/// Taking a backup was already possible, and nothing could use one: the startup failure dialog
/// told the user to open a file from the Backups folder, which SQLite will not open in place of a
/// live database. So a restore point was a decoration. This is the other half.
///
/// The restore itself cannot run while the app is up — the database file would be swapped out
/// from under open connections — so <see cref="ScheduleRestore"/> only writes a marker, and
/// <see cref="ApplyPendingRestore"/> runs at the next start, before the container is built.
/// </summary>
public sealed class BackupStore
{
    /// <summary>How many snapshots are kept. Enough to notice a bad day, few enough to not fill a disk.</summary>
    public const int DefaultKeep = 20;

    private const string ManualPrefix = "daftar_backup_";
    private const string PreMigratePrefix = "pre-migrate_";
    private const string PreRestorePrefix = "pre-restore_";

    /// <summary>
    /// Pruning only ever considers files this app wrote under these names. A user who keeps
    /// something else in the folder keeps it, even when it is the oldest file there.
    /// </summary>
    private static readonly string[] OwnedPrefixes = [ManualPrefix, PreMigratePrefix, PreRestorePrefix];

    private readonly string _backupDirectory;
    private readonly string _databasePath;
    private readonly string _markerPath;

    public BackupStore(string backupDirectory, string databasePath, string? markerPath = null)
    {
        _backupDirectory = backupDirectory;
        _databasePath = databasePath;
        _markerPath = markerPath ?? databasePath + ".restore-pending";
    }

    /// <summary>The store the running app uses.</summary>
    public static BackupStore ForApp => new(DaftarPaths.BackupDirectory, DaftarPaths.DatabasePath);

    /// <summary>Snapshots we own, newest first.</summary>
    public IReadOnlyList<BackupFile> List()
    {
        if (!Directory.Exists(_backupDirectory))
            return [];

        var files = new List<BackupFile>();
        foreach (var path in Directory.EnumerateFiles(_backupDirectory))
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!OwnedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                continue;

            var info = new FileInfo(path);
            files.Add(new BackupFile(name, path, info.LastWriteTime, info.Length));
        }

        files.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return files;
    }

    /// <summary>
    /// Deletes the oldest snapshots past <paramref name="keep"/>. Returns how many went, so the
    /// caller can say so out loud rather than quietly eating the user's history.
    /// </summary>
    public int Prune(int keep = DefaultKeep)
    {
        if (keep < 1)
            throw new ArgumentOutOfRangeException(nameof(keep), "لازم نسيب نسخة واحدة على الأقل.");

        var all = List();
        if (all.Count <= keep)
            return 0;

        var removed = 0;
        foreach (var file in all.Skip(keep))
        {
            try
            {
                File.Delete(file.Path);
                removed++;
            }
            catch (IOException)
            {
                // Locked or in use. Leaving it is the safe outcome; the next prune tries again.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>Writes a consistent snapshot of the live database.</summary>
    public async Task<BackupFile> CreateAsync(
        DaftarContext context,
        string prefix = ManualPrefix,
        CancellationToken ct = default)
        => await CreateAtAsync(
            context,
            Path.Combine(_backupDirectory, prefix + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db"),
            ct);

    /// <summary>
    /// Writes a consistent snapshot to a path the caller chose. The pre-migration backup names its
    /// own file before it gets here, so both callers share one implementation.
    /// </summary>
    public async Task<BackupFile> CreateAtAsync(
        DaftarContext context,
        string destination,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // VACUUM INTO takes a literal path and cannot be parameterised. Every destination here is
        // built from the app data directory and a timestamp, so nothing a user typed reaches the
        // SQL; the quote is doubled anyway rather than relying on that reasoning alone.
#pragma warning disable EF1002 // VACUUM INTO has no parameter form.
        var escaped = destination.Replace("'", "''");
        await context.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escaped}';", ct);
#pragma warning restore EF1002

        if (!File.Exists(destination))
            throw new IOException("النسخة الاحتياطية ما اتكتبتش. تأكد إن في مساحة على القرص.");

        var info = new FileInfo(destination);
        return new BackupFile(Path.GetFileName(destination), destination, info.LastWriteTime, info.Length);
    }

    /// <summary>
    /// Records the snapshot to restore from. Does not touch the live database — the swap happens
    /// at the next start, in <see cref="ApplyPendingRestore"/>.
    /// </summary>
    public void ScheduleRestore(string backupPath)
    {
        var full = Path.GetFullPath(backupPath);

        // The marker is a plain text file holding a path. It is written by this app, but it also
        // survives on disk between runs and between users; checking that it stays inside the
        // backup folder is what stops a tampered marker from naming an arbitrary file for
        // overwrite.
        if (!IsInside(full, _backupDirectory))
            throw new InvalidOperationException("النسخة المختارة بره مجلد النسخ الاحتياطية.");

        if (!File.Exists(full))
            throw new FileNotFoundException("ملف النسخة الاحتياطية مش موجود.", full);

        if (!IsReadableDatabase(full))
            throw new InvalidOperationException(
                "النسخة دي مش قاعدة بيانات سليمة. متحطهاش مكان البيانات الأصلية.");

        Directory.CreateDirectory(_backupDirectory);
        File.WriteAllText(_markerPath, full, Encoding.UTF8);
    }

    /// <summary>The snapshot waiting to be restored, or null when there is none.</summary>
    public string? PendingRestore()
    {
        try
        {
            return File.Exists(_markerPath)
                ? File.ReadAllText(_markerPath, Encoding.UTF8).Trim() is { Length: > 0 } p ? p : null
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void CancelPendingRestore()
    {
        if (!File.Exists(_markerPath))
            return;

        try
        {
            File.Delete(_markerPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Swaps the live database for the snapshot named by the marker. Called once at startup with
    /// no connection open. The database being replaced is copied aside first, so a restore is
    /// itself reversible — the operator can undo a wrong choice from the same screen.
    /// </summary>
    public RestoreOutcome ApplyPendingRestore()
    {
        if (!File.Exists(_markerPath))
            return new RestoreOutcome(false, "مفيش نسخة اتحددت للاستعادة.");

        string source;
        try
        {
            source = File.ReadAllText(_markerPath, Encoding.UTF8).Trim();
        }
        catch (IOException ex)
        {
            return new RestoreOutcome(false, $"تعذر قراءة طلب الاستعادة: {ex.Message}");
        }

        if (source.Length == 0 || !IsInside(Path.GetFullPath(source), _backupDirectory)
            || !File.Exists(source))
        {
            CancelPendingRestore();
            return new RestoreOutcome(false, "النسخة المطلوبة مش موجودة، والاستعادة اتلغت.");
        }

        if (!IsReadableDatabase(source))
        {
            CancelPendingRestore();
            return new RestoreOutcome(false, "النسخة المطلوبة فيها خلل، والاستعادة اتلغت.");
        }

        string? safetyCopy = null;
        try
        {
            if (File.Exists(_databasePath))
                safetyCopy = CopyCurrentAside();

            // A stale write-ahead log belongs to the file being replaced. Left in place, SQLite
            // would replay it on top of the restored database and produce a ledger that is
            // neither the old one nor the new one.
            foreach (var sidecar in new[] { _databasePath + "-wal", _databasePath + "-shm" })
                if (File.Exists(sidecar))
                    File.Delete(sidecar);

            File.Copy(source, _databasePath, overwrite: true);
        }
        catch (IOException ex)
        {
            return new RestoreOutcome(
                false,
                $"الاستعادة ما اتنفذتش: {ex.Message}",
                safetyCopy);
        }

        CancelPendingRestore();

        return new RestoreOutcome(
            true,
            safetyCopy is null
                ? "تمت الاستعادة. البياناترجعت لحالة النسخة المختارة."
                : $"تمت الاستعادة. البيانات اللي كانت موجودة اتحفظت في {Path.GetFileName(safetyCopy)}.",
            safetyCopy);
    }

    /// <summary>
    /// Preserves the database being replaced, together with its write-ahead log. A plain file
    /// copy without the log is not a consistent snapshot, so both are kept under matching names.
    /// </summary>
    private string? CopyCurrentAside()
    {
        Directory.CreateDirectory(_backupDirectory);

        var name = PreRestorePrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db";
        var destination = Path.Combine(_backupDirectory, name);

        File.Copy(_databasePath, destination);

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = _databasePath + suffix;
            if (File.Exists(sidecar))
                File.Copy(sidecar, destination + suffix);
        }

        return File.Exists(destination) ? destination : null;
    }

    /// <summary>
    /// Opens a file read-only and asks SQLite whether its pages are intact. Restoring replaces the
    /// only good copy, so a snapshot that fails this is refused rather than swapped in.
    /// </summary>
    private static bool IsReadableDatabase(string path)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return string.Equals(
                Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture),
                "ok",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsInside(string candidate, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));

        return relative.Length > 0
               && !Path.IsPathRooted(relative)
               && !relative.StartsWith("..", StringComparison.Ordinal);
    }
}
