using Daftar.Domain.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Daftar.Infrastructure.Config;

public sealed class PaymentConfig : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("Payments", t =>
        {
            t.HasCheckConstraint("CK_Payments_Direction", "\"Direction\" IN ('In','Out')");
            t.HasCheckConstraint(
                "CK_Payments_Method",
                "\"Method\" IN ('Cash','Transfer','Wallet','Card','Cheque')");
            t.HasCheckConstraint("CK_Payments_Amount", "\"Amount\" > 0");
            t.HasCheckConstraint("CK_Payments_Status", "\"Status\" IN ('Draft','Posted','Cancelled')");
            t.HasCheckConstraint("CK_Payments_DraftHasNoNo", "\"Status\" = 'Draft' OR \"DocNo\" IS NOT NULL");
            t.HasCheckConstraint(
                "CK_Payments_CancelHasReason",
                "\"Status\" <> 'Cancelled' OR \"CancelReason\" IS NOT NULL");
            t.HasCheckConstraint("CK_Payments_OneDocument", "\"SaleId\" IS NULL OR \"PurchaseId\" IS NULL");
        });

        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedOnAdd();
        b.Property(p => p.Direction).HasConversion<string>().IsRequired();
        b.Property(p => p.Method).HasConversion<string>().IsRequired();
        b.Property(p => p.Status).HasConversion<string>().IsRequired();
        b.Property(p => p.Amount).HasPrecision(18, 2).IsRequired();
        b.Property(p => p.DocDate).IsRequired();

        b.HasIndex(p => p.DocNo).IsUnique();
        b.HasIndex(p => new { p.PartyId, p.DocDate });
        b.HasIndex(p => p.DistributorSessionId);

        b.HasOne(p => p.Party).WithMany().HasForeignKey(p => p.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(p => p.CashAccount).WithMany().HasForeignKey(p => p.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(p => p.Sale).WithMany().HasForeignKey(p => p.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(p => p.Purchase).WithMany().HasForeignKey(p => p.PurchaseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(p => p.DistributorSession).WithMany().HasForeignKey(p => p.DistributorSessionId).OnDelete(DeleteBehavior.Restrict);

        AuditColumns.Map(b);
    }
}

public sealed class PartyAccountTransactionConfig : IEntityTypeConfiguration<PartyAccountTransaction>
{
    private static readonly string Kinds =
        "'Opening','Sale','SaleReturn','Purchase','PurchaseReturn',"
        + "'PaymentIn','PaymentOut','Adjustment','Reversal'";

    public void Configure(EntityTypeBuilder<PartyAccountTransaction> b)
    {
        b.ToTable("PartyAccountTransactions", t =>
        {
            t.HasCheckConstraint("CK_PartyAccountTransactions_Kind", $"\"Kind\" IN ({Kinds})");
        });

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.OccurredAt).IsRequired();
        b.Property(x => x.BusinessDate).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().IsRequired();
        b.Property(x => x.Amount).HasPrecision(18, 2).IsRequired();
        b.Property(x => x.SourceType).IsRequired();
        b.Property(x => x.CreatedBy).IsRequired();

        b.HasIndex(x => new { x.PartyId, x.BusinessDate });
        b.HasIndex(x => new { x.SourceType, x.SourceId });

        b.HasOne(x => x.Party).WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Reverses).WithMany().HasForeignKey(x => x.ReversesId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CashTransactionConfig : IEntityTypeConfiguration<CashTransaction>
{
    private static readonly string Kinds =
        "'Opening','Collection','SupplierPayment','Expense','TransferIn','TransferOut',"
        + "'Deposit','Withdrawal','Adjustment','Reversal'";

    public void Configure(EntityTypeBuilder<CashTransaction> b)
    {
        b.ToTable("CashTransactions", t =>
        {
            t.HasCheckConstraint("CK_CashTransactions_Kind", $"\"Kind\" IN ({Kinds})");
            t.HasCheckConstraint(
                "CK_CashTransactions_Category",
                "Category IS NULL OR Category IN "
                + "('Fuel','Rent','Salary','Commission','Utilities','Maintenance','Other')");
        });

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.OccurredAt).IsRequired();
        b.Property(x => x.BusinessDate).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().IsRequired();
        b.Property(x => x.Amount).HasPrecision(18, 2).IsRequired();
        b.Property(x => x.Category).HasConversion<string>();
        b.Property(x => x.CreatedBy).IsRequired();

        b.HasIndex(x => new { x.CashAccountId, x.BusinessDate });
        b.HasIndex(x => x.DistributorSessionId);

        b.HasOne(x => x.CashAccount).WithMany(a => a.Transactions)
            .HasForeignKey(x => x.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Payment).WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Reverses).WithMany().HasForeignKey(x => x.ReversesId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.DistributorSession).WithMany().HasForeignKey(x => x.DistributorSessionId).OnDelete(DeleteBehavior.Restrict);
    }
}