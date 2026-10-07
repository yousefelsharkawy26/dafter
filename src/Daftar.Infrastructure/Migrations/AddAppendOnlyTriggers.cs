using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Daftar.Infrastructure.Migrations;

/// <summary>
/// Append-only enforcement, in one place.
///
/// <para>
/// These triggers cannot live in a normal migration: SQLite does not allow <c>CREATE TRIGGER</c>
/// inside a transaction, and EF wraps every migration in one. A <c>SuppressTransaction</c>
/// migration is the only way to run DDL of this kind, so all four ledgers are handled together
/// here rather than scattered across the migrations that introduced each table.
/// </para>
///
/// <para>
/// This guards against bugs and stray edits. It is not security: someone with file access can
/// drop the triggers. Backups are what cover that.
/// </para>
/// </summary>
[DbContext(typeof(DaftarContext))]
[Migration("20261005070000_AddAppendOnlyTriggers")]
public partial class AddAppendOnlyTriggers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[]
                 {
                     "AuditLogs",
                     "InventoryMovements",
                     "PartyAccountTransactions",
                     "CashTransactions",
                 })
        {
            Guard(migrationBuilder, table);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[]
                 {
                     "AuditLogs",
                     "InventoryMovements",
                     "PartyAccountTransactions",
                     "CashTransactions",
                 })
        {
            migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_{table}_no_update;");
            migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_{table}_no_delete;");
        }
    }

    private static void Guard(MigrationBuilder migrationBuilder, string table)
    {
        Trigger(migrationBuilder, $"trg_{table}_no_update", table, $"UPDATE ON {table}");
        Trigger(migrationBuilder, $"trg_{table}_no_delete", table, $"DELETE ON {table}");
    }

    private static void Trigger(
        MigrationBuilder migrationBuilder,
        string triggerName,
        string tableName,
        string clause)
    {
        // SuppressTransaction is required: SQLite refuses CREATE TRIGGER inside a transaction, and EF
        // wraps a migration in one by default. SqlOperation takes its SQL via a settable property,
        // and MigrationBuilder.Operations is public, so the operation is built directly.
        migrationBuilder.Operations.Add(new SqlOperation
        {
            Sql = $"""
                CREATE TRIGGER IF NOT EXISTS {triggerName} BEFORE {clause}
                BEGIN SELECT RAISE(ABORT, '{tableName} is append-only'); END;
                """,
            SuppressTransaction = true,
        });
    }
}