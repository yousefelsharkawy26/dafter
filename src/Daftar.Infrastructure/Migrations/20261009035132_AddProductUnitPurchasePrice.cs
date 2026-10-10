using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daftar.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductUnitPurchasePrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PurchasePrice",
                table: "ProductUnits",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PurchasePrice",
                table: "ProductUnits");
        }
    }
}
