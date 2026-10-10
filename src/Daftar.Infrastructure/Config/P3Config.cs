using Daftar.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

public sealed class StockTransferConfig : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> b)
    {
        b.ToTable("StockTransfers", t =>
        {
            t.HasCheckConstraint("CK_StockTransfers_Status", "\"Status\" IN ('Draft','Posted','Cancelled')");
            t.HasCheckConstraint("CK_StockTransfers_DraftHasNoNo", "\"Status\" = 'Draft' OR \"DocNo\" IS NOT NULL");
            t.HasCheckConstraint(
                "CK_StockTransfers_CancelHasReason",
                "\"Status\" <> 'Cancelled' OR \"CancelReason\" IS NOT NULL");
            // Two different locations.
            t.HasCheckConstraint("CK_StockTransfers_Distinct", "\"FromLocationId\" <> \"ToLocationId\"");
        });

        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedOnAdd();
        b.Property(t => t.FromLocationId).IsRequired();
        b.Property(t => t.ToLocationId).IsRequired();
        b.Property(t => t.Status).HasConversion<string>().IsRequired();
        b.Property(t => t.DocDate).IsRequired();

        b.HasIndex(t => t.DocNo).IsUnique();
        b.HasIndex(t => t.DistributorSessionId);

        b.HasOne(t => t.FromLocation).WithMany().HasForeignKey(t => t.FromLocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(t => t.ToLocation).WithMany().HasForeignKey(t => t.ToLocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(t => t.DistributorSession).WithMany().HasForeignKey(t => t.DistributorSessionId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(t => t.Items).WithOne(i => i.Transfer!)
            .HasForeignKey(i => i.TransferId).OnDelete(DeleteBehavior.Cascade);

        AuditColumns.Map(b);
    }
}

public sealed class StockTransferItemConfig : IEntityTypeConfiguration<StockTransferItem>
{
    public void Configure(EntityTypeBuilder<StockTransferItem> b)
    {
        b.ToTable("StockTransferItems", t =>
        {
            t.HasCheckConstraint("CK_StockTransferItems_BaseQuantity", "\"BaseQuantity\" > 0");
        });

        b.HasKey(i => i.Id);
        b.Property(i => i.Id).ValueGeneratedOnAdd();
        b.Property(i => i.BaseQuantity).HasPrecision(18, 3).IsRequired();

        b.HasIndex(i => new { i.TransferId, i.LotId }).IsUnique();

        b.HasOne(i => i.Lot).WithMany().HasForeignKey(i => i.LotId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class StockCountConfig : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> b)
    {
        b.ToTable("StockCounts", t =>
        {
            t.HasCheckConstraint("CK_StockCounts_Status", "\"Status\" IN ('Draft','Posted','Cancelled')");
            t.HasCheckConstraint("CK_StockCounts_DraftHasNoNo", "\"Status\" = 'Draft' OR \"DocNo\" IS NOT NULL");
            t.HasCheckConstraint(
                "CK_StockCounts_CancelHasReason",
                "\"Status\" <> 'Cancelled' OR \"CancelReason\" IS NOT NULL");
        });

        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedOnAdd();
        b.Property(c => c.LocationId).IsRequired();
        b.Property(c => c.Status).HasConversion<string>().IsRequired();
        b.Property(c => c.DocDate).IsRequired();

        b.HasIndex(c => c.DocNo).IsUnique();
        b.HasIndex(c => c.LocationId);
        // One count per distributor session. Two would settle the same session twice.
        b.HasIndex(c => c.DistributorSessionId).IsUnique();

        b.HasOne(c => c.Location).WithMany().HasForeignKey(c => c.LocationId).OnDelete(DeleteBehavior.Restrict);
        // The owning side of the session <-> count link. DistributorSession.StockCountId is the
        // dependent side; declaring both as owners produced a shadow FK column.
        b.HasOne(c => c.DistributorSession).WithMany()
            .HasForeignKey(c => c.DistributorSessionId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(c => c.Items).WithOne(i => i.StockCount!)
            .HasForeignKey(i => i.StockCountId).OnDelete(DeleteBehavior.Cascade);

        AuditColumns.Map(b);
    }
}

public sealed class StockCountItemConfig : IEntityTypeConfiguration<StockCountItem>
{
    public void Configure(EntityTypeBuilder<StockCountItem> b)
    {
        b.ToTable("StockCountItems", t =>
        {
            t.HasCheckConstraint("CK_StockCountItems_SystemQty", "\"SystemQty\" >= 0");
            t.HasCheckConstraint("CK_StockCountItems_CountedQty", "\"CountedQty\" IS NULL OR \"CountedQty\" >= 0");
        });

        b.HasKey(i => i.Id);
        b.Property(i => i.Id).ValueGeneratedOnAdd();
        b.Property(i => i.SystemQty).HasPrecision(18, 3).IsRequired();
        b.Property(i => i.CountedQty).HasPrecision(18, 3);
        b.Property(i => i.DiffQty).HasPrecision(18, 3);

        b.HasIndex(i => new { i.StockCountId, i.LotId }).IsUnique();

        b.HasOne(i => i.Lot).WithMany().HasForeignKey(i => i.LotId).OnDelete(DeleteBehavior.Restrict);
    }
}