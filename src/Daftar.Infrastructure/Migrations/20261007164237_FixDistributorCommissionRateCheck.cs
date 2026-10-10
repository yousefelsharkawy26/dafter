using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daftar.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixDistributorCommissionRateCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Distributors_CommissionRate",
                table: "Distributors");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Distributors_CommissionRate",
                table: "Distributors",
                sql: "CAST(\"CommissionRate\" AS REAL) >= 0 AND CAST(\"CommissionRate\" AS REAL) <= 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Distributors_CommissionRate",
                table: "Distributors");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Distributors_CommissionRate",
                table: "Distributors",
                sql: "\"CommissionRate\" >= 0 AND \"CommissionRate\" <= 100");
        }
    }
}
