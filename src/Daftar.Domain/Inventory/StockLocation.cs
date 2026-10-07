using Daftar.Domain.Shared;

namespace Daftar.Domain.Inventory;

public enum StockLocationType
{
    Warehouse,
    /// <summary>A distributor's vehicle. Its owner is found through Distributors.StockLocationId (P3).</summary>
    Vehicle,
}

public sealed class StockLocation : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public StockLocationType Type { get; set; } = StockLocationType.Warehouse;
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainException("Location name is required.");
    }
}