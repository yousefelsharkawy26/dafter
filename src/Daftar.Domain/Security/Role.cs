using Daftar.Domain.Shared;

namespace Daftar.Domain.Security;

/// <summary>
/// A role is a named bundle of permission codes plus the deepest discount its holder may apply.
/// Codes are defined in code, never stored as rows.
/// </summary>
public sealed class Role : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Comma-separated, e.g. "Sales.Create,Price.Override,Stock.Adjust".</summary>
    public string Permissions { get; set; } = string.Empty;

    public decimal MaxDiscountPercent { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public IReadOnlyList<string> PermissionCodes =>
        Permissions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool Has(string code)
        => PermissionCodes.Contains(code, StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainException("Role name is required.");
        if (MaxDiscountPercent is < 0 or > 100)
            throw new DomainException($"Role '{Name}' has MaxDiscountPercent {MaxDiscountPercent}; must be 0..100.");
    }

    /// <summary>
    /// Permission codes referenced anywhere in the application.
    ///
    /// Every code here must gate something real. Sales.Cancel and Period.Unlock used to sit in this
    /// list with a checkbox and an Arabic description in the settings screen, and nothing in the
    /// application checked either one: there is no code that cancels a sale and no code that locks
    /// a period. An operator who ticked "إلغاء الفواتير" had every reason to believe invoices could
    /// be cancelled, and nothing would have happened. They are removed rather than enforced -- a
    /// switch that does nothing is worse than a switch that is absent. They come back when the
    /// features do.
    /// </summary>
    public static class Codes
    {
        public const string SalesCreate = "Sales.Create";
        public const string PurchasesCreate = "Purchases.Create";
        public const string PaymentsManage = "Payments.Manage";
        public const string PriceOverride = "Price.Override";
        public const string StockAdjust = "Stock.Adjust";
        public const string StockScrap = "Stock.Scrap";
        public const string PartiesEditCredit = "Parties.EditCredit";
        public const string ReportsCost = "Reports.Cost";
        public const string UsersManage = "Users.Manage";

        public static readonly string[] All =
        [
            SalesCreate, PurchasesCreate, PaymentsManage, PriceOverride,
            StockAdjust, StockScrap, PartiesEditCredit, ReportsCost, UsersManage,
        ];

        /// <summary>
        /// The full set, joined once. The seeded Admin role takes this verbatim, so a code added
        /// above reaches the administrator without a migration or a data fix.
        /// </summary>
        public static readonly string Csv = string.Join(',', All);
    }
}