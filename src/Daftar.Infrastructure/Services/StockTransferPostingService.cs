using Daftar.Domain.Inventory;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Daftar.Infrastructure.Services;

public sealed record TransferPostingResult(int TransferId, string DocNo, int RowsWritten);

/// <summary>
/// Posting a stock transfer. Writes two movements per line — TransferOut negative at the source,
/// TransferIn positive at the destination — sharing one SourceId, in a single transaction.
/// This is how a distributor's vehicle gets loaded from the warehouse.
/// </summary>
public sealed class StockTransferPostingService(DaftarContext context)
{
    public async Task<TransferPostingResult> PostAsync(
        int transferId,
        int userId,
        DateOnly? businessDate = null,
        CancellationToken cancellationToken = default)
    {
        context.RequirePermission(Daftar.Domain.Security.Role.Codes.StockAdjust);

        await using TransactionScope tx = await TransactionScope.BeginAsync(context, cancellationToken);
        try
        {
            var result = await PostCoreAsync(transferId, userId, businessDate, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<TransferPostingResult> PostCoreAsync(
        int transferId,
        int userId,
        DateOnly? businessDate,
        CancellationToken cancellationToken)
    {
        var transfer = await context.StockTransfers
            .Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.Id == transferId, cancellationToken)
            ?? throw new DomainException($"Transfer {transferId} does not exist.");

        if (!transfer.IsDraft)
            throw new DomainException($"Transfer {transferId} is already {transfer.Status}.");
        if (transfer.FromLocationId == transfer.ToLocationId)
            throw new DomainException("A transfer needs two different locations.");

        var now = DateTime.UtcNow;
        var date = businessDate ?? transfer.DocDate;

        var docNo = await NextDocNoAsync(date.Year, cancellationToken);
        var rows = 0;

        foreach (var item in transfer.Items)
        {
            var lot = await context.Lots.FirstOrDefaultAsync(l => l.Id == item.LotId, cancellationToken)
                ?? throw new DomainException($"Lot {item.LotId} does not exist.");

            // Out of the source: this is what refuses to go negative if stock is short.
            await ApplyMovementAsync(
                lot, transfer.FromLocationId, InventoryMovementKind.TransferOut,
                item.BaseQuantity, transfer.Id, userId, date, now, cancellationToken);

            await ApplyMovementAsync(
                lot, transfer.ToLocationId, InventoryMovementKind.TransferIn,
                item.BaseQuantity, transfer.Id, userId, date, now, cancellationToken);

            rows += 2;
        }

        transfer.Post(docNo, userId, now);
        await context.SaveChangesAsync(cancellationToken);

        return new TransferPostingResult(transfer.Id, docNo, rows);
    }

    private async Task ApplyMovementAsync(
        Lot lot,
        int locationId,
        InventoryMovementKind kind,
        decimal magnitude,
        int transferId,
        int userId,
        DateOnly businessDate,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var quantity = InventoryMovement.SignedQuantity(kind, magnitude);

        var balance = await context.StockBalances
            .FirstOrDefaultAsync(b => b.LotId == lot.Id && b.LocationId == locationId, cancellationToken);

        if (balance is null)
        {
            balance = StockBalance.Open(lot.Id, locationId, nowUtc);
            context.StockBalances.Add(balance);
        }

        balance.Apply(quantity, nowUtc);

        context.InventoryMovements.Add(new InventoryMovement
        {
            OccurredAt = nowUtc,
            BusinessDate = businessDate,
            ProductId = lot.ProductId,
            LotId = lot.Id,
            LocationId = locationId,
            Kind = kind,
            Quantity = quantity,
            SourceType = MovementSourceType.Transfer,
            SourceId = transferId,
            TransferId = transferId,
            CreatedBy = userId,
        });
    }

    private async Task<string> NextDocNoAsync(int year, CancellationToken cancellationToken)
    {
        var sequence = await context.DocumentSequences
            .FirstOrDefaultAsync(s => s.DocType == DocumentType.Transfer && s.Year == year, cancellationToken)
            ?? await context.DocumentSequences
                .FirstOrDefaultAsync(s => s.DocType == DocumentType.Transfer && s.Year == 0, cancellationToken);

        if (sequence is null)
            throw new DomainException($"No DocumentSequences row for Transfer in {year}.");

        return sequence.Take(year);
    }
}