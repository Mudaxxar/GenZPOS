using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>One item of an invoice as seen by the return screen (same product + same rate grouped together).</summary>
public class SaleReturnableLine
{
    public int StockItemId { get; init; }
    public string ProductName { get; init; } = "";
    public string? ArticleNo { get; init; }
    public string? Barcode { get; init; }
    public string? Colour { get; init; }
    public string? Size { get; init; }
    public string Unit { get; init; } = "";
    public decimal Sold { get; init; }
    public decimal Returned { get; init; }
    public decimal Returnable => Math.Max(0, Sold - Returned);
    /// <summary>Invoice rate (before the invoice discount).</summary>
    public decimal Rate { get; init; }
    /// <summary>Cost recorded on the sale line.</summary>
    public decimal Cost { get; init; }

    /// <summary>
    /// What the customer really paid for all <see cref="Sold"/> of it: its amount less its share of the invoice discount
    /// (owner 2026-10-23: a 20% invoice discount is not refunded). The shares of an invoice add up to its Total exactly.
    /// </summary>
    public decimal NetTotal { get; init; }
    /// <summary>Money already given back for it by earlier returns.</summary>
    public decimal ReturnedAmount { get; init; }
    /// <summary>Rate after the invoice discount (for display).</summary>
    public decimal NetRate => Sold == 0 ? Rate : Math.Round(NetTotal / Sold, 2);

    /// <summary>
    /// Refund for <paramref name="qty"/>: qty × net rate; returning all that is left gets exactly what is left of
    /// <see cref="NetTotal"/>, so a full return gives back the invoice total to the paisa.
    /// </summary>
    public decimal AmountFor(decimal qty) =>
        qty <= 0 || Sold == 0 ? 0
        : qty >= Returnable ? Math.Max(0, NetTotal - ReturnedAmount)
        : Math.Min(Math.Round(qty * NetTotal / Sold, 2), Math.Max(0, NetTotal - ReturnedAmount));

    /// <summary>Entered on the return screen.</summary>
    public decimal ReturnQty { get; set; }
}

/// <summary>An item that can be returned in an exchange (New Sale screen): from which invoice, sold to whom.</summary>
public sealed record ExchangeCandidate(int SaleId, DateTime SaleDate, int CustomerId, string CustomerName, SaleReturnableLine Line);

/// <summary>A returned item in the till's exchange list: which one, how many, and its refund.</summary>
public sealed class ExchangePick(ExchangeCandidate candidate)
{
    public ExchangeCandidate Candidate { get; } = candidate;
    public bool Selected { get; set; }
    public decimal Qty { get; set; } = 1;
    /// <summary>What the customer paid for it (same amount SaleReturnService saves).</summary>
    public decimal Amount => Candidate.Line.AmountFor(Qty);
}

