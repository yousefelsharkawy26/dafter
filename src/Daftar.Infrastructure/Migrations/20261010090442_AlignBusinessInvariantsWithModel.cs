using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daftar.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AlignBusinessInvariantsWithModel : Migration
    {
        /// <summary>
        /// Re-declares three business invariants in the model so a future
        /// <c>dotnet ef migrations add</c> stops emitting a DropIndex that quietly removes them:
        /// one base unit per product, one open session per distributor, one stock count per session.
        ///
        /// <para>
        /// Also removes the dead <c>StockCountId1</c> shadow column. DistributorSession.StockCount
        /// and StockCount.DistributorSession were both configured as owners of the same
        /// relationship, so EF invented a shadow FK and left the real StockCountId column
        /// unconstrained — DistributorSessionService writes to it with nothing enforcing it.
        /// </para>
        ///
        /// <para>
        /// The unique indexes are rebuilt from data that already satisfies them. The guard at the
        /// top makes that assumption explicit: if an existing database does violate one of these,
        /// the migration stops and says so rather than failing halfway through a DropIndex.
        /// </para>
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The three CreateIndex calls below are the guard. Building a UNIQUE index over data that
            // violates it makes SQLite abort the whole migration with a clear constraint error,
            // so a database that already has two base units (or two open sessions) stops here
            // with the schema untouched — rather than losing the data to a half-applied change.

            migrationBuilder.DropForeignKey(
                name: "FK_DistributorSessions_StockCounts_StockCountId1",
                table: "DistributorSessions");

            migrationBuilder.DropIndex(
                name: "IX_ProductUnits_ProductId",
                table: "ProductUnits");

            migrationBuilder.DropIndex(
                name: "IX_DistributorSessions_DistributorId",
                table: "DistributorSessions");

            migrationBuilder.DropIndex(
                name: "IX_DistributorSessions_StockCountId1",
                table: "DistributorSessions");

            // Move any value the dead column held onto the real one before the column goes, so a database
            // that had something written into the unconstrained column does not lose it silently.
            migrationBuilder.Sql(
                "UPDATE DistributorSessions SET StockCountId = StockCountId1 WHERE StockCountId IS NULL AND StockCountId1 IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "StockCountId1",
                table: "DistributorSessions");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId",
                table: "ProductUnits",
                column: "ProductId",
                unique: true,
                filter: "IsBase = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DistributorSessions_DistributorId",
                table: "DistributorSessions",
                column: "DistributorId",
                unique: true,
                filter: "Status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_DistributorSessions_StockCountId",
                table: "DistributorSessions",
                column: "StockCountId");

            migrationBuilder.AddForeignKey(
                name: "FK_DistributorSessions_StockCounts_StockCountId",
                table: "DistributorSessions",
                column: "StockCountId",
                principalTable: "StockCounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DistributorSessions_StockCounts_StockCountId",
                table: "DistributorSessions");

            migrationBuilder.DropIndex(
                name: "IX_ProductUnits_ProductId",
                table: "ProductUnits");

            migrationBuilder.DropIndex(
                name: "IX_DistributorSessions_DistributorId",
                table: "DistributorSessions");

            migrationBuilder.DropIndex(
                name: "IX_DistributorSessions_StockCountId",
                table: "DistributorSessions");

            migrationBuilder.AddColumn<int>(
                name: "StockCountId1",
                table: "DistributorSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId",
                table: "ProductUnits",
                column: "ProductId",
                filter: "IsBase = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DistributorSessions_DistributorId",
                table: "DistributorSessions",
                column: "DistributorId",
                filter: "Status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_DistributorSessions_StockCountId1",
                table: "DistributorSessions",
                column: "StockCountId1");

            migrationBuilder.AddForeignKey(
                name: "FK_DistributorSessions_StockCounts_StockCountId1",
                table: "DistributorSessions",
                column: "StockCountId1",
                principalTable: "StockCounts",
                principalColumn: "Id");
        }
    }
}
