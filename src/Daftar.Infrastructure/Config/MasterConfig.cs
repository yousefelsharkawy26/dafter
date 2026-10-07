using Daftar.Domain.Inventory;
using Daftar.Domain.Parties;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

public sealed class StockLocationConfig : IEntityTypeConfiguration<StockLocation>
{
    public void Configure(EntityTypeBuilder<StockLocation> b)
    {
        b.ToTable("StockLocations", t =>
        {
            t.HasCheckConstraint("CK_StockLocations_Type", "\"Type\" IN ('Warehouse','Vehicle')");
        });

        b.HasKey(l => l.Id);
        b.Property(l => l.Id).ValueGeneratedOnAdd();
        b.Property(l => l.Name).IsRequired();
        b.Property(l => l.Type).HasConversion<string>().IsRequired();
        b.Property(l => l.IsActive).HasDefaultValue(true);

        b.HasIndex(l => l.Name).IsUnique();

        AuditColumns.Map(b);
    }
}

public sealed class PartyConfig : IEntityTypeConfiguration<Party>
{
    public void Configure(EntityTypeBuilder<Party> b)
    {
        b.ToTable("Parties", t =>
        {
            t.HasCheckConstraint("CK_Parties_Role", "IsCustomer = 1 OR IsSupplier = 1");
            t.HasCheckConstraint(
                "CK_Parties_CustomerType",
                "IsCustomer = 0 OR CustomerType IS NOT NULL");
            t.HasCheckConstraint(
                "CK_Parties_CustomerType_Values",
                "CustomerType IS NULL OR CustomerType IN ('Supermarket','Restaurant','Wholesale','Retail','Individual','Other')");
            t.HasCheckConstraint(
                "CK_Parties_Status",
                "\"Status\" IN ('Active','Pending','Blocked','Archived')");
            t.HasCheckConstraint("CK_Parties_CreditLimit", "\"CreditLimit\" >= 0");
            t.HasCheckConstraint("CK_Parties_PaymentTermsDays", "PaymentTermsDays >= 0");
        });

        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedOnAdd();
        b.Property(p => p.Code).IsRequired();
        b.Property(p => p.Name).IsRequired();
        b.Property(p => p.CustomerType).HasConversion<string>();
        b.Property(p => p.Status).HasConversion<string>().IsRequired();
        b.Property(p => p.CreditLimit).HasPrecision(18, 2).HasDefaultValue(0m);
        b.Property(p => p.PaymentTermsDays).HasDefaultValue(0);

        b.HasIndex(p => p.Code).IsUnique();
        b.HasIndex(p => p.Name);
        b.HasIndex(p => p.Phone);

        // Added with the P2 migration.
        b.HasOne(p => p.PriceList)
            .WithMany()
            .HasForeignKey(p => p.PriceListId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(p => p.AssignedDistributor)
            .WithMany()
            .HasForeignKey(p => p.AssignedDistributorId)
            .OnDelete(DeleteBehavior.Restrict);

        AuditColumns.Map(b);
    }
}

public sealed class SettingConfig : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> b)
    {
        b.ToTable("Settings");
        // Key is the primary key, not an autoincrement Id.
        b.HasKey(s => s.Key);
        b.Property(s => s.Key).ValueGeneratedNever();
        b.Property(s => s.Value).IsRequired();
        b.Property(s => s.UpdatedAt).IsRequired();
    }
}

public sealed class DocumentSequenceConfig : IEntityTypeConfiguration<DocumentSequence>
{
    public void Configure(EntityTypeBuilder<DocumentSequence> b)
    {
        b.ToTable("DocumentSequences");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedOnAdd();
        b.Property(s => s.DocType).HasConversion<string>().IsRequired();
        b.Property(s => s.Year).HasDefaultValue(0);
        b.Property(s => s.Prefix).IsRequired();
        b.Property(s => s.NextNumber).HasDefaultValue(1);
        b.Property(s => s.Padding).HasDefaultValue(6);

        b.HasIndex(s => new { s.DocType, s.Year }).IsUnique();
    }
}