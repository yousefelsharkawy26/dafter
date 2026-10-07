using Daftar.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

public sealed class PurchaseConfig : IEntityTypeConfiguration<Purchase>
{
    public void Configure(EntityTypeBuilder<Purchase> b)
    {
        b.ToTable("Purchases", t =>
        {
            t.HasCheckConstraint("CK_Purchases_Type", "\"Type\" IN ('Invoice','Return')");
            t.HasCheckConstraint("CK_Purchases_Status", "\"Status\" IN ('Draft','Posted','Cancelled')");
            // +D document constraints.
            t.HasCheckConstraint("CK_Purchases_DraftHasNoNo", "\"Status\" = 'Draft' OR \"DocNo\" IS NOT NULL");
            t.HasCheckConstraint(
                "CK_Purchases_CancelHasReason",
                "\"Status\" <> 'Cancelled' OR \"CancelReason\" IS NOT NULL");
            t.HasCheckConstraint("CK_Purchases_Discount", "\"DiscountAmount\" >= 0");
        });

        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedOnAdd();
        b.Property(p => p.Type).HasConversion<string>().IsRequired();
        b.Property(p => p.Status).HasConversion<string>().IsRequired();
        b.Property(p => p.DocDate).IsRequired();
        b.Property(p => p.Subtotal).HasPrecision(18, 2).IsRequired();
        b.Property(p => p.DiscountAmount).HasPrecision(18, 2).HasDefaultValue(0m);
        b.Property(p => p.Total).HasPrecision(18, 2).IsRequired();

        // DocNo is nullable (drafts are unnumbered) so the uniqueness index must allow duplicates
        // of NULL while rejecting two real numbers.
        b.HasIndex(p => p.DocNo).IsUnique();
        b.HasIndex(p => new { p.SupplierId, p.DocDate });
        b.HasIndex(p => p.DocDate);

        b.HasOne(p => p.Supplier)
            .WithMany()
            .HasForeignKey(p => p.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(p => p.Location)
            .WithMany()
            .HasForeignKey(p => p.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(p => p.OriginalPurchase)
            .WithMany()
            .HasForeignKey(p => p.OriginalPurchaseId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(p => p.Items)
            .WithOne(i => i.Purchase!)
            .HasForeignKey(i => i.PurchaseId)
            .OnDelete(DeleteBehavior.Cascade);

        AuditColumns.Map(b);
    }
}

public sealed class PurchaseItemConfig : IEntityTypeConfiguration<PurchaseItem>
{
    public void Configure(EntityTypeBuilder<PurchaseItem> b)
    {
        b.ToTable("PurchaseItems", t =>
        {
            t.HasCheckConstraint("CK_PurchaseItems_Quantity", "\"Quantity\" > 0");
            t.HasCheckConstraint("CK_PurchaseItems_BaseQuantity", "\"BaseQuantity\" > 0");
            t.HasCheckConstraint("CK_PurchaseItems_UnitPrice", "\"UnitPrice\" >= 0");
            t.HasCheckConstraint("CK_PurchaseItems_Discount", "\"DiscountAmount\" >= 0");
            t.HasCheckConstraint("CK_PurchaseItems_UnitFactor", "\"UnitFactor\" > 0");
        });

        b.HasKey(i => i.Id);
        b.Property(i => i.Id).ValueGeneratedOnAdd();
        b.Property(i => i.LineNo).IsRequired();
        b.Property(i => i.UnitName).IsRequired();
        b.Property(i => i.UnitFactor).HasPrecision(18, 6).IsRequired();
        b.Property(i => i.Quantity).HasPrecision(18, 3).IsRequired();
        b.Property(i => i.BaseQuantity).HasPrecision(18, 3).IsRequired();
        b.Property(i => i.UnitPrice).HasPrecision(18, 4).IsRequired();
        b.Property(i => i.DiscountAmount).HasPrecision(18, 2).HasDefaultValue(0m);
        b.Property(i => i.LineTotal).HasPrecision(18, 2).IsRequired();

        b.HasIndex(i => new { i.PurchaseId, i.LineNo }).IsUnique();
        b.HasIndex(i => i.LotId);

        b.HasOne(i => i.Product)
            .WithMany()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(i => i.ProductUnit)
            .WithMany()
            .HasForeignKey(i => i.ProductUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(i => i.Lot)
            .WithMany()
            .HasForeignKey(i => i.LotId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}