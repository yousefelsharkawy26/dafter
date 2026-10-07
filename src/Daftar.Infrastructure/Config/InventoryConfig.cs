using Daftar.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

public sealed class LotConfig : IEntityTypeConfiguration<Lot>
{
    public void Configure(EntityTypeBuilder<Lot> b)
    {
        b.ToTable("Lots", t =>
        {
            t.HasCheckConstraint("CK_Lots_Status", "\"Status\" IN ('Active','Blocked')");
            t.HasCheckConstraint("CK_Lots_Blocked", "\"Status\" <> 'Blocked' OR \"BlockReason\" IS NOT NULL");
            t.HasCheckConstraint("CK_Lots_UnitCost", "\"UnitCost\" >= 0");
        });

        b.HasKey(l => l.Id);
        b.Property(l => l.Id).ValueGeneratedOnAdd();
        b.Property(l => l.UnitCost).HasPrecision(18, 4).IsRequired();
        b.Property(l => l.ReceivedOn).IsRequired();
        b.Property(l => l.Status).HasConversion<string>().IsRequired();

        b.HasIndex(l => l.ProductId);
        b.HasIndex(l => l.SupplierId);

        b.HasOne(l => l.Product)
            .WithMany()
            .HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(l => l.Supplier)
            .WithMany()
            .HasForeignKey(l => l.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        AuditColumns.Map(b);
    }
}

public sealed class StockBalanceConfig : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> b)
    {
        b.ToTable("StockBalances", t =>
        {
            // Quantity is a decimal (TEXT on SQLite), so this CHECK is documentation only —
            // the Domain enforces it in StockBalance.Apply.
            t.HasCheckConstraint("CK_StockBalances_Quantity", "\"Quantity\" >= 0");
        });

        // Composite key: no Id column.
        b.HasKey(x => new { x.LotId, x.LocationId });
        b.Property(x => x.Quantity).HasPrecision(18, 3).IsRequired();
        b.Property(x => x.UpdatedAt).IsRequired();

        b.HasIndex(x => x.LocationId);

        b.HasOne(x => x.Lot)
            .WithMany(l => l.Balances)
            .HasForeignKey(x => x.LotId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.Location)
            .WithMany()
            .HasForeignKey(x => x.LocationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class InventoryMovementConfig : IEntityTypeConfiguration<InventoryMovement>
{
    private static readonly string[] Kinds =
    [
        "Opening", "Receipt", "PurchaseReturn", "Sale", "SaleReturn",
        "TransferIn", "TransferOut", "CountAdjust", "Adjust", "Scrap", "Reversal",
    ];

    public void Configure(EntityTypeBuilder<InventoryMovement> b)
    {
        var kindList = $"'{string.Join("','", Kinds)}'";

        b.ToTable("InventoryMovements", t =>
        {
            t.HasCheckConstraint("CK_InventoryMovements_Kind", $"\"Kind\" IN ({kindList})");
            t.HasCheckConstraint(
                "CK_InventoryMovements_ReasonCode",
                $"\"Kind\" NOT IN ('Adjust','Scrap') OR \"ReasonCode\" IS NOT NULL");
            // Quantity is a decimal (TEXT on SQLite), so the zero check is Domain-enforced.
            t.HasCheckConstraint("CK_InventoryMovements_SourceType",
                "\"SourceType\" IN ('Purchase','Sale','Transfer','Count','Manual','Opening')");
        });

        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedOnAdd();
        b.Property(m => m.OccurredAt).IsRequired();
        b.Property(m => m.BusinessDate).IsRequired();
        b.Property(m => m.Kind).HasConversion<string>().IsRequired();
        b.Property(m => m.Quantity).HasPrecision(18, 3).IsRequired();
        b.Property(m => m.SourceType).HasConversion<string>().IsRequired();
        b.Property(m => m.CreatedBy).IsRequired();

        b.HasIndex(m => new { m.ProductId, m.OccurredAt });
        b.HasIndex(m => m.LotId);
        b.HasIndex(m => new { m.LocationId, m.OccurredAt });
        b.HasIndex(m => new { m.SourceType, m.SourceId });
        b.HasIndex(m => m.TransferId);
        b.HasIndex(m => m.StockCountId);
        b.HasIndex(m => m.DistributorSessionId);

        b.HasOne(m => m.Lot)
            .WithMany()
            .HasForeignKey(m => m.LotId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(m => m.Location)
            .WithMany()
            .HasForeignKey(m => m.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(m => m.ReversesMovement)
            .WithMany()
            .HasForeignKey(m => m.ReversesMovementId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(m => m.Transfer).WithMany().HasForeignKey(m => m.TransferId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(m => m.StockCount).WithMany().HasForeignKey(m => m.StockCountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(m => m.DistributorSession).WithMany().HasForeignKey(m => m.DistributorSessionId).OnDelete(DeleteBehavior.Restrict);
    }
}