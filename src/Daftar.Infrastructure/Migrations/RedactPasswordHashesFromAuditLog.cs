using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Daftar.Infrastructure.Migrations;

/// <summary>
/// Scrubs password hashes that the audit interceptor copied into the audit trail.
///
/// <para>
/// AuditStampInterceptor serialises every changed column of an audited entity, and User was in
/// that set. So creating a user, changing a password, or an admin resetting one wrote the full
/// <c>pbkdf2-sha256$iterations$salt$hash</c> string into <c>AuditLogs.NewValues</c> as plain JSON.
/// That table is append-only at the database level, so the copy was permanent: changing the
/// password again did not remove the old hash, and the trigger blocks deleting the row.
/// </para>
///
/// <para>
/// The interceptor now redacts the column, so the defect cannot recur. This migration deals with
/// the copies already on disk. AuditLogs is append-only, so the same drop/recreate dance that
/// <see cref="RepairStalePurchaseLedgerSigns"/> uses is required, and it is applied per row inside
/// the DROP..CREATE window.
/// </para>
///
/// <remarks>
/// Rewriting history is not something to do silently, so the interceptor redaction and this
/// cleanup land together: after this runs, the audit trail records that a credential changed
/// without ever recording what it changed to.
/// </remarks>
[DbContext(typeof(DaftarContext))]
[Migration("20261010190000_RedactPasswordHashesFromAuditLog")]
public partial class RedactPasswordHashesFromAuditLog : Migration
{
    /// <summary>The column the interceptor used to copy the hash into.</summary>
    private const string NewValuesColumn = "NewValues";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite refuses CREATE/DROP TRIGGER inside a transaction, which is what EF wraps a
        // migration in, so each operation has to declare SuppressTransaction.
        //
        // The guard column is stored as JSON, so the match is on the property name appearing in
        // it. Replace the whole payload rather than trying to edit JSON in SQL: the audit value
        // of a credential change is "this happened", and keeping the rest of the row intact is
        // not worth a malformed-JSON risk.
        migrationBuilder.Operations.Add(new SqlOperation
        {
            Sql = "DROP TRIGGER IF EXISTS trg_AuditLogs_no_update;",
            SuppressTransaction = true,
        });

        migrationBuilder.Operations.Add(new SqlOperation
        {
            Sql = "UPDATE AuditLogs "
               + $"SET {NewValuesColumn} = '{{\"PasswordHash\":\"(redacted)\"}}' "
               + $"WHERE {NewValuesColumn} LIKE '%pbkdf2-sha256$%' "
               + $"OR {NewValuesColumn} LIKE '%\"PasswordHash\"%';",
            SuppressTransaction = true,
        });

        migrationBuilder.Operations.Add(new SqlOperation
        {
            Sql = """
                CREATE TRIGGER IF NOT EXISTS trg_AuditLogs_no_update BEFORE UPDATE ON AuditLogs
                BEGIN SELECT RAISE(ABORT, 'AuditLogs is append-only'); END;
                """,
            SuppressTransaction = true,
        });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The hashes are unrecoverable by design -- that is the point of scrubbing them -- so this
        // migration is intentionally irreversible. There is nothing meaningful to restore.
        throw new NotSupportedException(
            "Password hashes cannot be recovered once redacted. Re-scrubbing is a no-op.");
    }
}