using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

public class PurchaseService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser)
{
    /// <summary>Saves the bill, adds stock and updates each product's last purchase price, in one transaction.</summary>
    public async Task<int> CreateAsync(Purchase purchase)
    {
        currentUser.Demand(Permissions.PurchasesManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var id = await CreateInAsync(db, purchase);
        await tx.CommitAsync();
        return id;
    }

    /// <summary>
    /// Saves a new bill with its stock movements and prices on <paramref name="db"/>, inside the caller's transaction
    /// (the caller checks rights and commits). Also used by the Excel stock import (ProductExcelService).
    /// </summary>
    internal static async Task<int> CreateInAsync(AppDbContext db, Purchase purchase)
    {
        var lines = Prepare(purchase);
        purchase.CreatedAt = DateTime.Now;
        db.Purchases.Add(purchase);
        await db.SaveChangesAsync();

        AddMovements(db, purchase, lines);
        await db.SaveChangesAsync();

        await RecomputePurchasePriceAsync(db, lines.Select(l => l.StockItemId));
        await ApplyRetailPricesAsync(db, lines);
        await db.SaveChangesAsync();
        return purchase.Id;
    }

    /// <summary>The retail price typed on a purchase line (cost + profit %) becomes the product's sale price.</summary>
    private static async Task ApplyRetailPricesAsync(AppDbContext db, List<PurchaseLine> lines)
    {
        var prices = lines.Where(l => l.SalePrice is not null)
            .GroupBy(l => l.StockItemId).ToDictionary(g => g.Key, g => g.Last().SalePrice!.Value);
        if (prices.Count == 0) return;
        var ids = prices.Keys.ToList();
        foreach (var product in await db.StockItems.Where(v => ids.Contains(v.Id)).ToListAsync())
            product.SalePrice = prices[product.Id];
    }

    /// <summary>
    /// Edits a saved bill. Header, prices and lines can all change, except that a product's quantity can't go
    /// below what has already been sold/used from stock or returned to the supplier (and such a line can't be removed).
    /// </summary>
    public async Task UpdateAsync(Purchase changes)
    {
        currentUser.Demand(Permissions.PurchasesManage);
        var lines = Prepare(changes);

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

        var purchase = await db.Purchases.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == changes.Id)
            ?? throw new BusinessException("Purchase not found. It may have been deleted.");

        // Per product: new qty may not drop below the part already gone from stock, nor below what was returned.
        var returned = await GetReturnedQtyAsync(db, purchase.Id);
        var minimums = await GetMinimumQtyAsync(db, purchase, returned);
        var newQty = lines.GroupBy(l => l.StockItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty));
        foreach (var (productId, min) in minimums.Where(m => m.Value > 0))
        {
            var qty = newQty.GetValueOrDefault(productId);
            if (qty >= min) continue;

            var name = await ProductNameAsync(db, productId);
            var ret = returned.GetValueOrDefault(productId);
            if (ret <= 0)
                throw new BusinessException(qty == 0
                    ? $"{name} can't be removed: {min:0.##} of it has already been sold."
                    : $"{name}: {min:0.##} has already been sold, so quantity can't be less than {min:0.##}.");

            var why = min > ret
                ? $"{ret:0.##} was returned to the supplier and some has already been sold"
                : $"{ret:0.##} of it was returned to the supplier";
            throw new BusinessException(qty == 0
                ? $"{name} can't be removed: {why}."
                : $"{name}: {why}, so quantity can't be less than {min:0.##}.");
        }

        var affected = purchase.Lines.Select(l => l.StockItemId).Concat(lines.Select(l => l.StockItemId)).Distinct().ToList();

        purchase.PurchaseDate = changes.PurchaseDate;
        purchase.SupplierId = changes.SupplierId;
        purchase.BillNo = changes.BillNo;
        purchase.Discount = changes.Discount;
        purchase.SubTotal = changes.SubTotal;
        purchase.Total = changes.Total;
        purchase.Paid = changes.Paid;
        purchase.Notes = changes.Notes;

