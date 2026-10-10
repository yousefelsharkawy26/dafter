using Daftar.Domain.Distributors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

public sealed class DistributorConfig : IEntityTypeConfiguration<Distributor>
{
    public void Configure(EntityTypeBuilder<Distributor> b)
    {
        b.ToTable("Distributors", t =>
        {
            t.HasCheckConstraint(
                "CK_Distributors_CommissionBasis", "\"CommissionBasis\" IN ('Sales','Collections')");
            t.HasCheckConstraint("CK_Distributors_CommissionRate", "CAST(\"CommissionRate\" AS REAL) >= 0 AND CAST(\"CommissionRate\" AS REAL) <= 100");
        });

        b.HasKey(d => d.Id);
        b.Property(d => d.Id).ValueGeneratedOnAdd();
        b.Property(d => d.Name).IsRequired();
        b.Property(d => d.StockLocationId).IsRequired();
        b.Property(d => d.CashAccountId).IsRequired();
        b.Property(d => d.CommissionRate).HasPrecision(5, 2).HasDefaultValue(0m);
        b.Property(d => d.CommissionBasis).HasConversion<string>().IsRequired();
        b.Property(d => d.IsActive).HasDefaultValue(true);

        // One vehicle per distributor, one custody account per distributor, one login per distributor.
        b.HasIndex(d => d.StockLocationId).IsUnique();
        b.HasIndex(d => d.CashAccountId).IsUnique();
        b.HasIndex(d => d.UserId).IsUnique();
        b.HasIndex(d => d.Name);

        b.HasOne(d => d.StockLocation).WithMany().HasForeignKey(d => d.StockLocationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(d => d.CashAccount).WithMany().HasForeignKey(d => d.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Restrict);

        AuditColumns.Map(b);
    }
}

public sealed class DistributorSessionConfig : IEntityTypeConfiguration<DistributorSession>
{
    public void Configure(EntityTypeBuilder<DistributorSession> b)
    {
        b.ToTable("DistributorSessions", t =>
        {
            t.HasCheckConstraint("CK_DistributorSessions_Status", "\"Status\" IN ('Open','Closed')");
            t.HasCheckConstraint(
                "CK_DistributorSessions_ClosedHasCloser",
                "\"Status\" <> 'Closed' OR (\"ClosedAt\" IS NOT NULL AND \"ClosedBy\" IS NOT NULL)");
        });

        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedOnAdd();
        b.Property(s => s.Status).HasConversion<string>().IsRequired();
        b.Property(s => s.OpenedAt).IsRequired();
        b.Property(s => s.OpenedBy).IsRequired();

        // The settlement snapshot.
        b.Property(s => s.SalesTotal).HasPrecision(18, 2);
        b.Property(s => s.ReturnsTotal).HasPrecision(18, 2);
        b.Property(s => s.CollectionsTotal).HasPrecision(18, 2);
        b.Property(s => s.ExpectedCash).HasPrecision(18, 2);
        b.Property(s => s.CountedCash).HasPrecision(18, 2);
        b.Property(s => s.CashDifference).HasPrecision(18, 2);
        b.Property(s => s.StockShortageValue).HasPrecision(18, 2);
        b.Property(s => s.CommissionAmount).HasPrecision(18, 2);

        // One open session per distributor. Partial unique index, added in the migration.
        b.HasIndex(s => s.DistributorId).HasFilter("Status = 'Open'");
        b.HasIndex(s => new { s.DistributorId, s.Status });

        b.HasOne(s => s.Distributor).WithMany(d => d.Sessions)
            .HasForeignKey(s => s.DistributorId).OnDelete(DeleteBehavior.Restrict);

        AuditColumns.Map(b);
    }
}