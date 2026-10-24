using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

public class SaleService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser)
{
    /// <summary>
    /// Saves the invoice and removes stock in one Serializable transaction (stock check + insert together,
    /// so two counters can't sell the same last tin). Blocks overselling.
    /// </summary>
    public async Task<int> CreateAsync(Sale sale)
    {
        currentUser.Demand(Permissions.SalesCreate);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var id = await CreateInAsync(db, sale, currentUser);
        await tx.CommitAsync();
        return id;
    }

    /// <summary>
    /// Exchange (owner 2026-10-23): the customer brings items back and takes others, on one screen and one receipt.
    /// Saves the new invoice and the return(s) (one per original invoice) in ONE transaction. The returned value pays
    /// for the new items first: invoice <c>Paid</c> = that part + <paramref name="cashNow"/> when the customer pays the
    /// difference; each return's <c>Refunded</c> = its part + the cash given back when the returns are worth more
    /// (<paramref name="cashNow"/> negative). So balance and Day Book follow the usual Money rules: the customer's balance
    /// changes by (new total − returned total − cash now) and net cash = cash now. Returns the new invoice id.
    /// </summary>
    /// <param name="returns">Returns against earlier invoices (SaleId + lines with StockItemId, Qty, Rate); amounts are
    /// worked out here (invoice rate less its share of the invoice discount).</param>
    /// <param name="cashNow">Cash the customer pays now (positive, at most new total − returned), or cash given back to
    /// them (negative, at most returned − new total). Walk-in: exactly the difference.</param>
    public async Task<int> CreateExchangeAsync(Sale sale, IReadOnlyList<SaleReturn> returns, decimal cashNow)
    {
        currentUser.Demand(Permissions.SalesCreate);
        currentUser.Demand(Permissions.SaleReturnsManage);
        if (returns.Count == 0)
            throw new BusinessException("Add the returned item(s) first, or save it as a normal sale.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

        // Returns are worked out first (amounts after the original invoice's discount) to know what they are worth.
        var prepared = new List<SaleReturn>();
        foreach (var g in returns.GroupBy(r => r.SaleId))
        {
            var ret = new SaleReturn
            {
                SaleId = g.Key, ReturnDate = sale.SaleDate.Date,
                Lines = g.SelectMany(r => r.Lines).Select(l => new SaleReturnLine { StockItemId = l.StockItemId, Qty = l.Qty, Rate = l.Rate }).ToList(),
            };
            prepared.Add(await SaleReturnService.PrepareInAsync(db, ret));
        }
        if (prepared.FirstOrDefault(r => r.CustomerId != sale.CustomerId) is { } other)
        {
            var name = await db.Customers.Where(c => c.Id == other.CustomerId).Select(c => c.Name).FirstOrDefaultAsync();
            throw new BusinessException($"Invoice #{other.SaleId} was sold to {name}: an exchange must be for the same customer.");
        }

        var newTotal = sale.Lines.Where(l => l.StockItemId > 0 && l.Qty > 0).Sum(l => Math.Round(l.Qty * l.Rate, 2)) - sale.Discount;
        var returned = prepared.Sum(r => r.Total);
        var setOff = Math.Max(0, Math.Min(returned, newTotal)); // returned value used to pay for the new items
        if (cashNow > 0 && cashNow > newTotal - returned)
            throw new BusinessException($"Paid now can be at most {Math.Max(0, newTotal - returned):N2} (new items {newTotal:N2} − returned {returned:N2}).");
        if (cashNow < 0 && -cashNow > returned - newTotal)
            throw new BusinessException($"Cash back can be at most {Math.Max(0, returned - newTotal):N2} (returned {returned:N2} − new items {newTotal:N2}).");
        if (sale.CustomerId == Customer.WalkInId && cashNow != newTotal - returned)
            throw new BusinessException(newTotal >= returned
                ? $"Walk-in customer must pay the difference in full: {newTotal - returned:N2}."
                : $"Walk-in customer has no account: give back the whole difference in cash ({returned - newTotal:N2}).");

        sale.Paid = setOff + Math.Max(cashNow, 0);
        var saleId = await CreateInAsync(db, sale, currentUser);

        decimal setOffLeft = setOff, cashBackLeft = Math.Max(-cashNow, 0);
        foreach (var ret in prepared)
        {
            var part = Math.Min(setOffLeft, ret.Total);
            setOffLeft -= part;
            var cash = Math.Min(cashBackLeft, ret.Total - part);
            cashBackLeft -= cash;
            ret.Refunded = part + cash;
            ret.ExchangeSaleId = saleId;
            ret.Notes = $"Exchange: new invoice #{saleId}";
            await SaleReturnService.SaveInAsync(db, ret);
        }

        await tx.CommitAsync();
        AppLog.Info($"Exchange by \"{currentUser.Session?.UserName}\": invoice #{saleId} ({newTotal:N2}) with return(s) " +
                    $"#{string.Join(", #", prepared.Select(r => r.Id))} ({returned:N2}), cash now {cashNow:N2}");
        return saleId;
    }

    /// <summary>
    /// Validates and saves an invoice with its stock movements on <paramref name="db"/>, inside the caller's
    /// Serializable transaction (the caller checks rights and commits).
    /// </summary>
    internal static async Task<int> CreateInAsync(AppDbContext db, Sale sale, CurrentUser user)
    {
        if (sale.CustomerId <= 0)
            throw new BusinessException("Select a customer.");
        var lines = sale.Lines.Where(l => l.StockItemId > 0 && l.Qty > 0).ToList();
        if (lines.Count == 0)
            throw new BusinessException("Add at least one item.");
        if (lines.Any(l => l.Rate < 0))
            throw new BusinessException("Rate can't be negative.");

        foreach (var l in lines)
        {
            l.Amount = Math.Round(l.Qty * l.Rate, 2);
            l.StockItem = null;
        }

        sale.Notes = string.IsNullOrWhiteSpace(sale.Notes) ? null : sale.Notes.Trim();
        if (sale.Notes is { Length: > 250 })
            throw new BusinessException("Notes can be at most 250 characters.");

        sale.Lines = lines;
        sale.Customer = null;
        sale.SaleDate = sale.SaleDate.Date;
        sale.SubTotal = lines.Sum(l => l.Amount);
        sale.Total = sale.SubTotal - sale.Discount;
        if (sale.Discount < 0 || sale.Total < 0)
            throw new BusinessException("Discount can't be more than the invoice amount.");
        if (sale.Paid < 0)
            throw new BusinessException("Paid can't be negative.");
        if (sale.Paid > sale.Total)
            throw new BusinessException("Paid can't be more than the invoice total. Enter the amount kept, not the cash handed over.");
        if (sale.CustomerId == Customer.WalkInId && sale.Paid < sale.Total)
            throw new BusinessException("Walk-in customer must pay in full. Select or add a customer to sell on credit.");
        sale.CreatedAt = DateTime.Now;

        if (!await db.Customers.AnyAsync(c => c.Id == sale.CustomerId))
            throw new BusinessException("The selected customer no longer exists. Please pick the customer again.");

        // Cost for profit = the variant's purchase price right now (read inside the transaction, never from the UI).
        var variantIds = lines.Select(l => l.StockItemId).Distinct().ToList();
        var products = await db.StockItems.Where(v => variantIds.Contains(v.Id))
            .Select(v => new
            {
                v.Id, v.PurchasePrice, v.SalePrice, v.Product!.MaxDiscountPercent,
                Name = v.Product.Name + (v.Colour != null ? " " + v.Colour : "") + (v.Size != null ? " " + v.Size : ""),
            })
            .ToDictionaryAsync(p => p.Id);
        var discountRoom = 0m;
        foreach (var l in lines)
        {
            if (!products.TryGetValue(l.StockItemId, out var p))
                throw new BusinessException("One of the items no longer exists. Please remove it and add it again.");
            l.Cost = p.PurchasePrice;

            // Per-item maximum discount (DiscountRules): rate floor, then the room left for the invoice discount.
            if (DiscountRules.MinRate(p.SalePrice, p.MaxDiscountPercent) is { } minRate && l.Rate < minRate)
                throw new BusinessException(
                    $"{p.Name}: rate can't be below {minRate:N2} (maximum discount {p.MaxDiscountPercent:0.##}% of {p.SalePrice:N2}).");
            discountRoom += DiscountRules.Room(l.Qty, l.Rate, p.SalePrice, p.MaxDiscountPercent);
        }
        if (sale.Discount > discountRoom)
            throw new BusinessException(
                $"Discount can be at most {discountRoom:N2} on this invoice (items' maximum discount).");

        var onHand = await StockService.GetOnHandAsync(db, lines.Select(l => l.StockItemId));
        foreach (var g in lines.GroupBy(l => l.StockItemId))
        {
            var need = g.Sum(l => l.Qty);
            var have = onHand.GetValueOrDefault(g.Key);
            if (need > have)
            {
                var name = await db.StockItems.Where(v => v.Id == g.Key)
                    .Select(v => v.Product!.Name + (v.Colour != null ? " " + v.Colour : "") + (v.Size != null ? " " + v.Size : "") + " (" + v.Barcode + ")").FirstOrDefaultAsync()
                    ?? $"item #{g.Key}";
                throw new BusinessException($"Not enough stock for {name}: {have:0.##} available, {need:0.##} needed.");
            }
        }

        db.Sales.Add(sale);
        await db.SaveChangesAsync();

        foreach (var l in lines)
        {
            db.StockMovements.Add(new StockMovement
            {
                StockItemId = l.StockItemId,
                MovementDate = sale.SaleDate,
                Type = StockMovementType.Sale,
                QtyOut = l.Qty,
                RefId = sale.Id,
            });
        }

        // Audit: a big discount = rate cuts + invoice discount ≥ Audit.BigDiscountPercent of the list price.
        var listValue = lines.Sum(l => Math.Round(l.Qty * products[l.StockItemId].SalePrice, 2));
        if (listValue > 0 && listValue - sale.Total >= listValue * Audit.BigDiscountPercent / 100)
        {
            var below = lines.Where(l => l.Rate < products[l.StockItemId].SalePrice)
                .Select(l => $"{products[l.StockItemId].Name} {l.Rate:N2} (price {products[l.StockItemId].SalePrice:N2})").ToList();
            Audit.Add(db, user, Audit.BigDiscount,
                $"Invoice #{sale.Id}: list {listValue:N2} → total {sale.Total:N2} ({(listValue - sale.Total) / listValue:P0} off)" +
                (sale.Discount > 0 ? $"; invoice discount {sale.Discount:N2}" : "") +
                (below.Count > 0 ? "; below price: " + string.Join(", ", below) : ""), sale.Id);
        }

        await db.SaveChangesAsync();
        return sale.Id;
    }

    public async Task<Sale?> GetAsync(int saleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Sales.AsNoTracking()
            .Include(s => s.Customer)
            .Include(s => s.Lines.OrderBy(l => l.Id)).ThenInclude(l => l.StockItem).ThenInclude(v => v!.Product).ThenInclude(p => p!.Company)
            .Include(s => s.ExchangeReturns.OrderBy(r => r.Id)).ThenInclude(r => r.Lines.OrderBy(l => l.Id))
                .ThenInclude(l => l.StockItem).ThenInclude(v => v!.Product)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == saleId);
    }

    /// <summary>
    /// Customer balance just before this invoice ("previous balance" on a khata-style invoice):
    /// opening balance + earlier invoices' unpaid parts − payments received before it − earlier returns' credit
    /// (return Total − Refunded). Earlier = earlier date, or same date and a lower invoice number / entered before the invoice.
    /// </summary>
    public async Task<decimal> GetBalanceBeforeAsync(Sale sale)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var customerId = sale.CustomerId;
        var date = sale.SaleDate.Date;

        var opening = await db.Customers.Where(c => c.Id == customerId).Select(c => c.OpeningBalance).FirstOrDefaultAsync();
        var invoices = await db.Sales
            .Where(s => s.CustomerId == customerId && (s.SaleDate < date || (s.SaleDate == date && s.Id < sale.Id)))
            .SumAsync(s => (decimal?)(s.Total - s.Paid)) ?? 0;
        var payments = await db.CustomerPayments
            .Where(p => p.CustomerId == customerId && (p.PaymentDate < date || (p.PaymentDate == date && p.CreatedAt < sale.CreatedAt)))
            .SumAsync(p => (decimal?)p.Amount) ?? 0;
        var returns = await db.SaleReturns
            .Where(r => r.CustomerId == customerId && (r.ReturnDate < date || (r.ReturnDate == date && r.CreatedAt < sale.CreatedAt)))
            .SumAsync(r => (decimal?)(r.Total - r.Refunded)) ?? 0;
        return opening + invoices - payments - returns;
    }

