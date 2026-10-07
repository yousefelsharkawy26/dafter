using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daftar.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropLotExpiryAndPerishables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Lots_LotNumber",
                table: "Lots");

            migrationBuilder.DropIndex(
                name: "IX_Lots_ProductId_ExpiryDate",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "ExpiryDate",
                table: "PurchaseItems");

            migrationBuilder.DropColumn(
                name: "LotNumber",
                table: "PurchaseItems");

            migrationBuilder.DropColumn(
                name: "ProductionDate",
                table: "PurchaseItems");

            migrationBuilder.DropColumn(
                name: "IsPerishable",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ShelfLifeDays",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ExpiryDate",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "LotNumber",
                table: "Lots");

            migrationBuilder.DropColumn(
                name: "ProductionDate",
                table: "Lots");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_ProductId",
                table: "Lots",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Lots_ProductId",
                table: "Lots");

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExpiryDate",
                table: "PurchaseItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LotNumber",
                table: "PurchaseItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ProductionDate",
                table: "PurchaseItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPerishable",
                table: "Products",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ShelfLifeDays",
                table: "Products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExpiryDate",
                table: "Lots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LotNumber",
                table: "Lots",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateOnly>(
                name: "ProductionDate",
                table: "Lots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Lots_LotNumber",
                table: "Lots",
                column: "LotNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_ProductId_ExpiryDate",
                table: "Lots",
                columns: new[] { "ProductId", "ExpiryDate" });
        }
    }
}
