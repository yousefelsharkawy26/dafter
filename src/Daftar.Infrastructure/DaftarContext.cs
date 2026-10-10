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
    /// <summary>
    /// Who is acting, for permission checks. Null during seeding, migrations and background work,
    /// where no operator is signed in and nothing should be blocked.
    /// </summary>
    /// <remarks>
    /// Read from the ambient identity rather than injected: EF creates contexts per scope and
    /// there are dozens of construction sites, so threading the user through each one would be a
    /// place to forget. CurrentUser uses AsyncLocal, so this reads the right value for the
    /// operation in flight.
    /// </remarks>
    internal int? SignedInUserId => CurrentUserIdentity.Value?.UserId;

    /// <summary>
    /// Throws unless the signed-in role holds the code. Called by the posting services before they
    /// move stock or money, so a screen that forgets to check still cannot perform the operation.
    /// A no-op when nobody is signed in, because seeding and migrations write through here.
    /// </summary>
    public void RequirePermission(string permissionCode)
    {
        if (SignedInUserId is not { } userId)
            return;

        if (!RoleGrants(userId, permissionCode))
            throw new DomainException(
                $"ليست عندك صلاحية '{permissionCode}'. لو محتاج تعمل كده، كلّم مدير النظام.");
    }

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

        EnforcePermissions();
    }

    /// <summary>
    /// The permission codes each protected entity requires to be written.
    /// </summary>
    /// <remarks>
    /// Enforced here, in the one method every write passes through, rather than in each
    /// ViewModel. A check in the UI stops a button; a check here stops the write. Adding a new
    /// screen that creates a sale cannot forget this, because it does not know this exists.
    /// </remarks>
    private static readonly Dictionary<string, string> ProtectedWrites = new(StringComparer.Ordinal)
    {
        [nameof(User)] = Role.Codes.UsersManage,
        [nameof(Role)] = Role.Codes.UsersManage,
    };

    /// <summary>
    /// Refuses writes to permission-protected tables when the signed-in user lacks the code.
    ///
    /// Runs after the domain invariants so a genuine validation error still reads as one. Skipped
    /// entirely when nobody is signed in: seeding, migrations and the startup integrity check all
    /// write through here, and there is no operator to ask.
    /// </summary>
    private void EnforcePermissions()
    {
        if (SignedInUserId is not { } userId)
            return;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            if (!ProtectedWrites.TryGetValue(entry.Metadata.ClrType.Name, out var code))
                continue;

            if (!RoleGrants(userId, code))
                throw new DomainException(
                    $"ليست عندك صلاحية '{code}'. لو محتاج تعمل كده، كلّم مدير النظام.");
        }
    }

    /// <summary>
    /// Whether the signed-in user holds the code, read from the database rather than cached so
    /// revoking a permission takes effect on the very next write.
    /// </summary>
    private bool RoleGrants(int userId, string code)
    {
        var permissions = Database.SqlQueryRaw<string>(
                "SELECT r.Permissions FROM Users u JOIN Roles r ON r.Id = u.RoleId " +
                "WHERE u.Id = {0} AND u.IsActive = 1",
                userId)
            .AsEnumerable()
            .FirstOrDefault();

        if (permissions is null)
            return false;

        // Same whole-token matching as Role.Has: the codes are comma separated, so padding with
        // commas turns a LIKE into an exact match on the list.
        var wanted = $",{code},";
        var have = $",{permissions.Replace(" ", string.Empty)},";
        return have.Contains(wanted, StringComparison.OrdinalIgnoreCase);
    }
}