    /// <summary>Returns made against this invoice (newest last).</summary>
    public async Task<List<SaleReturn>> GetReturnsAsync(int saleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SaleReturns.AsNoTracking()
            .Where(r => r.SaleId == saleId)
            .OrderBy(r => r.ReturnDate).ThenBy(r => r.Id)
            .ToListAsync();
    }

    /// <summary>Deletes the invoice and its stock movements (stock goes back) in one transaction.</summary>
    public async Task DeleteAsync(int saleId)
    {
        currentUser.Demand(Permissions.SalesDelete);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var returns = await db.SaleReturns.Where(r => r.SaleId == saleId).Select(r => r.Id).ToListAsync();
        if (returns.Count > 0)
            throw new BusinessException(
                $"Invoice #{saleId} has {(returns.Count == 1 ? "a return" : $"{returns.Count} returns")} (return # {string.Join(", ", returns)}). Delete its returns first.");
        var exchange = await db.SaleReturns.Where(r => r.ExchangeSaleId == saleId).Select(r => r.Id).ToListAsync();
        if (exchange.Count > 0)
            throw new BusinessException(
                $"Invoice #{saleId} was an exchange together with return # {string.Join(", ", exchange)}. Delete that return first.");
        var info = await db.Sales.Where(s => s.Id == saleId)
            .Select(s => new { s.SaleDate, Customer = s.Customer!.Name, s.Total, s.Paid, Items = s.Lines.Count }).FirstOrDefaultAsync()
            ?? throw new BusinessException("Invoice not found. It may already have been deleted.");
        await db.StockMovements
            .Where(m => m.Type == StockMovementType.Sale && m.RefId == saleId)
            .ExecuteDeleteAsync();
        await db.Sales.Where(s => s.Id == saleId).ExecuteDeleteAsync();
        Audit.Add(db, currentUser, Audit.SaleDeleted,
            $"Invoice #{saleId} of {info.SaleDate:dd-MMM-yyyy}, {info.Customer}: {info.Items} item(s), total {info.Total:N2}, paid {info.Paid:N2}", saleId);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
