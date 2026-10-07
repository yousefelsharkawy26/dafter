namespace Daftar.Domain.Shared;

/// <summary>Key/value application settings. Rows are seeded and updated, never audited individually.</summary>
public sealed class Setting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    /// <summary>Known keys, so a typo in a lookup is a compile error.</summary>
    public static class Keys
    {
        public const string CompanyName = "CompanyName";
        public const string TaxNumber = "TaxNumber";
        public const string Phone = "Phone";
        public const string Address = "Address";
        public const string ReceiptFooter = "ReceiptFooter";
        public const string DefaultCustomerId = "DefaultCustomerId";
        public const string DefaultLocationId = "DefaultLocationId";
        public const string DefaultPriceListId = "DefaultPriceListId";
        public const string LockedUntilDate = "LockedUntilDate";
    }

    public int GetInt(string key, int fallback)
        => int.TryParse(Value, out var parsed) ? parsed : fallback;
}