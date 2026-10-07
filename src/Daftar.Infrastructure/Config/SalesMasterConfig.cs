using Daftar.Domain.Finance;
using Daftar.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

public sealed class PriceListConfig : IEntityTypeConfiguration<PriceList>
{
    public void Configure(EntityTypeBuilder<PriceList> b)
    {
        b.ToTable("PriceLists");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedOnAdd();
        b.Property(p => p.Name).IsRequired();
        b.Property(p => p.IsActive).HasDefaultValue(true);

        b.HasIndex(p => p.Name).IsUnique();

        // One default list. Filtered unique index.
        b.HasIndex(p => p.IsDefault).IsUnique().HasFilter("IsDefault = 1");

        AuditColumns.Map(b);
    }
}

public sealed class ProductPriceConfig : IEntityTypeConfiguration<ProductPrice>
{
    public void Configure(EntityTypeBuilder<ProductPrice> b)
    {
        b.ToTable("ProductPrices", t =>
        {
            t.HasCheckConstraint("CK_ProductPrices_Price", "\"Price\" >= 0");
        });

        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedOnAdd();
        b.Property(p => p.Price).HasPrecision(18, 4).IsRequired();
        b.Property(p => p.EffectiveFrom).IsRequired();

        b.HasIndex(p => new { p.PriceListId, p.ProductUnitId, p.EffectiveFrom }).IsUnique();

        b.HasOne(p => p.PriceList)
            .WithMany(pl => pl.Prices)
            .HasForeignKey(p => p.PriceListId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(p => p.ProductUnit)
            .WithMany()
            .HasForeignKey(p => p.ProductUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        AuditColumns.Map(b);
    }
}

public sealed class CashAccountConfig : IEntityTypeConfiguration<CashAccount>
{
    public void Configure(EntityTypeBuilder<CashAccount> b)
    {
        b.ToTable("CashAccounts", t =>
        {
            t.HasCheckConstraint(
                "CK_CashAccounts_Type",
                "\"Type\" IN ('Cash','Bank','Wallet','DistributorCustody')");
        });

        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedOnAdd();
        b.Property(a => a.Name).IsRequired();
        b.Property(a => a.Type).HasConversion<string>().IsRequired();
        b.Property(a => a.IsActive).HasDefaultValue(true);

        b.HasIndex(a => a.Name).IsUnique();

        AuditColumns.Map(b);
    }
}