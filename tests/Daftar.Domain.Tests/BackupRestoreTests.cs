using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Backups were written but nothing could read one back: the failure dialog told the operator to
/// open a file from the Backups folder, which SQLite will not load in place of a live database.
/// These pin the restore path that replaced that dead end.
/// </summary>
public sealed class BackupRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "daftar-backup-tests", Guid.NewGuid().ToString("N"));

    private string BackupDirectory => Path.Combine(_root, "Backups");
    private string DatabasePath => Path.Combine(_root, "daftar.db");

    private BackupStore Store => new(BackupDirectory, DatabasePath);

    public BackupRestoreTests() => Directory.CreateDirectory(BackupDirectory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pooling is off on purpose. A pooled connection keeps the replaced inode alive, so a read
    /// after File.Copy would report the file that was just overwritten rather than the new one —
    /// which would make these tests pass while the restore did nothing.
    /// </summary>
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void WriteDatabase(string path, string marker)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        // The marker is a literal written by the test, never anything a user supplied.
        command.CommandText = $"CREATE TABLE t(v TEXT); INSERT INTO t VALUES ('{marker}');";
        command.ExecuteNonQuery();
    }

    private static string ReadMarker(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v FROM t;";
        return (string?)command.ExecuteScalar() ?? string.Empty;
    }

    private string Backup(string name, string marker)
    {
        var path = Path.Combine(BackupDirectory, name);
        WriteDatabase(path, marker);
        return path;
    }

    // ── restore ────────────────────────────────────────────────────────────────

    [Fact]
    public void Restoring_Puts_The_Chosen_Contents_Back()
    {
        WriteDatabase(DatabasePath, "live");
        var backup = Backup("daftar_backup_20260101_100000.db", "old");

        var store = Store;
        store.ScheduleRestore(backup);

        // Scheduling alone must not touch the live database: the app is still running behind
        // this screen, and the swap belongs to the next start.
        Assert.Equal("live", ReadMarker(DatabasePath));
        Assert.Equal(backup, store.PendingRestore());

        Assert.True(store.ApplyPendingRestore().Restored);
        Assert.Equal("old", ReadMarker(DatabasePath));
        Assert.Null(store.PendingRestore());
    }

    [Fact]
    public void Restoring_Keeps_A_Copy_Of_What_It_Replaced()
    {
        // A restore that cannot be undone is a mistake waiting to happen: the operator picks the
        // wrong day and the only copy of today is gone.
        WriteDatabase(DatabasePath, "live");
        var store = Store;
        store.ScheduleRestore(Backup("daftar_backup_20260101_100000.db", "old"));

        var outcome = store.ApplyPendingRestore();

        Assert.True(outcome.Restored);
        Assert.NotNull(outcome.SafetyCopyPath);
        Assert.True(File.Exists(outcome.SafetyCopyPath));
        Assert.StartsWith("pre-restore_", Path.GetFileName(outcome.SafetyCopyPath));

        // And the copy really holds what was there before, so it is usable for the undo.
        Assert.Equal("live", ReadMarker(outcome.SafetyCopyPath!));
    }

    [Fact]
    public void Restoring_Removes_The_Stale_Write_Ahead_Log()
    {
        // SQLite replays a leftover -wal on top of whatever file it opens. Left behind from the
        // database being replaced, it would graft yesterday's rows onto today's restored file.
        WriteDatabase(DatabasePath, "live");
        File.WriteAllText(DatabasePath + "-wal", "leftover");
        File.WriteAllText(DatabasePath + "-shm", "leftover");

        var store = Store;
        store.ScheduleRestore(Backup("daftar_backup_20260101_100000.db", "old"));
        Assert.True(store.ApplyPendingRestore().Restored);

        Assert.False(File.Exists(DatabasePath + "-wal"));
        Assert.False(File.Exists(DatabasePath + "-shm"));
        Assert.Equal("old", ReadMarker(DatabasePath));
    }

    [Fact]
    public void Cancelling_Stops_The_Pending_Restore()
    {
        WriteDatabase(DatabasePath, "live");
        var store = Store;
        store.ScheduleRestore(Backup("daftar_backup_20260101_100000.db", "old"));

        store.CancelPendingRestore();

        Assert.Null(store.PendingRestore());
        Assert.False(store.ApplyPendingRestore().Restored);
        Assert.Equal("live", ReadMarker(DatabasePath));
    }

    [Fact]
    public void Starting_With_No_Marker_Touches_Nothing()
    {
        WriteDatabase(DatabasePath, "live");

        var outcome = Store.ApplyPendingRestore();

        Assert.False(outcome.Restored);
        Assert.Equal("live", ReadMarker(DatabasePath));
    }

    // ── refusing bad input ─────────────────────────────────────────────────────

    [Fact]
    public void A_File_That_Is_Not_A_Database_Is_Refused()
    {
        // The restore overwrites the only good copy. A truncated or wrong file would turn a
        // recoverable problem into an unrecoverable one.
        var junk = Path.Combine(BackupDirectory, "daftar_backup_20260101_100000.db");
        File.WriteAllText(junk, "this is not a database");

        var store = Store;
        Assert.Throws<InvalidOperationException>(() => store.ScheduleRestore(junk));
        Assert.Null(store.PendingRestore());
    }

    [Fact]
    public void A_Truncated_Database_Is_Refused()
    {
        var backup = Backup("daftar_backup_20260101_100000.db", "old");
        using (var stream = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.SetLength(stream.Length / 3);
        }

        var store = Store;
        Assert.Throws<InvalidOperationException>(() => store.ScheduleRestore(backup));
    }

    [Fact]
    public void A_Path_Outside_The_Backup_Folder_Is_Refused()
    {
        // The marker is a text file that outlives the process that wrote it. Without this check, a
        // tampered marker names any file on the disk for overwrite.
        WriteDatabase(DatabasePath, "live");
        var outside = Path.Combine(_root, "elsewhere.db");
        WriteDatabase(outside, "elsewhere");

        var marker = DatabasePath + ".restore-pending";
        File.WriteAllText(marker, outside);

        var outcome = Store.ApplyPendingRestore();

        Assert.False(outcome.Restored);
        Assert.Equal("live", ReadMarker(DatabasePath));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void A_Damaged_File_Named_By_The_Marker_Is_Refused_At_Startup()
    {
        // The schedule-time check is not the one that matters here: the marker can outlive the
        // process that wrote it, and the file it names can be truncated or replaced in between.
        // The check that runs at startup has to stand on its own.
        WriteDatabase(DatabasePath, "live");
        var backup = Backup("daftar_backup_20260101_100000.db", "old");
        using (var stream = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.SetLength(stream.Length / 3);
        }

        File.WriteAllText(DatabasePath + ".restore-pending", backup);

        var outcome = Store.ApplyPendingRestore();

        Assert.False(outcome.Restored);
        Assert.Equal("live", ReadMarker(DatabasePath));
        Assert.Null(Store.PendingRestore());
    }

    [Fact]
    public void A_Marker_Pointing_At_A_Deleted_File_Cancels_Itself()
    {
        WriteDatabase(DatabasePath, "live");
        var backup = Backup("daftar_backup_20260101_100000.db", "old");

        var store = Store;
        store.ScheduleRestore(backup);
        File.Delete(backup);

        var outcome = store.ApplyPendingRestore();

        Assert.False(outcome.Restored);
        Assert.Null(store.PendingRestore());
        Assert.Equal("live", ReadMarker(DatabasePath));
    }

    // ── listing and pruning ────────────────────────────────────────────────────

    [Fact]
    public void Pruning_Keeps_The_Newest_And_Leaves_Foreign_Files_Alone()
    {
        WriteDatabase(DatabasePath, "live");

        // Newest first by write time; names alone would tie on a fast filesystem.
        var old = Backup("daftar_backup_20260101_090000.db", "a");
        var middle = Backup("daftar_backup_20260102_090000.db", "b");
        var newest = Backup("daftar_backup_20260103_090000.db", "c");
        var preMigrate = Backup("pre-migrate_20260104_090000.db", "d");

        var foreign = Path.Combine(BackupDirectory, "my-own-notes.db");
        File.WriteAllText(foreign, "not ours");

        File.SetLastWriteTimeUtc(old, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(middle, new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newest, new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(preMigrate, new DateTime(2026, 1, 4, 9, 0, 0, DateTimeKind.Utc));

        var store = Store;
        Assert.Equal(4, store.List().Count);
        Assert.Equal(2, store.Prune(2));

        var left = store.List().Select(f => f.Name).ToArray();
        Assert.Equal(
            [Path.GetFileName(preMigrate), Path.GetFileName(newest)],
            left);
        Assert.True(File.Exists(foreign), "a file the app did not write must survive pruning");
    }

    [Fact]
    public void Pruning_Stops_When_It_Is_Already_Short_Enough()
    {
        Backup("daftar_backup_20260101_090000.db", "a");
        Backup("daftar_backup_20260102_090000.db", "b");

        var store = Store;
        Assert.Equal(0, store.Prune(5));
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Listing_Is_Newest_First()
    {
        var older = Backup("daftar_backup_20260101_090000.db", "a");
        var newer = Backup("daftar_backup_20260102_090000.db", "b");

        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc));

        var listed = Store.List().Select(f => f.Name).ToArray();

        Assert.Equal(["daftar_backup_20260102_090000.db", "daftar_backup_20260101_090000.db"], listed);
    }
}
