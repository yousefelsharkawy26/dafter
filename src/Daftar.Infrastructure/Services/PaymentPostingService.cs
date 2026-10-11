using Daftar.Domain.Finance;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Daftar.Infrastructure.Services;

public sealed record PaymentPostingResult(int PaymentId, string DocNo);

/// <summary>
/// Posting a payment voucher. In one transaction: the voucher takes its DocNo, the party's net
/// balance moves, and the cash account moves by the matching amount.
///
/// A sale taken at the till creates its payment in the same transaction as the sale, so the till
/// and the receivable can never disagree.
/// </summary>
public sealed class PaymentPostingService(DaftarContext context)
{
    public async Task<PaymentPostingResult> PostAsync(
        int paymentId,
        int userId,
        DateOnly? businessDate = null,
        CancellationToken cancellationToken = default)
    {
        context.RequirePermission(Daftar.Domain.Security.Role.Codes.PaymentsManage);

        await using TransactionScope tx = await TransactionScope.BeginAsync(context, cancellationToken);
        try
        {
            var result = await PostCoreAsync(paymentId, userId, businessDate, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<PaymentPostingResult> PostCoreAsync(
        int paymentId,
        int userId,
        DateOnly? businessDate,
        CancellationToken cancellationToken)
    {
        var payment = await context.Payments
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw new DomainException($"Payment {paymentId} does not exist.");

        if (!payment.IsDraft)
            throw new DomainException($"Payment {paymentId} is already {payment.Status}.");

        var now = DateTime.UtcNow;
        var date = businessDate ?? payment.DocDate;

        var docNo = await NextDocNoAsync(
            payment.Direction == PaymentDirection.In ? DocumentType.PaymentIn : DocumentType.PaymentOut,
            date.Year,
            cancellationToken);

        payment.Post(docNo, userId, now);

        context.CashTransactions.Add(new CashTransaction
        {
            CashAccountId = payment.CashAccountId,
            OccurredAt = now,
            BusinessDate = date,
            Kind = payment.Direction == PaymentDirection.In
                ? CashTransactionKind.Collection
                : CashTransactionKind.SupplierPayment,
            Amount = payment.CashDelta,
            PaymentId = payment.Id,
            Note = payment.Reference,
            CreatedBy = userId,
        });

        context.PartyAccountTransactions.Add(new PartyAccountTransaction
        {
            PartyId = payment.PartyId,
            OccurredAt = now,
            BusinessDate = date,
            Kind = payment.Direction == PaymentDirection.In
                ? PartyLedgerKind.PaymentIn
                : PartyLedgerKind.PaymentOut,
            Amount = payment.PartyBalanceDelta,
            SourceType = "Payment",
            SourceId = payment.Id,
            Note = payment.Reference,
            CreatedBy = userId,
        });

        await context.SaveChangesAsync(cancellationToken);

        return new PaymentPostingResult(payment.Id, docNo);
    }

    /// <summary>
    /// Records an expense or a cash movement that is not a payment voucher — fuel, rent, salary,
    /// a deposit, a withdrawal. No party ledger row, because no party's balance changes.
    /// </summary>
    public async Task RecordCashMovementAsync(
        int cashAccountId,
        CashTransactionKind kind,
        decimal amount,
        int userId,
        DateOnly businessDate,
        ExpenseCategory? category = null,
        string? note = null,
        string? transferRef = null,
        CancellationToken cancellationToken = default)
    {
        // Expenses, deposits and withdrawals move money out of an account exactly as a voucher
        // does. PostAsync checks PaymentsManage; these did not, so a role without it could still
        // empty the till by calling them directly.
        context.RequirePermission(Daftar.Domain.Security.Role.Codes.PaymentsManage);

        if (amount <= 0m)
            throw new DomainException("A cash movement amount must be positive.");

        context.CashTransactions.Add(new CashTransaction
        {
            CashAccountId = cashAccountId,
            OccurredAt = DateTime.UtcNow,
            BusinessDate = businessDate,
            Kind = kind,
            Amount = kind is CashTransactionKind.Expense or CashTransactionKind.Withdrawal
                ? -amount
                : amount,
            Category = category,
            Note = note,
            TransferRef = transferRef,
            CreatedBy = userId,
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Moves money between two accounts as a matched pair of rows sharing a TransferRef, so the
    /// two sides can always be reconciled.
    /// </summary>
    public async Task TransferBetweenAccountsAsync(
        int fromAccountId,
        int toAccountId,
        decimal amount,
        int userId,
        DateOnly businessDate,
        CancellationToken cancellationToken = default)
    {
        // Moving money between two of the company's own accounts is still moving money, and it
        // is how a till gets emptied without a voucher existing to explain it.
        context.RequirePermission(Daftar.Domain.Security.Role.Codes.PaymentsManage);

        if (fromAccountId == toAccountId)
            throw new DomainException("A transfer needs two different accounts.");
        if (amount <= 0m)
            throw new DomainException("A transfer amount must be positive.");

        var transferRef = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        context.CashTransactions.Add(new CashTransaction
        {
            CashAccountId = fromAccountId, OccurredAt = now, BusinessDate = businessDate,
            Kind = CashTransactionKind.TransferOut, Amount = -amount,
            TransferRef = transferRef, CreatedBy = userId,
        });

        context.CashTransactions.Add(new CashTransaction
        {
            CashAccountId = toAccountId, OccurredAt = now, BusinessDate = businessDate,
            Kind = CashTransactionKind.TransferIn, Amount = amount,
            TransferRef = transferRef, CreatedBy = userId,
        });

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> NextDocNoAsync(DocumentType docType, int year, CancellationToken cancellationToken)
    {
        var sequence = await context.DocumentSequences
            .FirstOrDefaultAsync(s => s.DocType == docType && s.Year == year, cancellationToken)
            ?? await context.DocumentSequences
                .FirstOrDefaultAsync(s => s.DocType == docType && s.Year == 0, cancellationToken);

        if (sequence is null)
            throw new DomainException($"No DocumentSequences row for {docType} in {year}.");

        return sequence.Take(year);
    }
}