        db.PurchaseLines.RemoveRange(purchase.Lines);
        purchase.Lines = lines;

        await db.StockMovements
            .Where(m => m.Type == StockMovementType.Purchase && m.RefId == purchase.Id)
            .ExecuteDeleteAsync();
        AddMovements(db, purchase, lines);
        await db.SaveChangesAsync();

        await RecomputePurchasePriceAsync(db, affected);
        await ApplyRetailPricesAsync(db, lines);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<Purchase?> GetAsync(int purchaseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Purchases.AsNoTracking()
            .Include(p => p.Supplier)
            .Include(p => p.Lines.OrderBy(l => l.Id)).ThenInclude(l => l.StockItem).ThenInclude(v => v!.Product).ThenInclude(p => p!.Company)
            .FirstOrDefaultAsync(p => p.Id == purchaseId);
    }

    /// <summary>
    /// For each product on the bill: the lowest total qty it can be edited down to
    /// (= the part of this purchase that has already left stock, and at least what was returned to the supplier).
    /// 0 means freely editable.
    /// </summary>
    public async Task<Dictionary<int, decimal>> GetMinimumQtyAsync(int purchaseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var purchase = await db.Purchases.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == purchaseId);
        return purchase is null ? [] : await GetMinimumQtyAsync(db, purchase, await GetReturnedQtyAsync(db, purchaseId));
    }

    /// <summary>Qty per product already returned to the supplier against this bill.</summary>
    public async Task<Dictionary<int, decimal>> GetReturnedQtyAsync(int purchaseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await GetReturnedQtyAsync(db, purchaseId);
    }

    /// <summary>Returns made to the supplier against this bill (oldest first).</summary>
    public async Task<List<PurchaseReturn>> GetReturnsAsync(int purchaseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PurchaseReturns.AsNoTracking()
            .Where(r => r.PurchaseId == purchaseId)
            .OrderBy(r => r.ReturnDate).ThenBy(r => r.Id)
            .ToListAsync();
    }

    internal static async Task<Dictionary<int, decimal>> GetReturnedQtyAsync(AppDbContext db, int purchaseId) =>
        await (from l in db.PurchaseReturnLines
               join r in db.PurchaseReturns on l.PurchaseReturnId equals r.Id
               where r.PurchaseId == purchaseId
               group l by l.StockItemId into g
               select new { g.Key, Qty = g.Sum(x => x.Qty) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty);

    private static async Task<Dictionary<int, decimal>> GetMinimumQtyAsync(AppDbContext db, Purchase purchase, Dictionary<int, decimal> returned)
    {
        var onHand = await StockService.GetOnHandAsync(db, purchase.Lines.Select(l => l.StockItemId));
        return purchase.Lines.GroupBy(l => l.StockItemId).ToDictionary(
            g => g.Key,
            g => Math.Max(Math.Max(0, g.Sum(l => l.Qty) - onHand.GetValueOrDefault(g.Key)), returned.GetValueOrDefault(g.Key)));
    }

    public async Task DeleteAsync(int purchaseId)
    {
        currentUser.Demand(Permissions.PurchasesDelete);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var purchase = await db.Purchases.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == purchaseId)
            ?? throw new BusinessException("Purchase not found.");

        var returns = await db.PurchaseReturns.Where(r => r.PurchaseId == purchaseId).Select(r => r.Id).ToListAsync();
        if (returns.Count > 0)
            throw new BusinessException(
                $"Purchase #{purchaseId} has {(returns.Count == 1 ? "a return" : $"{returns.Count} returns")} to the supplier (return # {string.Join(", ", returns)}). Delete its returns first.");

        // Removing this stock must not push any product below zero.
        var blocked = (await GetMinimumQtyAsync(db, purchase, [])).FirstOrDefault(m => m.Value > 0);
        if (blocked.Value > 0)
        {
            var name = await ProductNameAsync(db, blocked.Key);
            throw new BusinessException(
                $"{blocked.Value:0.##} of {name} from this purchase has already been sold, so it can't be deleted. Edit it instead.");
        }

        var productIds = purchase.Lines.Select(l => l.StockItemId).Distinct().ToList();
        await db.StockMovements
            .Where(m => m.Type == StockMovementType.Purchase && m.RefId == purchaseId)
            .ExecuteDeleteAsync();
        db.Purchases.Remove(purchase);
        await db.SaveChangesAsync();

        await RecomputePurchasePriceAsync(db, productIds);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Validates the bill and computes line amounts and totals. Returns the lines to save.</summary>
    private static List<PurchaseLine> Prepare(Purchase purchase)
    {
        if (purchase.SupplierId <= 0)
            throw new BusinessException("Select a supplier.");
        var lines = purchase.Lines.Where(l => l.StockItemId > 0 && l.Qty > 0).ToList();
        if (lines.Count == 0)
            throw new BusinessException("Add at least one item.");
        if (lines.Any(l => l.Cost < 0))
            throw new BusinessException("Cost can't be negative.");
        if (lines.Any(l => l.SalePrice < 0))
            throw new BusinessException("Retail price can't be negative.");

        lines = lines.Select(l => new PurchaseLine
        {
            StockItemId = l.StockItemId, Qty = l.Qty, Cost = l.Cost, Amount = Math.Round(l.Qty * l.Cost, 2),
            SalePrice = l.SalePrice is { } price ? Math.Round(price, 2) : null,
        }).ToList();

        purchase.Lines = lines;
        purchase.Supplier = null;
        purchase.SubTotal = lines.Sum(l => l.Amount);
        purchase.Total = purchase.SubTotal - purchase.Discount;
        if (purchase.Discount < 0 || purchase.Total < 0)
            throw new BusinessException("Discount can't be more than the bill amount.");
        if (purchase.Paid < 0)
            throw new BusinessException("Paid can't be negative.");
        if (purchase.Paid > purchase.Total)
            throw new BusinessException("Paid can't be more than the bill total.");
        return lines;
    }

    private static void AddMovements(AppDbContext db, Purchase purchase, List<PurchaseLine> lines)
    {
        foreach (var l in lines)
        {
            db.StockMovements.Add(new StockMovement
            {
                StockItemId = l.StockItemId,
                MovementDate = purchase.PurchaseDate,
                Type = StockMovementType.Purchase,
                QtyIn = l.Qty,
                RefId = purchase.Id,
            });
        }
    }

    /// <summary>
    /// StockItem.PurchasePrice = cost on the latest remaining purchase line (latest bill date, then bill #, then line).
    /// If no purchase line is left for a product, its current price is kept. Call after SaveChanges, inside the transaction.
    /// </summary>
    private static async Task RecomputePurchasePriceAsync(AppDbContext db, IEnumerable<int> productIds)
    {
        var ids = productIds.Distinct().ToList();
        var products = await db.StockItems.Where(v => ids.Contains(v.Id)).ToListAsync();
        foreach (var product in products)
        {
            var cost = await (from l in db.PurchaseLines
                              join p in db.Purchases on l.PurchaseId equals p.Id
                              where l.StockItemId == product.Id
                              orderby p.PurchaseDate descending, p.Id descending, l.Id descending
                              select (decimal?)l.Cost).FirstOrDefaultAsync();
            if (cost is decimal c)
                product.PurchasePrice = c;
        }
    }

    private static Task<string> ProductNameAsync(AppDbContext db, int variantId) =>
        db.StockItems.Where(v => v.Id == variantId)
            .Select(v => v.Product!.Name + (v.Colour != null ? " " + v.Colour : "") + (v.Size != null ? " " + v.Size : "") + " (" + v.Barcode + ")").FirstAsync();
}
