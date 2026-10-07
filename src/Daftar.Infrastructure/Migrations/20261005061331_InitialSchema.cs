using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daftar.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    EntityName = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", nullable: false),
                    OldValues = table.Column<string>(type: "TEXT", nullable: true),
                    NewValues = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CashAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashAccounts", x => x.Id);
                    table.CheckConstraint("CK_CashAccounts_Type", "\"Type\" IN ('Cash','Bank','Wallet','DistributorCustody')");
                });

            migrationBuilder.CreateTable(
                name: "DocumentSequences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocType = table.Column<string>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Prefix = table.Column<string>(type: "TEXT", nullable: false),
                    NextNumber = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    Padding = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 6)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSequences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceLists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sku = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsPerishable = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShelfLifeDays = table.Column<int>(type: "INTEGER", nullable: true),
                    ReorderLevel = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.CheckConstraint("CK_Products_ReorderLevel", "ReorderLevel IS NULL OR ReorderLevel >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Permissions = table.Column<string>(type: "TEXT", nullable: false),
                    MaxDiscountPercent = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "StockLocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLocations", x => x.Id);
                    table.CheckConstraint("CK_StockLocations_Type", "\"Type\" IN ('Warehouse','Vehicle')");
                });

            migrationBuilder.CreateTable(
                name: "ProductUnits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Factor = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    IsBase = table.Column<bool>(type: "INTEGER", nullable: false),
                    Barcode = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefaultSale = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    IsDefaultPurchase = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductUnits", x => x.Id);
                    table.CheckConstraint("CK_ProductUnits_Factor", "\"Factor\" > 0");
                    table.CheckConstraint("CK_ProductUnits_IsBase", "IsBase IN (0,1)");
                    table.ForeignKey(
                        name: "FK_ProductUnits_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    RoleId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductPrices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PriceListId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductUnitId = table.Column<int>(type: "INTEGER", nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductPrices", x => x.Id);
                    table.CheckConstraint("CK_ProductPrices_Price", "\"Price\" >= 0");
                    table.ForeignKey(
                        name: "FK_ProductPrices_PriceLists_PriceListId",
                        column: x => x.PriceListId,
                        principalTable: "PriceLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductPrices_ProductUnits_ProductUnitId",
                        column: x => x.ProductUnitId,
                        principalTable: "ProductUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Distributors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    StockLocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    CashAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    CommissionRate = table.Column<decimal>(type: "TEXT", precision: 5, scale: 2, nullable: false, defaultValue: 0m),
                    CommissionBasis = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Distributors", x => x.Id);
                    table.CheckConstraint("CK_Distributors_CommissionBasis", "\"CommissionBasis\" IN ('Sales','Collections')");
                    table.CheckConstraint("CK_Distributors_CommissionRate", "\"CommissionRate\" >= 0 AND \"CommissionRate\" <= 100");
                    table.ForeignKey(
                        name: "FK_Distributors_CashAccounts_CashAccountId",
                        column: x => x.CashAccountId,
                        principalTable: "CashAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Distributors_StockLocations_StockLocationId",
                        column: x => x.StockLocationId,
                        principalTable: "StockLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Distributors_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Parties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsCustomer = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSupplier = table.Column<bool>(type: "INTEGER", nullable: false),
                    CustomerType = table.Column<string>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", nullable: true),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    Area = table.Column<string>(type: "TEXT", nullable: true),
                    ContactPerson = table.Column<string>(type: "TEXT", nullable: true),
                    TaxNumber = table.Column<string>(type: "TEXT", nullable: true),
                    CreditLimit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    PaymentTermsDays = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    PriceListId = table.Column<int>(type: "INTEGER", nullable: true),
                    AssignedDistributorId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parties", x => x.Id);
                    table.CheckConstraint("CK_Parties_CreditLimit", "\"CreditLimit\" >= 0");
                    table.CheckConstraint("CK_Parties_CustomerType", "IsCustomer = 0 OR CustomerType IS NOT NULL");
                    table.CheckConstraint("CK_Parties_CustomerType_Values", "CustomerType IS NULL OR CustomerType IN ('Supermarket','Restaurant','Wholesale','Retail','Individual','Other')");
                    table.CheckConstraint("CK_Parties_PaymentTermsDays", "PaymentTermsDays >= 0");
                    table.CheckConstraint("CK_Parties_Role", "IsCustomer = 1 OR IsSupplier = 1");
                    table.CheckConstraint("CK_Parties_Status", "\"Status\" IN ('Active','Pending','Blocked','Archived')");
                    table.ForeignKey(
                        name: "FK_Parties_Distributors_AssignedDistributorId",
                        column: x => x.AssignedDistributorId,
                        principalTable: "Distributors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Parties_PriceLists_PriceListId",
                        column: x => x.PriceListId,
                        principalTable: "PriceLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Lots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    LotNumber = table.Column<string>(type: "TEXT", nullable: false),
                    ProductionDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ReceivedOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SupplierId = table.Column<int>(type: "INTEGER", nullable: true),
                    UnitCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    BlockReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lots", x => x.Id);
                    table.CheckConstraint("CK_Lots_Blocked", "\"Status\" <> 'Blocked' OR \"BlockReason\" IS NOT NULL");
                    table.CheckConstraint("CK_Lots_Status", "\"Status\" IN ('Active','Blocked')");
                    table.CheckConstraint("CK_Lots_UnitCost", "\"UnitCost\" >= 0");
                    table.ForeignKey(
                        name: "FK_Lots_Parties_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Lots_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PartyAccountTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PartyId = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    SourceType = table.Column<string>(type: "TEXT", nullable: false),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    ReversesId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartyAccountTransactions", x => x.Id);
                    table.CheckConstraint("CK_PartyAccountTransactions_Kind", "\"Kind\" IN ('Opening','Sale','SaleReturn','Purchase','PurchaseReturn','PaymentIn','PaymentOut','Adjustment','Reversal')");
                    table.ForeignKey(
                        name: "FK_PartyAccountTransactions_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PartyAccountTransactions_PartyAccountTransactions_ReversesId",
                        column: x => x.ReversesId,
                        principalTable: "PartyAccountTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Purchases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    SupplierId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    SupplierInvoiceNo = table.Column<string>(type: "TEXT", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    OriginalPurchaseId = table.Column<int>(type: "INTEGER", nullable: true),
                    Subtotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    Total = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    DocNo = table.Column<string>(type: "TEXT", nullable: true),
                    DocDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PostedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PostedBy = table.Column<int>(type: "INTEGER", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledBy = table.Column<int>(type: "INTEGER", nullable: true),
                    CancelReason = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Purchases", x => x.Id);
                    table.CheckConstraint("CK_Purchases_CancelHasReason", "\"Status\" <> 'Cancelled' OR \"CancelReason\" IS NOT NULL");
                    table.CheckConstraint("CK_Purchases_Discount", "\"DiscountAmount\" >= 0");
                    table.CheckConstraint("CK_Purchases_DraftHasNoNo", "\"Status\" = 'Draft' OR \"DocNo\" IS NOT NULL");
                    table.CheckConstraint("CK_Purchases_Status", "\"Status\" IN ('Draft','Posted','Cancelled')");
                    table.CheckConstraint("CK_Purchases_Type", "\"Type\" IN ('Invoice','Return')");
                    table.ForeignKey(
                        name: "FK_Purchases_Parties_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Purchases_Purchases_OriginalPurchaseId",
                        column: x => x.OriginalPurchaseId,
                        principalTable: "Purchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Purchases_StockLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "StockLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockBalances",
                columns: table => new
                {
                    LotId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockBalances", x => new { x.LotId, x.LocationId });
                    table.CheckConstraint("CK_StockBalances_Quantity", "\"Quantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockBalances_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StockBalances_StockLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "StockLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PurchaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductUnitId = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitName = table.Column<string>(type: "TEXT", nullable: false),
                    UnitFactor = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    BaseQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    LineTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    LotNumber = table.Column<string>(type: "TEXT", nullable: true),
                    ProductionDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LotId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseItems", x => x.Id);
                    table.CheckConstraint("CK_PurchaseItems_BaseQuantity", "\"BaseQuantity\" > 0");
                    table.CheckConstraint("CK_PurchaseItems_Discount", "\"DiscountAmount\" >= 0");
                    table.CheckConstraint("CK_PurchaseItems_Quantity", "\"Quantity\" > 0");
                    table.CheckConstraint("CK_PurchaseItems_UnitFactor", "\"UnitFactor\" > 0");
                    table.CheckConstraint("CK_PurchaseItems_UnitPrice", "\"UnitPrice\" >= 0");
                    table.ForeignKey(
                        name: "FK_PurchaseItems_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseItems_ProductUnits_ProductUnitId",
                        column: x => x.ProductUnitId,
                        principalTable: "ProductUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseItems_Purchases_PurchaseId",
                        column: x => x.PurchaseId,
                        principalTable: "Purchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CashTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CashAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    PaymentId = table.Column<int>(type: "INTEGER", nullable: true),
                    DistributorSessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    TransferRef = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    ReversesId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashTransactions", x => x.Id);
                    table.CheckConstraint("CK_CashTransactions_Category", "Category IS NULL OR Category IN ('Fuel','Rent','Salary','Commission','Utilities','Maintenance','Other')");
                    table.CheckConstraint("CK_CashTransactions_Kind", "\"Kind\" IN ('Opening','Collection','SupplierPayment','Expense','TransferIn','TransferOut','Deposit','Withdrawal','Adjustment','Reversal')");
                    table.ForeignKey(
                        name: "FK_CashTransactions_CashAccounts_CashAccountId",
                        column: x => x.CashAccountId,
                        principalTable: "CashAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashTransactions_CashTransactions_ReversesId",
                        column: x => x.ReversesId,
                        principalTable: "CashTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DistributorSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DistributorId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OpenedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedBy = table.Column<int>(type: "INTEGER", nullable: true),
                    SalesTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    ReturnsTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    CollectionsTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    ExpectedCash = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    CountedCash = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    CashDifference = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    StockShortageValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    CommissionAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    StockCountId = table.Column<int>(type: "INTEGER", nullable: true),
                    StockCountId1 = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DistributorSessions", x => x.Id);
                    table.CheckConstraint("CK_DistributorSessions_ClosedHasCloser", "\"Status\" <> 'Closed' OR (\"ClosedAt\" IS NOT NULL AND \"ClosedBy\" IS NOT NULL)");
                    table.CheckConstraint("CK_DistributorSessions_Status", "\"Status\" IN ('Open','Closed')");
                    table.ForeignKey(
                        name: "FK_DistributorSessions_Distributors_DistributorId",
                        column: x => x.DistributorId,
                        principalTable: "Distributors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Sales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    PartyId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceListId = table.Column<int>(type: "INTEGER", nullable: false),
                    DistributorId = table.Column<int>(type: "INTEGER", nullable: true),
                    DistributorSessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    OriginalSaleId = table.Column<int>(type: "INTEGER", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Subtotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    Total = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    DocNo = table.Column<string>(type: "TEXT", nullable: true),
                    DocDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PostedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PostedBy = table.Column<int>(type: "INTEGER", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledBy = table.Column<int>(type: "INTEGER", nullable: true),
                    CancelReason = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sales", x => x.Id);
                    table.CheckConstraint("CK_Sales_CancelHasReason", "\"Status\" <> 'Cancelled' OR \"CancelReason\" IS NOT NULL");
                    table.CheckConstraint("CK_Sales_Discount", "\"DiscountAmount\" >= 0");
                    table.CheckConstraint("CK_Sales_DraftHasNoNo", "\"Status\" = 'Draft' OR \"DocNo\" IS NOT NULL");
                    table.CheckConstraint("CK_Sales_ReturnHasOriginal", "\"Type\" <> 'Return' OR \"OriginalSaleId\" IS NOT NULL");
                    table.CheckConstraint("CK_Sales_Status", "\"Status\" IN ('Draft','Posted','Cancelled')");
                    table.CheckConstraint("CK_Sales_Type", "\"Type\" IN ('Invoice','Return')");
                    table.ForeignKey(
                        name: "FK_Sales_DistributorSessions_DistributorSessionId",
                        column: x => x.DistributorSessionId,
                        principalTable: "DistributorSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sales_Distributors_DistributorId",
                        column: x => x.DistributorId,
                        principalTable: "Distributors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sales_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sales_PriceLists_PriceListId",
                        column: x => x.PriceListId,
                        principalTable: "PriceLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sales_Sales_OriginalSaleId",
                        column: x => x.OriginalSaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Sales_StockLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "StockLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockCounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    DistributorSessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    DocNo = table.Column<string>(type: "TEXT", nullable: true),
                    DocDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PostedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PostedBy = table.Column<int>(type: "INTEGER", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledBy = table.Column<int>(type: "INTEGER", nullable: true),
                    CancelReason = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCounts", x => x.Id);
                    table.CheckConstraint("CK_StockCounts_CancelHasReason", "\"Status\" <> 'Cancelled' OR \"CancelReason\" IS NOT NULL");
                    table.CheckConstraint("CK_StockCounts_DraftHasNoNo", "\"Status\" = 'Draft' OR \"DocNo\" IS NOT NULL");
                    table.CheckConstraint("CK_StockCounts_Status", "\"Status\" IN ('Draft','Posted','Cancelled')");
                    table.ForeignKey(
                        name: "FK_StockCounts_DistributorSessions_DistributorSessionId",
                        column: x => x.DistributorSessionId,
                        principalTable: "DistributorSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockCounts_StockLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "StockLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromLocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToLocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    DistributorSessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    DocNo = table.Column<string>(type: "TEXT", nullable: true),
                    DocDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PostedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PostedBy = table.Column<int>(type: "INTEGER", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledBy = table.Column<int>(type: "INTEGER", nullable: true),
                    CancelReason = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransfers", x => x.Id);
                    table.CheckConstraint("CK_StockTransfers_CancelHasReason", "\"Status\" <> 'Cancelled' OR \"CancelReason\" IS NOT NULL");
                    table.CheckConstraint("CK_StockTransfers_Distinct", "\"FromLocationId\" <> \"ToLocationId\"");
                    table.CheckConstraint("CK_StockTransfers_DraftHasNoNo", "\"Status\" = 'Draft' OR \"DocNo\" IS NOT NULL");
                    table.CheckConstraint("CK_StockTransfers_Status", "\"Status\" IN ('Draft','Posted','Cancelled')");
                    table.ForeignKey(
                        name: "FK_StockTransfers_DistributorSessions_DistributorSessionId",
                        column: x => x.DistributorSessionId,
                        principalTable: "DistributorSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_StockLocations_FromLocationId",
                        column: x => x.FromLocationId,
                        principalTable: "StockLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_StockLocations_ToLocationId",
                        column: x => x.ToLocationId,
                        principalTable: "StockLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    PartyId = table.Column<int>(type: "INTEGER", nullable: false),
                    CashAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Reference = table.Column<string>(type: "TEXT", nullable: true),
                    SaleId = table.Column<int>(type: "INTEGER", nullable: true),
                    PurchaseId = table.Column<int>(type: "INTEGER", nullable: true),
                    DistributorSessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    DocNo = table.Column<string>(type: "TEXT", nullable: true),
                    DocDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PostedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PostedBy = table.Column<int>(type: "INTEGER", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledBy = table.Column<int>(type: "INTEGER", nullable: true),
                    CancelReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.CheckConstraint("CK_Payments_Amount", "\"Amount\" > 0");
                    table.CheckConstraint("CK_Payments_CancelHasReason", "\"Status\" <> 'Cancelled' OR \"CancelReason\" IS NOT NULL");
                    table.CheckConstraint("CK_Payments_Direction", "\"Direction\" IN ('In','Out')");
                    table.CheckConstraint("CK_Payments_DraftHasNoNo", "\"Status\" = 'Draft' OR \"DocNo\" IS NOT NULL");
                    table.CheckConstraint("CK_Payments_Method", "\"Method\" IN ('Cash','Transfer','Wallet','Card','Cheque')");
                    table.CheckConstraint("CK_Payments_OneDocument", "\"SaleId\" IS NULL OR \"PurchaseId\" IS NULL");
                    table.CheckConstraint("CK_Payments_Status", "\"Status\" IN ('Draft','Posted','Cancelled')");
                    table.ForeignKey(
                        name: "FK_Payments_CashAccounts_CashAccountId",
                        column: x => x.CashAccountId,
                        principalTable: "CashAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_DistributorSessions_DistributorSessionId",
                        column: x => x.DistributorSessionId,
                        principalTable: "DistributorSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "Parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Purchases_PurchaseId",
                        column: x => x.PurchaseId,
                        principalTable: "Purchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SaleItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SaleId = table.Column<int>(type: "INTEGER", nullable: false),
                    LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductUnitId = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitName = table.Column<string>(type: "TEXT", nullable: false),
                    UnitFactor = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    BaseQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    ListPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    LineTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    CostTotal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    OriginalSaleItemId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleItems", x => x.Id);
                    table.CheckConstraint("CK_SaleItems_BaseQuantity", "\"BaseQuantity\" > 0");
                    table.CheckConstraint("CK_SaleItems_Discount", "\"DiscountAmount\" >= 0");
                    table.CheckConstraint("CK_SaleItems_ListPrice", "\"ListPrice\" >= 0");
                    table.CheckConstraint("CK_SaleItems_Quantity", "\"Quantity\" > 0");
                    table.CheckConstraint("CK_SaleItems_UnitFactor", "\"UnitFactor\" > 0");
                    table.CheckConstraint("CK_SaleItems_UnitPrice", "\"UnitPrice\" >= 0");
                    table.ForeignKey(
                        name: "FK_SaleItems_ProductUnits_ProductUnitId",
                        column: x => x.ProductUnitId,
                        principalTable: "ProductUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleItems_SaleItems_OriginalSaleItemId",
                        column: x => x.OriginalSaleItemId,
                        principalTable: "SaleItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleItems_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockCountItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StockCountId = table.Column<int>(type: "INTEGER", nullable: false),
                    LotId = table.Column<int>(type: "INTEGER", nullable: false),
                    SystemQty = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    CountedQty = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    DiffQty = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCountItems", x => x.Id);
                    table.CheckConstraint("CK_StockCountItems_CountedQty", "\"CountedQty\" IS NULL OR \"CountedQty\" >= 0");
                    table.CheckConstraint("CK_StockCountItems_SystemQty", "\"SystemQty\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockCountItems_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockCountItems_StockCounts_StockCountId",
                        column: x => x.StockCountId,
                        principalTable: "StockCounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InventoryMovements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    LotId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", nullable: false),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReasonCode = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    ReversesMovementId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    TransferId = table.Column<int>(type: "INTEGER", nullable: true),
                    StockCountId = table.Column<int>(type: "INTEGER", nullable: true),
                    DistributorSessionId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovements", x => x.Id);
                    table.CheckConstraint("CK_InventoryMovements_Kind", "\"Kind\" IN ('Opening','Receipt','PurchaseReturn','Sale','SaleReturn','TransferIn','TransferOut','CountAdjust','Adjust','Scrap','Reversal')");
                    table.CheckConstraint("CK_InventoryMovements_ReasonCode", "\"Kind\" NOT IN ('Adjust','Scrap') OR \"ReasonCode\" IS NOT NULL");
                    table.CheckConstraint("CK_InventoryMovements_SourceType", "\"SourceType\" IN ('Purchase','Sale','Transfer','Count','Manual','Opening')");
                    table.ForeignKey(
                        name: "FK_InventoryMovements_DistributorSessions_DistributorSessionId",
                        column: x => x.DistributorSessionId,
                        principalTable: "DistributorSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_InventoryMovements_ReversesMovementId",
                        column: x => x.ReversesMovementId,
                        principalTable: "InventoryMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_StockCounts_StockCountId",
                        column: x => x.StockCountId,
                        principalTable: "StockCounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_StockLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "StockLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_StockTransfers_TransferId",
                        column: x => x.TransferId,
                        principalTable: "StockTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TransferId = table.Column<int>(type: "INTEGER", nullable: false),
                    LotId = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferItems", x => x.Id);
                    table.CheckConstraint("CK_StockTransferItems_BaseQuantity", "\"BaseQuantity\" > 0");
                    table.ForeignKey(
                        name: "FK_StockTransferItems_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferItems_StockTransfers_TransferId",
                        column: x => x.TransferId,
                        principalTable: "StockTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SaleItemLots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SaleItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    LotId = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    UnitCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleItemLots", x => x.Id);
                    table.CheckConstraint("CK_SaleItemLots_BaseQuantity", "\"BaseQuantity\" > 0");
                    table.CheckConstraint("CK_SaleItemLots_UnitCost", "\"UnitCost\" >= 0");
                    table.ForeignKey(
                        name: "FK_SaleItemLots_Lots_LotId",
                        column: x => x.LotId,
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleItemLots_SaleItems_SaleItemId",
                        column: x => x.SaleItemId,
                        principalTable: "SaleItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_At",
                table: "AuditLogs",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityName_EntityId",
                table: "AuditLogs",
                columns: new[] { "EntityName", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_UserId",
                table: "AuditLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CashAccounts_Name",
                table: "CashAccounts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_CashAccountId_BusinessDate",
                table: "CashTransactions",
                columns: new[] { "CashAccountId", "BusinessDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_DistributorSessionId",
                table: "CashTransactions",
                column: "DistributorSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_PaymentId",
                table: "CashTransactions",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_ReversesId",
                table: "CashTransactions",
                column: "ReversesId");

            migrationBuilder.CreateIndex(
                name: "IX_Distributors_CashAccountId",
                table: "Distributors",
                column: "CashAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Distributors_Name",
                table: "Distributors",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Distributors_StockLocationId",
                table: "Distributors",
                column: "StockLocationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Distributors_UserId",
                table: "Distributors",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DistributorSessions_DistributorId",
                table: "DistributorSessions",
                column: "DistributorId",
                unique: true,
                filter: "Status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_DistributorSessions_DistributorId_Status",
                table: "DistributorSessions",
                columns: new[] { "DistributorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DistributorSessions_StockCountId1",
                table: "DistributorSessions",
                column: "StockCountId1");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSequences_DocType_Year",
                table: "DocumentSequences",
                columns: new[] { "DocType", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_DistributorSessionId",
                table: "InventoryMovements",
                column: "DistributorSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_LocationId_OccurredAt",
                table: "InventoryMovements",
                columns: new[] { "LocationId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_LotId",
                table: "InventoryMovements",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_ProductId_OccurredAt",
                table: "InventoryMovements",
                columns: new[] { "ProductId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_ReversesMovementId",
                table: "InventoryMovements",
                column: "ReversesMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_SourceType_SourceId",
                table: "InventoryMovements",
                columns: new[] { "SourceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_StockCountId",
                table: "InventoryMovements",
                column: "StockCountId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_TransferId",
                table: "InventoryMovements",
                column: "TransferId");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_LotNumber",
                table: "Lots",
                column: "LotNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_ProductId_ExpiryDate",
                table: "Lots",
                columns: new[] { "ProductId", "ExpiryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Lots_SupplierId",
                table: "Lots",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Parties_AssignedDistributorId",
                table: "Parties",
                column: "AssignedDistributorId");

            migrationBuilder.CreateIndex(
                name: "IX_Parties_Code",
                table: "Parties",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Parties_Name",
                table: "Parties",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Parties_Phone",
                table: "Parties",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_Parties_PriceListId",
                table: "Parties",
                column: "PriceListId");

            migrationBuilder.CreateIndex(
                name: "IX_PartyAccountTransactions_PartyId_BusinessDate",
                table: "PartyAccountTransactions",
                columns: new[] { "PartyId", "BusinessDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PartyAccountTransactions_ReversesId",
                table: "PartyAccountTransactions",
                column: "ReversesId");

            migrationBuilder.CreateIndex(
                name: "IX_PartyAccountTransactions_SourceType_SourceId",
                table: "PartyAccountTransactions",
                columns: new[] { "SourceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CashAccountId",
                table: "Payments",
                column: "CashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_DistributorSessionId",
                table: "Payments",
                column: "DistributorSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_DocNo",
                table: "Payments",
                column: "DocNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PartyId_DocDate",
                table: "Payments",
                columns: new[] { "PartyId", "DocDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PurchaseId",
                table: "Payments",
                column: "PurchaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_SaleId",
                table: "Payments",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_IsDefault",
                table: "PriceLists",
                column: "IsDefault",
                unique: true,
                filter: "IsDefault = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_Name",
                table: "PriceLists",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductPrices_PriceListId_ProductUnitId_EffectiveFrom",
                table: "ProductPrices",
                columns: new[] { "PriceListId", "ProductUnitId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductPrices_ProductUnitId",
                table: "ProductPrices",
                column: "ProductUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Name",
                table: "Products",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Sku",
                table: "Products",
                column: "Sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_Barcode",
                table: "ProductUnits",
                column: "Barcode",
                unique: true,
                filter: "\"Barcode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId",
                table: "ProductUnits",
                column: "ProductId",
                unique: true,
                filter: "IsBase = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId_Name",
                table: "ProductUnits",
                columns: new[] { "ProductId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseItems_LotId",
                table: "PurchaseItems",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseItems_ProductId",
                table: "PurchaseItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseItems_ProductUnitId",
                table: "PurchaseItems",
                column: "ProductUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseItems_PurchaseId_LineNo",
                table: "PurchaseItems",
                columns: new[] { "PurchaseId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_DocDate",
                table: "Purchases",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_DocNo",
                table: "Purchases",
                column: "DocNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_LocationId",
                table: "Purchases",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_OriginalPurchaseId",
                table: "Purchases",
                column: "OriginalPurchaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_SupplierId_DocDate",
                table: "Purchases",
                columns: new[] { "SupplierId", "DocDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleItemLots_LotId",
                table: "SaleItemLots",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleItemLots_SaleItemId_LotId",
                table: "SaleItemLots",
                columns: new[] { "SaleItemId", "LotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleItems_OriginalSaleItemId",
                table: "SaleItems",
                column: "OriginalSaleItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleItems_ProductId",
                table: "SaleItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleItems_ProductUnitId",
                table: "SaleItems",
                column: "ProductUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleItems_SaleId_LineNo",
                table: "SaleItems",
                columns: new[] { "SaleId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sales_DistributorId",
                table: "Sales",
                column: "DistributorId");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_DistributorSessionId",
                table: "Sales",
                column: "DistributorSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_DocDate",
                table: "Sales",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_DocNo",
                table: "Sales",
                column: "DocNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sales_LocationId",
                table: "Sales",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_OriginalSaleId",
                table: "Sales",
                column: "OriginalSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_PartyId_DocDate",
                table: "Sales",
                columns: new[] { "PartyId", "DocDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Sales_PriceListId",
                table: "Sales",
                column: "PriceListId");

            migrationBuilder.CreateIndex(
                name: "IX_StockBalances_LocationId",
                table: "StockBalances",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCountItems_LotId",
                table: "StockCountItems",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCountItems_StockCountId_LotId",
                table: "StockCountItems",
                columns: new[] { "StockCountId", "LotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_DistributorSessionId",
                table: "StockCounts",
                column: "DistributorSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_DocNo",
                table: "StockCounts",
                column: "DocNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_LocationId",
                table: "StockCounts",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockLocations_Name",
                table: "StockLocations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferItems_LotId",
                table: "StockTransferItems",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferItems_TransferId_LotId",
                table: "StockTransferItems",
                columns: new[] { "TransferId", "LotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_DistributorSessionId",
                table: "StockTransfers",
                column: "DistributorSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_DocNo",
                table: "StockTransfers",
                column: "DocNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_FromLocationId",
                table: "StockTransfers",
                column: "FromLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_ToLocationId",
                table: "StockTransfers",
                column: "ToLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_RoleId",
                table: "Users",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CashTransactions_DistributorSessions_DistributorSessionId",
                table: "CashTransactions",
                column: "DistributorSessionId",
                principalTable: "DistributorSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CashTransactions_Payments_PaymentId",
                table: "CashTransactions",
                column: "PaymentId",
                principalTable: "Payments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DistributorSessions_StockCounts_StockCountId1",
                table: "DistributorSessions",
                column: "StockCountId1",
                principalTable: "StockCounts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Distributors_CashAccounts_CashAccountId",
                table: "Distributors");

            migrationBuilder.DropForeignKey(
                name: "FK_StockCounts_DistributorSessions_DistributorSessionId",
                table: "StockCounts");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "CashTransactions");

            migrationBuilder.DropTable(
                name: "DocumentSequences");

            migrationBuilder.DropTable(
                name: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "PartyAccountTransactions");

            migrationBuilder.DropTable(
                name: "ProductPrices");

            migrationBuilder.DropTable(
                name: "PurchaseItems");

            migrationBuilder.DropTable(
                name: "SaleItemLots");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "StockBalances");

            migrationBuilder.DropTable(
                name: "StockCountItems");

            migrationBuilder.DropTable(
                name: "StockTransferItems");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "SaleItems");

            migrationBuilder.DropTable(
                name: "Lots");

            migrationBuilder.DropTable(
                name: "StockTransfers");

            migrationBuilder.DropTable(
                name: "Purchases");

            migrationBuilder.DropTable(
                name: "ProductUnits");

            migrationBuilder.DropTable(
                name: "Sales");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "Parties");

            migrationBuilder.DropTable(
                name: "PriceLists");

            migrationBuilder.DropTable(
                name: "CashAccounts");

            migrationBuilder.DropTable(
                name: "DistributorSessions");

            migrationBuilder.DropTable(
                name: "Distributors");

            migrationBuilder.DropTable(
                name: "StockCounts");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "StockLocations");

            migrationBuilder.DropTable(
                name: "Roles");
        }
    }
}
