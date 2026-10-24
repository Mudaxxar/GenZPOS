using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>One product of a supplier bill as seen by the return-to-supplier screen.</summary>
public class PurchaseReturnableLine
{
    public int StockItemId { get; init; }
    public string ProductName { get; init; } = "";
    public string Unit { get; init; } = "";
    public decimal Bought { get; init; }
    public decimal Returned { get; init; }
    public decimal OnHand { get; init; }
    /// <summary>Bought − already returned, but never more than is in stock now.</summary>
    public decimal Returnable => Math.Max(0, Math.Min(Bought - Returned, OnHand));
    /// <summary>Cost per unit on the bill (average if the product is on the bill at different costs).</summary>
    public decimal Cost { get; init; }

    /// <summary>Entered on the return screen.</summary>
    public decimal ReturnQty { get; set; }
}

/// <summary>Returns to supplier (stock out, amount owed to supplier reduced / cash received back). See CLAUDE.md "Money rules".</summary>
public class PurchaseReturnService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser)
{
    /// <summary>Per product on the bill: bought, already returned, on hand and what can still go back.</summary>
    public async Task<List<PurchaseReturnableLine>> GetReturnableAsync(int purchaseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await BuildReturnableAsync(db, purchaseId);
    }

    /// <summary>
    /// Saves a return to the supplier against a bill: qty ≤ bought − returned and ≤ on hand, at the bill cost;
    /// takes the stock out, all in one Serializable transaction. Returns the new return's Id.
    /// </summary>
    public async Task<int> CreateAsync(PurchaseReturn ret)
    {
        currentUser.Demand(Permissions.PurchaseReturnsManage);
        if (ret.PurchaseId is not int purchaseId || purchaseId <= 0)
            throw new BusinessException("Returns to a supplier are made against a purchase bill. Open the purchase and click \"Return to supplier\".");
        if (ret.Lines.Any(l => l.Qty < 0))
            throw new BusinessException("Return quantity can't be negative.");
        var requested = ret.Lines.Where(l => l.StockItemId > 0 && l.Qty > 0).ToList();
        if (requested.Count == 0)
            throw new BusinessException("Enter the quantity being returned for at least one item.");

        var notes = string.IsNullOrWhiteSpace(ret.Notes) ? null : ret.Notes.Trim();
        if (notes is { Length: > 250 })
            throw new BusinessException("Notes can be at most 250 characters.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

        var purchase = await db.Purchases.AsNoTracking().FirstOrDefaultAsync(p => p.Id == purchaseId)
            ?? throw new BusinessException($"Purchase #{purchaseId} was not found. It may have been deleted.");
        if (ret.ReturnDate.Date < purchase.PurchaseDate.Date)
            throw new BusinessException($"Return date can't be before the purchase date ({purchase.PurchaseDate:dd-MMM-yyyy}).");

        var available = await BuildReturnableAsync(db, purchaseId);
        var lines = new List<PurchaseReturnLine>();
        foreach (var g in requested.GroupBy(l => l.StockItemId))
        {
            var item = available.FirstOrDefault(a => a.StockItemId == g.Key)
                ?? throw new BusinessException("One of the items is not on this purchase bill.");
            var qty = g.Sum(l => l.Qty);
            if (qty > item.Bought - item.Returned)
                throw new BusinessException(item.Bought - item.Returned <= 0
                    ? $"{item.ProductName}: everything bought on this bill has already been returned."
                    : $"{item.ProductName}: only {item.Bought - item.Returned:0.##} can be returned ({item.Bought:0.##} bought, {item.Returned:0.##} already returned).");
            if (qty > item.OnHand)
                throw new BusinessException($"{item.ProductName}: only {Math.Max(0, item.OnHand):0.##} in stock, so {qty:0.##} can't be returned.");
            lines.Add(new PurchaseReturnLine
            {
                StockItemId = item.StockItemId,
                Qty = qty,
                Cost = item.Cost,
                Amount = Math.Round(qty * item.Cost, 2),
            });
        }

        var total = lines.Sum(l => l.Amount);
        if (ret.Received < 0)
            throw new BusinessException("Cash received can't be negative.");
        if (ret.Received > total)
            throw new BusinessException("Cash received can't be more than the return total.");

        var entity = new PurchaseReturn
        {
            ReturnDate = ret.ReturnDate.Date,
            SupplierId = purchase.SupplierId,
            PurchaseId = purchaseId,
            Total = total,
            Received = ret.Received,
            Notes = notes,
            CreatedAt = DateTime.Now,
            Lines = lines,
        };
        db.PurchaseReturns.Add(entity);
        await db.SaveChangesAsync();

        foreach (var l in lines)
        {
            db.StockMovements.Add(new StockMovement
            {
                StockItemId = l.StockItemId,
                MovementDate = entity.ReturnDate,
                Type = StockMovementType.PurchaseReturn,
                QtyOut = l.Qty,
                RefId = entity.Id,
            });
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return entity.Id;
    }

    public async Task<PurchaseReturn?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PurchaseReturns.AsNoTracking()
            .Include(r => r.Supplier)
            .Include(r => r.Purchase)
            .Include(r => r.Lines.OrderBy(l => l.Id)).ThenInclude(l => l.StockItem).ThenInclude(v => v!.Product).ThenInclude(p => p!.Company)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    /// <summary>Deletes the return; its stock comes back in and the supplier balance goes back up.</summary>
    public async Task DeleteAsync(int id)
    {
        currentUser.Demand(Permissions.PurchaseReturnsManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var ret = await db.PurchaseReturns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new BusinessException("Return not found. It may already have been deleted.");

        await db.StockMovements
            .Where(m => m.Type == StockMovementType.PurchaseReturn && m.RefId == id)
            .ExecuteDeleteAsync();
        db.PurchaseReturns.Remove(ret);
        Audit.Add(db, currentUser, Audit.PurchaseReturnDeleted,
            $"Purchase return #{id} of {ret.ReturnDate:dd-MMM-yyyy} (bill #{ret.PurchaseId}): {ret.Lines.Count} line(s), total {ret.Total:N2}, cash back {ret.Received:N2}", id);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static async Task<List<PurchaseReturnableLine>> BuildReturnableAsync(AppDbContext db, int purchaseId)
    {
        var lines = await db.PurchaseLines.AsNoTracking()
            .Where(l => l.PurchaseId == purchaseId)
            .Include(l => l.StockItem).ThenInclude(v => v!.Product).ThenInclude(p => p!.Company)
            .OrderBy(l => l.Id)
            .ToListAsync();
        var returned = await PurchaseService.GetReturnedQtyAsync(db, purchaseId);
        var onHand = await StockService.GetOnHandAsync(db, lines.Select(l => l.StockItemId));

        return lines
            .GroupBy(l => l.StockItemId)
            .Select(g =>
            {
                var qty = g.Sum(l => l.Qty);
                var cost = g.Select(l => l.Cost).Distinct().Count() == 1
                    ? g.First().Cost
                    : qty == 0 ? 0 : Math.Round(g.Sum(l => l.Qty * l.Cost) / qty, 2);
                return new PurchaseReturnableLine
                {
                    StockItemId = g.Key,
                    ProductName = g.First().StockItem?.DisplayName ?? $"Item #{g.Key}",
                    Unit = g.First().StockItem?.Product?.Unit ?? "",
                    Bought = qty,
                    Returned = returned.GetValueOrDefault(g.Key),
                    OnHand = onHand.GetValueOrDefault(g.Key),
                    Cost = cost,
                };
            })
            .ToList();
    }
}
