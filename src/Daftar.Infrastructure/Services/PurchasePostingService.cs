using Daftar.Domain.Inventory;
using Daftar.Domain.Purchasing;
using Daftar.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Daftar.Infrastructure.Services;

/// <summary>Result of posting a purchase: the document number and the stock it created.</summary>
public sealed record PurchasePostingResult(
    int PurchaseId,
    string DocNo,
    IReadOnlyList<int> LotIds);

/// <summary>
/// Posting a purchase invoice — the one operation that has to be a single transaction.
///
/// The header gets its DocNo, every line becomes a lot, every lot gets an inventory movement, and
/// every balance is adjusted. Intermediate SaveChanges calls exist only so EF generates Lot Ids;
/// they all run inside one explicit transaction, so a failure anywhere rolls the whole thing back
/// with no gaps in the number sequence and no half-received stock.
///
/// Rule 1 (posting is one transaction) lives here; the entity invariants live in the Domain.
/// </summary>
public sealed class PurchasePostingService(DaftarContext context)
{
    public async Task<PurchasePostingResult> PostAsync(
        int purchaseId,
        int userId,
        DateOnly? businessDate = null,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await TransactionScope.BeginAsync(context, cancellationToken);

        try
        {
            var result = await PostCoreAsync(purchaseId, userId, businessDate, cancellationToken);
            await TransactionScope.CommitAsync(tx, context, cancellationToken);
            return result;
        }
        catch
        {
            await TransactionScope.RollbackAsync(tx, context, cancellationToken);
            throw;
        }
    }

    private async Task<PurchasePostingResult> PostCoreAsync(
        int purchaseId,
        int userId,
        DateOnly? businessDate,
        CancellationToken cancellationToken)
    {
        var purchase = await context.Purchases
            .Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.Id == purchaseId, cancellationToken)
            ?? throw new DomainException($"Purchase {purchaseId} does not exist.");

        if (!purchase.IsDraft)
            throw new DomainException($"Purchase {purchaseId} is already {purchase.Status}.");

        var now = DateTime.UtcNow;
        var date = businessDate ?? purchase.DocDate;

        var docNo = await NextDocNoAsync(
            purchase.Type == PurchaseType.Return ? DocumentType.PurchaseReturn : DocumentType.Purchase,
            date.Year,
            cancellationToken);

        var lotIds = new List<int>();

        foreach (var item in purchase.Items)
        {
            var product = await context.Products
                .FirstOrDefaultAsync(p => p.Id == item.ProductId, cancellationToken)
                ?? throw new DomainException($"Product {item.ProductId} does not exist.");

            var lot = Lot.Create(
                productId: product.Id,
                receivedOn: date,
                supplierId: purchase.SupplierId,
                unitCost: item.ComputeLotUnitCost());

            // Freeze so the cost of this delivery cannot drift once stock exists.
            lot.Freeze();

            context.Lots.Add(lot);
            // Generates the Lot Id; still inside the transaction.
            await context.SaveChangesAsync(cancellationToken);

            lotIds.Add(lot.Id);
            item.LotId = lot.Id;

            ApplyMovement(
                lot: lot,
                locationId: purchase.LocationId,
                kind: purchase.Type == PurchaseType.Return
                    ? InventoryMovementKind.PurchaseReturn
                    : InventoryMovementKind.Receipt,
                magnitude: item.BaseQuantity,
                sourceType: MovementSourceType.Purchase,
                sourceId: purchase.Id,
                userId: userId,
                businessDate: date,
                nowUtc: now);
        }

        purchase.Post(docNo, userId, now);

        // The payable: a purchase increases what we owe the supplier, and a purchase return
        // gives it back. Written here rather than in P1 because PartyAccountTransactions did
        // not exist until P2 — without it, a supplier's balance only ever showed payments.
        context.PartyAccountTransactions.Add(new Domain.Finance.PartyAccountTransaction
        {
            PartyId = purchase.SupplierId,
            OccurredAt = now,
            BusinessDate = date,
            Kind = purchase.Type == PurchaseType.Return
                ? Domain.Finance.PartyLedgerKind.PurchaseReturn
                : Domain.Finance.PartyLedgerKind.Purchase,
            // Signed from our side: a purchase makes US owe the supplier, so it is negative.
            // A purchase return gives it back, which is positive.
            Amount = purchase.Type == PurchaseType.Return ? purchase.Total : -purchase.Total,
            DueDate = purchase.DueDate,
            SourceType = "Purchase",
            SourceId = purchase.Id,
            CreatedBy = userId,
        });

        // Rule 11: a product's base unit stops moving once stock exists.
        await FreezeUsedCatalogAsync(purchase, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);

        return new PurchasePostingResult(purchase.Id, docNo, lotIds);
    }

    /// <summary>
    /// Allocates the next number from DocumentSequences, inside the posting transaction.
    /// Prefers a row for the exact year; falls back to the Year = 0 row, which never resets.
    /// </summary>
    private async Task<string> NextDocNoAsync(DocumentType docType, int year, CancellationToken cancellationToken)
    {
        var sequence = await context.DocumentSequences
            .FirstOrDefaultAsync(s => s.DocType == docType && s.Year == year, cancellationToken)
            ?? await context.DocumentSequences
                .FirstOrDefaultAsync(s => s.DocType == docType && s.Year == 0, cancellationToken);

        if (sequence is null)
            throw new DomainException(
                $"No DocumentSequences row for {docType} in {year}; the seed data is missing.");

        return sequence.Take(year);
    }

    /// <summary>
    /// Stages a movement and moves the balance. The caller saves, so the row and the balance change
    /// always land together. A transfer calls this twice with opposite signs.
    /// </summary>
    public void ApplyMovement(
        Lot lot,
        int locationId,
        InventoryMovementKind kind,
        decimal magnitude,
        MovementSourceType sourceType,
        int? sourceId,
        int userId,
        DateOnly businessDate,
        DateTime nowUtc)
    {
        var quantity = InventoryMovement.SignedQuantity(kind, magnitude);

        var balance = context.StockBalances
            .FirstOrDefault(b => b.LotId == lot.Id && b.LocationId == locationId);

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
            SourceType = sourceType,
            SourceId = sourceId,
            CreatedBy = userId,
        });
    }

    private async Task FreezeUsedCatalogAsync(Purchase purchase, CancellationToken cancellationToken)
    {
        var productIds = purchase.Items.Select(i => i.ProductId).Distinct().ToList();

        var products = await context.Products
            .Include(p => p.Units)
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        foreach (var product in products)
        {
            product.Freeze();
            foreach (var unit in product.Units)
                unit.Freeze();
        }
    }
}