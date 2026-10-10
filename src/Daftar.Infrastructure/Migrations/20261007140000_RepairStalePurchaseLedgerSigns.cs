using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Daftar.Infrastructure.Migrations;

/// <summary>
/// Repairs two early test purchase ledger rows created before the SignAgreesWithKind fix was in place.
/// Drops the append-only trigger temporarily, flips the sign of positive Purchase rows to negative,
/// and re-establishes the trigger.
/// </summary>
[DbContext(typeof(DaftarContext))]
[Migration("20261007140000_RepairStalePurchaseLedgerSigns")]
public partial class RepairStalePurchaseLedgerSigns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Operations.Add(new SqlOperation
        {
            Sql = "DROP TRIGGER IF EXISTS trg_PartyAccountTransactions_no_update;",
            SuppressTransaction = true,
        });

        migrationBuilder.Operations.Add(new SqlOperation
        {
            Sql = "UPDATE PartyAccountTransactions SET Amount = '-' || Amount WHERE Kind = 'Purchase' AND CAST(Amount AS NUMERIC) > 0;",
            SuppressTransaction = true,
        });

        migrationBuilder.Operations.Add(new SqlOperation
        {
            Sql = """
                CREATE TRIGGER IF NOT EXISTS trg_PartyAccountTransactions_no_update BEFORE UPDATE ON PartyAccountTransactions
                BEGIN SELECT RAISE(ABORT, 'PartyAccountTransactions is append-only'); END;
                """,
            SuppressTransaction = true,
        });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}

