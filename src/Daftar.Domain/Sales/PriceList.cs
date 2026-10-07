using Daftar.Domain.Shared;

namespace Daftar.Domain.Sales;

/// <summary>A named set of prices. Exactly one list is the default; parties can be pinned to one.</summary>
public sealed class PriceList : IAuditable, IDomainValidatable
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public List<ProductPrice> Prices { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new DomainException("Price list name is required.");
    }
}