using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

public class StockService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser)
{
    /// <summary>On-hand qty for the given variants (colour + size) (missing = 0). Uses the caller's context so it can join a transaction.</summary>
    public static async Task<Dictionary<int, decimal>> GetOnHandAsync(AppDbContext db, IEnumerable<int> variantIds)
    {
        var ids = variantIds.Distinct().ToList();
        return await db.StockMovements
            .Where(m => ids.Contains(m.StockItemId))
            .GroupBy(m => m.StockItemId)
            .Select(g => new { g.Key, Qty = g.Sum(m => m.QtyIn - m.QtyOut) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty);
    }

    public async Task<decimal> GetOnHandAsync(int variantId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var map = await GetOnHandAsync(db, [variantId]);
        return map.GetValueOrDefault(variantId);
    }

    /// <summary>Manual stock in/out (opening stock, damage, count correction).</summary>
    public async Task AdjustAsync(int variantId, DateTime date, StockMovementType type, decimal qty, string? notes)
    {
        currentUser.Demand(Permissions.StockAdjust);
        if (type is not (StockMovementType.Opening or StockMovementType.AdjustIn or StockMovementType.AdjustOut))
            throw new BusinessException("Only opening / adjustment movements can be entered manually.");
        if (qty <= 0)
            throw new BusinessException("Quantity must be more than zero.");

        await using var db = await dbFactory.CreateDbContextAsync();
        // Serializable: the on-hand check and the insert must see the same stock (no concurrent sale in between).
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

        if (type == StockMovementType.AdjustOut)
        {
            var onHand = (await GetOnHandAsync(db, [variantId])).GetValueOrDefault(variantId);
            if (qty > onHand)
                throw new BusinessException($"Only {onHand:0.##} in stock.");
        }

        var isOut = type == StockMovementType.AdjustOut;
        db.StockMovements.Add(new StockMovement
        {
            StockItemId = variantId,
            MovementDate = date,
            Type = type,
            QtyIn = isOut ? 0 : qty,
            QtyOut = isOut ? qty : 0,
            Notes = notes,
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// Physical count: compares the counted qty with on-hand and writes the difference as AdjustIn / AdjustOut
    /// (note "Stock count: system X, counted Y"). Returns the difference written (counted − system); 0 = nothing written.
    /// </summary>
    public async Task<decimal> CountAsync(int variantId, DateTime date, decimal countedQty, string? notes)
    {
        currentUser.Demand(Permissions.StockAdjust);
        if (variantId <= 0)
            throw new BusinessException("Select an item.");
        if (countedQty < 0)
            throw new BusinessException("Counted quantity can't be negative.");

        await using var db = await dbFactory.CreateDbContextAsync();
        // Serializable: the difference must be computed from the same stock the insert lands on.
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

        if (!await db.StockItems.AnyAsync(v => v.Id == variantId))
            throw new BusinessException("This item no longer exists.");

        var onHand = (await GetOnHandAsync(db, [variantId])).GetValueOrDefault(variantId);
        var diff = countedQty - onHand;
        if (diff == 0)
            return 0;

        var note = $"Stock count: system {onHand:0.##}, counted {countedQty:0.##}";
        if (!string.IsNullOrWhiteSpace(notes))
            note += " · " + notes.Trim();
        if (note.Length > 250)
            note = note[..250];

        db.StockMovements.Add(new StockMovement
        {
            StockItemId = variantId,
            MovementDate = date.Date,
            Type = diff > 0 ? StockMovementType.AdjustIn : StockMovementType.AdjustOut,
            QtyIn = diff > 0 ? diff : 0,
            QtyOut = diff < 0 ? -diff : 0,
            Notes = note,
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return diff;
    }

    /// <summary>Deletes a manual adjustment. Document movements are removed with their document.</summary>
    public async Task DeleteAdjustmentAsync(int movementId)
    {
        currentUser.Demand(Permissions.StockAdjust);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var m = await db.StockMovements.FindAsync(movementId)
            ?? throw new BusinessException("Entry not found.");
        if (m.RefId is not null)
            throw new BusinessException("This entry belongs to a purchase, sale or return. Delete that document instead.");

        if (m.QtyIn > 0)
        {
            var onHand = (await GetOnHandAsync(db, [m.StockItemId])).GetValueOrDefault(m.StockItemId);
            if (onHand - m.QtyIn < 0)
                throw new BusinessException("Deleting this would make stock negative.");
        }

        db.StockMovements.Remove(m);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
