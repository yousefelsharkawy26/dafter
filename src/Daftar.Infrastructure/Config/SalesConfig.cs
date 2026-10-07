using Daftar.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

public sealed class SaleConfig : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> b)
    {
        b.ToTable("Sales", t =>
        {
            t.HasCheckConstraint("CK_Sales_Type", "\"Type\" IN ('Invoice','Return')");
            t.HasCheckConstraint("CK_Sales_Status", "\"Status\" IN ('Draft','Posted','Cancelled')");
            t.HasCheckConstraint("CK_Sales_DraftHasNoNo", "\"Status\" = 'Draft' OR \"DocNo\" IS NOT NULL");
            t.HasCheckConstraint(
                "CK_Sales_CancelHasReason",
                "\"Status\" <> 'Cancelled' OR \"CancelReason\" IS NOT NULL");
            t.HasCheckConstraint("CK_Sales_ReturnHasOriginal",
                "\"Type\" <> 'Return' OR \"OriginalSaleId\" IS NOT NULL");
            t.HasCheckConstraint("CK_Sales_Discount", "\"DiscountAmount\" >= 0");
        });

        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedOnAdd();
        b.Property(s => s.Type).HasConversion<string>().IsRequired();
        b.Property(s => s.Status).HasConversion<string>().IsRequired();
        b.Property(s => s.DocDate).IsRequired();
        b.Property(s => s.Subtotal).HasPrecision(18, 2).IsRequired();
        b.Property(s => s.DiscountAmount).HasPrecision(18, 2).HasDefaultValue(0m);
        b.Property(s => s.Total).HasPrecision(18, 2).IsRequired();

        b.HasIndex(s => s.DocNo).IsUnique();
        b.HasIndex(s => new { s.PartyId, s.DocDate });
        b.HasIndex(s => s.DocDate);
        b.HasIndex(s => s.DistributorSessionId);

        b.HasOne(s => s.Distributor).WithMany().HasForeignKey(s => s.DistributorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(s => s.DistributorSession).WithMany().HasForeignKey(s => s.DistributorSessionId).OnDelete(DeleteBehavior.Restrict);

        b.HasOne(s => s.Party)
            .WithMany()
            .HasForeignKey(s => s.PartyId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(s => s.Location)
            .WithMany()
            .HasForeignKey(s => s.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(s => s.PriceList)
            .WithMany()
            .HasForeignKey(s => s.PriceListId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(s => s.OriginalSale)
            .WithMany()
            .HasForeignKey(s => s.OriginalSaleId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(s => s.Items)
            .WithOne(i => i.Sale!)
            .HasForeignKey(i => i.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        AuditColumns.Map(b);
    }
}

public sealed class SaleItemConfig : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> b)
    {
        b.ToTable("SaleItems", t =>
        {
            t.HasCheckConstraint("CK_SaleItems_Quantity", "\"Quantity\" > 0");
            t.HasCheckConstraint("CK_SaleItems_BaseQuantity", "\"BaseQuantity\" > 0");
            t.HasCheckConstraint("CK_SaleItems_UnitPrice", "\"UnitPrice\" >= 0");
            t.HasCheckConstraint("CK_SaleItems_ListPrice", "\"ListPrice\" >= 0");
            t.HasCheckConstraint("CK_SaleItems_Discount", "\"DiscountAmount\" >= 0");
            t.HasCheckConstraint("CK_SaleItems_UnitFactor", "\"UnitFactor\" > 0");
        });

        b.HasKey(i => i.Id);
        b.Property(i => i.Id).ValueGeneratedOnAdd();
        b.Property(i => i.LineNo).IsRequired();
        b.Property(i => i.UnitName).IsRequired();
        b.Property(i => i.UnitFactor).HasPrecision(18, 6).IsRequired();
        b.Property(i => i.Quantity).HasPrecision(18, 3).IsRequired();
        b.Property(i => i.BaseQuantity).HasPrecision(18, 3).IsRequired();
        b.Property(i => i.ListPrice).HasPrecision(18, 4).IsRequired();
        b.Property(i => i.UnitPrice).HasPrecision(18, 4).IsRequired();
        b.Property(i => i.DiscountAmount).HasPrecision(18, 2).HasDefaultValue(0m);
        b.Property(i => i.LineTotal).HasPrecision(18, 2).IsRequired();
        b.Property(i => i.CostTotal).HasPrecision(18, 2).HasDefaultValue(0m);

        b.HasIndex(i => new { i.SaleId, i.LineNo }).IsUnique();
        b.HasIndex(i => i.ProductId);

        b.HasOne(i => i.Product)
            .WithMany()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(i => i.ProductUnit)
            .WithMany()
            .HasForeignKey(i => i.ProductUnitId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(i => i.OriginalSaleItem)
            .WithMany()
            .HasForeignKey(i => i.OriginalSaleItemId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(i => i.Lots)
            .WithOne(l => l.SaleItem!)
            .HasForeignKey(l => l.SaleItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SaleItemLotConfig : IEntityTypeConfiguration<SaleItemLot>
{
    public void Configure(EntityTypeBuilder<SaleItemLot> b)
    {
        b.ToTable("SaleItemLots", t =>
        {
            t.HasCheckConstraint("CK_SaleItemLots_BaseQuantity", "\"BaseQuantity\" > 0");
            t.HasCheckConstraint("CK_SaleItemLots_UnitCost", "\"UnitCost\" >= 0");
        });

        b.HasKey(l => l.Id);
        b.Property(l => l.Id).ValueGeneratedOnAdd();
        b.Property(l => l.BaseQuantity).HasPrecision(18, 3).IsRequired();
        b.Property(l => l.UnitCost).HasPrecision(18, 4).IsRequired();

        b.HasIndex(l => new { l.SaleItemId, l.LotId }).IsUnique();

        // The recall query: "which customers received lot X".
        b.HasIndex(l => l.LotId);

        b.HasOne(l => l.Lot)
            .WithMany()
            .HasForeignKey(l => l.LotId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}