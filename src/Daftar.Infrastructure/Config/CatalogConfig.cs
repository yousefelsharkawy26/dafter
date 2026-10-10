using Daftar.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

public sealed class ProductConfig : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("Products", t =>
        {
            t.HasCheckConstraint("CK_Products_ReorderLevel", "ReorderLevel IS NULL OR ReorderLevel >= 0");
        });

        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedOnAdd();
        b.Property(p => p.Sku).IsRequired();
        b.Property(p => p.Name).IsRequired();
        b.Property(p => p.IsActive).HasDefaultValue(true);
        b.Property(p => p.ReorderLevel).HasPrecision(18, 3);

        // Uniqueness is a database concern: plain C# cannot see other rows.
        b.HasIndex(p => p.Sku).IsUnique();
        b.HasIndex(p => p.Name);

        AuditColumns.Map(b);
    }
}

public sealed class ProductUnitConfig : IEntityTypeConfiguration<ProductUnit>
{
    public void Configure(EntityTypeBuilder<ProductUnit> b)
    {
        b.ToTable("ProductUnits", t =>
        {
            t.HasCheckConstraint("CK_ProductUnits_Factor", "\"Factor\" > 0");
            t.HasCheckConstraint("CK_ProductUnits_IsBase", "IsBase IN (0,1)");
        });

        b.HasKey(u => u.Id);
        b.Property(u => u.Id).ValueGeneratedOnAdd();
        b.Property(u => u.Name).IsRequired();
        b.Property(u => u.Factor).HasPrecision(18, 6).IsRequired();
        b.Property(u => u.PurchasePrice).HasPrecision(18, 2).HasDefaultValue(0m);
        b.Property(u => u.IsDefaultSale).HasDefaultValue(false);
        b.Property(u => u.IsDefaultPurchase).HasDefaultValue(false);
        b.Property(u => u.IsActive).HasDefaultValue(true);
        b.Property(u => u.Barcode);

        b.HasIndex(u => u.Barcode).IsUnique().HasFilter("\"Barcode\" IS NOT NULL");
        b.HasIndex(u => new { u.ProductId, u.Name }).IsUnique();

        // Exactly one base unit per product. A partial index: EF cannot express this with
        // HasIndex/HasFilter reliably across providers, so the migration adds it with raw SQL.
        b.HasIndex(u => u.ProductId).HasFilter("IsBase = 1");

        b.HasOne(u => u.Product)
            .WithMany(p => p.Units)
            .HasForeignKey(u => u.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        AuditColumns.Map(b);
    }
}