using Daftar.Domain.Catalog;
using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Security;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Daftar.Infrastructure;

public sealed class DaftarContext(DbContextOptions<DaftarContext> options) : DbContext(options)
{
    // Security
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // Master data
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductUnit> ProductUnits => Set<ProductUnit>();
    public DbSet<StockLocation> StockLocations => Set<StockLocation>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

    // Inventory
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();

    // Purchasing
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();

    // Sales
    public DbSet<Domain.Sales.PriceList> PriceLists => Set<Domain.Sales.PriceList>();
    public DbSet<Domain.Sales.ProductPrice> ProductPrices => Set<Domain.Sales.ProductPrice>();
    public DbSet<Domain.Sales.Sale> Sales => Set<Domain.Sales.Sale>();
    public DbSet<Domain.Sales.SaleItem> SaleItems => Set<Domain.Sales.SaleItem>();
    public DbSet<Domain.Sales.SaleItemLot> SaleItemLots => Set<Domain.Sales.SaleItemLot>();

    // Finance
    public DbSet<Domain.Finance.CashAccount> CashAccounts => Set<Domain.Finance.CashAccount>();
    public DbSet<Domain.Finance.Payment> Payments => Set<Domain.Finance.Payment>();
    public DbSet<Domain.Finance.PartyAccountTransaction> PartyAccountTransactions
        => Set<Domain.Finance.PartyAccountTransaction>();
    public DbSet<Domain.Finance.CashTransaction> CashTransactions => Set<Domain.Finance.CashTransaction>();

    // Distributors and stock control
    public DbSet<Domain.Distributors.Distributor> Distributors => Set<Domain.Distributors.Distributor>();
    public DbSet<Domain.Distributors.DistributorSession> DistributorSessions
        => Set<Domain.Distributors.DistributorSession>();
    public DbSet<Domain.Inventory.StockTransfer> StockTransfers => Set<Domain.Inventory.StockTransfer>();
    public DbSet<Domain.Inventory.StockTransferItem> StockTransferItems => Set<Domain.Inventory.StockTransferItem>();
    public DbSet<Domain.Inventory.StockCount> StockCounts => Set<Domain.Inventory.StockCount>();
    public DbSet<Domain.Inventory.StockCountItem> StockCountItems => Set<Domain.Inventory.StockCountItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Decimal columns are TEXT on SQLite, so precision/scale are documentation only.
        // HasPrecision is ignored by the provider; the Domain enforces the scales.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DaftarContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateTrackedEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ValidateTrackedEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Runs the domain invariants of every entity about to be written. SQLite cannot CHECK decimal
    /// columns, so this is where "no negative quantity", "discount does not exceed the gross" and
    /// friends are actually enforced.
    ///
    /// Only Added and Modified rows are checked: an Unchanged entity is not being written, so it
    /// cannot corrupt anything and stale data elsewhere must not block an unrelated save.
    /// </summary>
    private void ValidateTrackedEntities()
    {
        foreach (var entry in ChangeTracker.Entries<IDomainValidatable>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.Validate();
        }
    }
}