/// <summary>Customer returns (stock back in, customer balance reduced / cash refunded). See CLAUDE.md "Money rules".</summary>
public class SaleReturnService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser)
{
    /// <summary>Per invoice item: sold, already returned and still returnable qty, its rate and net (after discount) value.</summary>
    public async Task<List<SaleReturnableLine>> GetReturnableAsync(int saleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var sale = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.Id == saleId);
        if (sale is null) return [];
        var lines = await db.SaleLines.AsNoTracking()
            .Where(l => l.SaleId == saleId)
            .Include(l => l.StockItem).ThenInclude(v => v!.Product).ThenInclude(p => p!.Company)
            .OrderBy(l => l.Id)
            .ToListAsync();
        var returned = await GetReturnedAsync(db, saleId);
        return BuildReturnable(sale, lines, returned);
    }

    /// <summary>
    /// Saves a return against an invoice: checks quantities (≤ sold − already returned), uses the invoice rate and cost,
    /// puts the stock back, all in one Serializable transaction. Returns the new return's Id.
    /// </summary>
    public async Task<int> CreateAsync(SaleReturn ret)
    {
        currentUser.Demand(Permissions.SaleReturnsManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var entity = await PrepareInAsync(db, ret);
        entity.Refunded = ret.Refunded;
        await SaveInAsync(db, entity);
        await tx.CommitAsync();
        return entity.Id;
    }

    /// <summary>
    /// Checks a return against its invoice (qty ≤ sold − already returned, the invoice rate) and builds it, with its
    /// amounts (invoice rate less its share of the invoice discount) and Total, on <paramref name="db"/> inside the
    /// caller's transaction. Nothing is saved; <see cref="SaleReturn.Refunded"/> is set by the caller.
    /// </summary>
    internal static async Task<SaleReturn> PrepareInAsync(AppDbContext db, SaleReturn ret)
    {
        if (ret.SaleId is not int saleId || saleId <= 0)
            throw new BusinessException("Returns are made against an invoice. Open the invoice and click \"Return items\".");
        var requested = ret.Lines.Where(l => l.StockItemId > 0 && l.Qty > 0).ToList();
        if (requested.Count == 0)
            throw new BusinessException("Enter the quantity being returned for at least one item.");
        if (ret.Lines.Any(l => l.Qty < 0))
            throw new BusinessException("Return quantity can't be negative.");

        var notes = string.IsNullOrWhiteSpace(ret.Notes) ? null : ret.Notes.Trim();
        if (notes is { Length: > 250 })
            throw new BusinessException("Notes can be at most 250 characters.");

        var sale = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.Id == saleId)
            ?? throw new BusinessException($"Invoice #{saleId} was not found. It may have been deleted.");
        if (ret.ReturnDate.Date < sale.SaleDate.Date)
            throw new BusinessException($"Return date can't be before the invoice date ({sale.SaleDate:dd-MMM-yyyy}).");

        var saleLines = await db.SaleLines.AsNoTracking()
            .Where(l => l.SaleId == saleId)
            .Include(l => l.StockItem).ThenInclude(v => v!.Product).ThenInclude(p => p!.Company)
            .ToListAsync();
        var available = BuildReturnable(sale, saleLines, await GetReturnedAsync(db, saleId));

        var lines = new List<SaleReturnLine>();
        foreach (var g in requested.GroupBy(l => (l.StockItemId, l.Rate)))
        {
            var item = available.FirstOrDefault(a => a.StockItemId == g.Key.StockItemId && a.Rate == g.Key.Rate)
                ?? throw new BusinessException(available.Any(a => a.StockItemId == g.Key.StockItemId)
                    ? "The return rate must be the rate on the invoice."
                    : "One of the items is not on this invoice.");
            var qty = g.Sum(l => l.Qty);
            if (qty > item.Returnable)
                throw new BusinessException(item.Returnable <= 0
                    ? $"{item.ProductName} ({item.Barcode}): everything sold on invoice #{saleId} has already been returned."
                    : $"{item.ProductName} ({item.Barcode}): only {item.Returnable:0.##} can be returned ({item.Sold:0.##} sold, {item.Returned:0.##} already returned).");
            lines.Add(new SaleReturnLine
            {
                StockItemId = item.StockItemId,
                Qty = qty,
                Rate = item.Rate,
                Cost = item.Cost,
                Amount = item.AmountFor(qty), // invoice rate less its share of the invoice discount
            });
        }

        return new SaleReturn
        {
            ReturnDate = ret.ReturnDate.Date,
            CustomerId = sale.CustomerId,
            SaleId = saleId,
            Total = lines.Sum(l => l.Amount),
            Notes = notes,
            Lines = lines,
        };
    }

    /// <summary>Checks the refund and saves a prepared return with its stock movements (caller's transaction).</summary>
    internal static async Task SaveInAsync(AppDbContext db, SaleReturn entity)
    {
        if (entity.Refunded < 0)
            throw new BusinessException("Cash refunded can't be negative.");
        if (entity.Refunded > entity.Total)
            throw new BusinessException("Cash refunded can't be more than the return total.");
        if (entity.CustomerId == Customer.WalkInId && entity.Refunded != entity.Total)
            throw new BusinessException("A walk-in customer has no account, so the full return amount must be refunded in cash.");

        entity.CreatedAt = DateTime.Now;
        db.SaleReturns.Add(entity);
        await db.SaveChangesAsync();

        foreach (var l in entity.Lines)
        {
            db.StockMovements.Add(new StockMovement
            {
                StockItemId = l.StockItemId,
                MovementDate = entity.ReturnDate,
                Type = StockMovementType.SaleReturn,
                QtyIn = l.Qty,
                RefId = entity.Id,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Exchange on the New Sale screen: what can be returned for a scanned / typed code. A barcode → that piece on the
    /// latest invoice it can still be returned from; otherwise an invoice no. (digits, optionally "#") → every item still
    /// returnable on it. Empty when nothing matches.
    /// </summary>
    public async Task<List<ExchangeCandidate>> FindForExchangeAsync(string code)
    {
        code = code.Trim();
        if (code.Length == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();

        var itemId = await db.StockItems.Where(v => v.Barcode == code).Select(v => (int?)v.Id).FirstOrDefaultAsync();
        if (itemId is int id)
        {
            var saleIds = await db.SaleLines.Where(l => l.StockItemId == id)
                .Select(l => l.SaleId).Distinct().OrderByDescending(s => s).ToListAsync();
            foreach (var saleId in saleIds)
            {
                var found = (await CandidatesAsync(db, saleId)).Where(c => c.Line.StockItemId == id).ToList();
                if (found.Count > 0) return found;
            }
            return [];
        }
        return int.TryParse(code.TrimStart('#'), out var invoice) ? await CandidatesAsync(db, invoice) : [];
    }

    private static async Task<List<ExchangeCandidate>> CandidatesAsync(AppDbContext db, int saleId)
    {
        var sale = await db.Sales.AsNoTracking().Include(s => s.Customer).FirstOrDefaultAsync(s => s.Id == saleId);
        if (sale is null) return [];
        var lines = await db.SaleLines.AsNoTracking()
            .Where(l => l.SaleId == saleId)
            .Include(l => l.StockItem).ThenInclude(v => v!.Product)
            .OrderBy(l => l.Id)
            .ToListAsync();
        return BuildReturnable(sale, lines, await GetReturnedAsync(db, saleId))
            .Where(l => l.Returnable > 0)
            .Select(l => new ExchangeCandidate(sale.Id, sale.SaleDate, sale.CustomerId, sale.Customer?.Name ?? "", l))
            .ToList();
    }

    public async Task<SaleReturn?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SaleReturns.AsNoTracking()
            .Include(r => r.Customer)
            .Include(r => r.Sale)
            .Include(r => r.Lines.OrderBy(l => l.Id)).ThenInclude(l => l.StockItem).ThenInclude(v => v!.Product).ThenInclude(p => p!.Company)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    /// <summary>Deletes the return and takes its stock back out (blocked if that stock has been sold again).</summary>
    public async Task DeleteAsync(int id)
    {
        currentUser.Demand(Permissions.SaleReturnsManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var ret = await db.SaleReturns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new BusinessException("Return not found. It may already have been deleted.");

        var onHand = await StockService.GetOnHandAsync(db, ret.Lines.Select(l => l.StockItemId));
        foreach (var g in ret.Lines.GroupBy(l => l.StockItemId))
        {
            var qty = g.Sum(l => l.Qty);
            var have = onHand.GetValueOrDefault(g.Key);
            if (have - qty < 0)
            {
                var name = await db.StockItems.Where(v => v.Id == g.Key)
                    .Select(v => v.Product!.Name + (v.Colour != null ? " " + v.Colour : "") + (v.Size != null ? " " + v.Size : "") + " (" + v.Barcode + ")").FirstOrDefaultAsync() ?? $"item #{g.Key}";
                throw new BusinessException(
                    $"Can't delete: {name} has only {have:0.##} in stock, but this return put back {qty:0.##} (some has been sold again).");
            }
        }

        await db.StockMovements
            .Where(m => m.Type == StockMovementType.SaleReturn && m.RefId == id)
            .ExecuteDeleteAsync();
        db.SaleReturns.Remove(ret);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Qty and money already returned against the invoice, per (product, rate).</summary>
    private static async Task<List<(int StockItemId, decimal Rate, decimal Qty, decimal Amount)>> GetReturnedAsync(AppDbContext db, int saleId)
    {
        var rows = await (from l in db.SaleReturnLines
                          join r in db.SaleReturns on l.SaleReturnId equals r.Id
                          where r.SaleId == saleId
                          group l by new { l.StockItemId, l.Rate } into g
                          select new { g.Key.StockItemId, g.Key.Rate, Qty = g.Sum(x => x.Qty), Amount = g.Sum(x => x.Amount) })
            .ToListAsync();
        return rows.Select(x => (x.StockItemId, x.Rate, x.Qty, x.Amount)).ToList();
    }

    private static List<SaleReturnableLine> BuildReturnable(Sale sale, List<SaleLine> saleLines,
        List<(int StockItemId, decimal Rate, decimal Qty, decimal Amount)> returned)
    {
        var net = NetShares(sale, saleLines);
        return saleLines
            .GroupBy(l => (l.StockItemId, l.Rate))
            .Select(g =>
            {
                var item = g.First().StockItem;
                var back = returned.Where(r => r.StockItemId == g.Key.StockItemId && r.Rate == g.Key.Rate).ToList();
                return new SaleReturnableLine
                {
                    StockItemId = g.Key.StockItemId,
                    Rate = g.Key.Rate,
                    ProductName = item?.Product?.Name ?? $"Item #{g.Key.StockItemId}",
                    ArticleNo = item?.ArticleNo,
                    Barcode = item?.Barcode,
                    Colour = item?.Colour,
                    Size = item?.Size,
                    Unit = item?.Product?.Unit ?? "",
                    Cost = g.First().Cost,
                    Sold = g.Sum(l => l.Qty),
                    Returned = back.Sum(r => r.Qty),
                    ReturnedAmount = back.Sum(r => r.Amount),
                    NetTotal = g.Sum(l => net[l.Id]),
                };
            })
            .ToList();
    }

    /// <summary>
    /// Each invoice line's share of the invoice Total (its Amount less its pro-rata part of the discount, like the item
    /// sales report), in paisa; the rounding paisas go to the lines with the largest remainders so the shares add up to
    /// the Total exactly.
    /// </summary>
    public static Dictionary<int, decimal> NetShares(Sale sale, List<SaleLine> lines)
    {
        var subTotal = lines.Sum(l => l.Amount);
        if (subTotal <= 0 || sale.Discount <= 0)
            return lines.ToDictionary(l => l.Id, l => l.Amount);
        var exact = lines.ToDictionary(l => l.Id, l => l.Amount * sale.Total / subTotal);
        var shares = exact.ToDictionary(x => x.Key, x => Math.Floor(x.Value * 100) / 100);
        var left = (int)Math.Round((sale.Total - shares.Values.Sum()) * 100);
        foreach (var id in exact.OrderByDescending(x => x.Value - shares[x.Key]).ThenBy(x => x.Key).Select(x => x.Key).Take(Math.Max(0, left)))
            shares[id] += 0.01m;
        return shares;
    }
}
