using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>
/// End-to-end backend check of a realistic shop week against a throw-away database (GenZPOS_Test):
/// opening balances → purchases → credit + walk-in sales → returns both ways → payments both ways →
/// purchase edit/delete → stock count. Every balance, ledger, stock figure and report total is compared with
/// numbers worked out by hand (see comments). Never touches the real "Blazor" database.
///
/// Connection: TEST_DB_CONNECTION env var, else named pipes to the local default instance.
/// </summary>
public class ShopWeekScenarioTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Database=GenZPOS_Test;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Full_week_keeps_stock_balances_ledgers_and_reports_consistent()
    {
        Assert.Contains("GenZPOS_Test", ConnectionString); // safety: never run against the real DB

        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }

        var system = CurrentUser.ForSystem();
        var stock = new StockService(factory, system);
        var purchases = new PurchaseService(factory, system);
        var sales = new SaleService(factory, system);
        var saleReturns = new SaleReturnService(factory, system);
        var purchaseReturns = new PurchaseReturnService(factory, system);
        var reports = new ReportService(factory);
        var today = DateTime.Today;

        // ---------- Day 1 setup ----------
        // paintId / primerId = the variant (what documents and stock point at); paintProductId = the article.
        int paintId, paintProductId, primerId, aliId, supplierId;
        await using (var db = factory.CreateDbContext())
        {
            var berger = new Company { Name = "Berger" };
            db.Companies.Add(berger);
            await db.SaveChangesAsync();

            var paint = new Product
            {
                CompanyId = berger.Id, SubCategoryId = 1, Name = "Weather Shield", PackSize = "Gallon",
                StockItems = [new() { ArticleNo = "ART-WS", Barcode = "WS-GAL", PurchasePrice = 1000, SalePrice = 1300, ReorderLevel = 2 }],
            };
            var primer = new Product
            {
                CompanyId = berger.Id, SubCategoryId = 1, Name = "Wall Primer", PackSize = "Quarter",
                StockItems = [new() { ArticleNo = "ART-WP", Barcode = "WP-QTR", PurchasePrice = 200, SalePrice = 280 }],
            };
            var ali = new Customer { Name = "Ali Painter", Type = CustomerType.Painter, OpeningBalance = 500 };     // already owes 500
            var supplier = new Supplier { Name = "Berger Distributor", OpeningBalance = 2000 };                    // we already owe 2,000
            db.AddRange(paint, primer, ali, supplier);
            await db.SaveChangesAsync();
            (paintId, paintProductId, primerId, aliId, supplierId) = (paint.StockItems[0].Id, paint.Id, primer.StockItems[0].Id, ali.Id, supplier.Id);
        }

        // ---------- Purchases ----------
        // P1: paint 10 @1000 + primer 20 @200 = 14,000, paid 4,000 → we owe 2,000 + 10,000 = 12,000
        var p1 = await purchases.CreateAsync(new Purchase
        {
            PurchaseDate = today, SupplierId = supplierId, BillNo = "B-1", Paid = 4000,
            Lines = [new() { StockItemId = paintId, Qty = 10, Cost = 1000 }, new() { StockItemId = primerId, Qty = 20, Cost = 200 }],
        });
        // P2: paint 5 @1100 = 5,500 unpaid → we owe 17,500; paint's last cost becomes 1,100
        var p2 = await purchases.CreateAsync(new Purchase
        {
            PurchaseDate = today, SupplierId = supplierId, BillNo = "B-2", Paid = 0,
            Lines = [new() { StockItemId = paintId, Qty = 5, Cost = 1100 }],
        });
        Assert.Equal(17500m, await reports.GetSupplierBalanceAsync(supplierId));
        Assert.Equal(1100m, await PurchasePrice(factory, paintId));

        // ---------- Sales ----------
        // Business rules first: overselling and an unpaid walk-in sale are refused.
        await Assert.ThrowsAsync<BusinessException>(() => sales.CreateAsync(new Sale
        {
            CustomerId = aliId, SaleDate = today, Lines = [new() { StockItemId = paintId, Qty = 100, Rate = 1300 }],
        }));
        await Assert.ThrowsAsync<BusinessException>(() => sales.CreateAsync(new Sale
        {
            CustomerId = Customer.WalkInId, SaleDate = today, Paid = 100, Lines = [new() { StockItemId = primerId, Qty = 1, Rate = 280 }],
        }));
        await Assert.ThrowsAsync<BusinessException>(() => sales.CreateAsync(new Sale
        {
            CustomerId = aliId, SaleDate = today, Paid = 9999, Lines = [new() { StockItemId = primerId, Qty = 1, Rate = 280 }],
        }));

        // S1 credit to Ali: paint 3 @1300 + primer 5 @280 = 5,300 − discount 530 (10%) = 4,770, paid 1,000 → Ali owes 500 + 3,770 = 4,270
        var s1 = await sales.CreateAsync(new Sale
        {
            CustomerId = aliId, SaleDate = today, Discount = 530, Paid = 1000,
            Lines = [new() { StockItemId = paintId, Qty = 3, Rate = 1300 }, new() { StockItemId = primerId, Qty = 5, Rate = 280 }],
        });
        // S2 walk-in: primer 2 @280 = 560 paid in full
        var s2 = await sales.CreateAsync(new Sale
        {
            CustomerId = Customer.WalkInId, SaleDate = today, Paid = 560,
            Lines = [new() { StockItemId = primerId, Qty = 2, Rate = 280 }],
        });
        Assert.Equal(4270m, await reports.GetCustomerBalanceAsync(aliId));

        // Cost captured at sale time (paint was 1,100 then), not later changes.
        await using (var db = factory.CreateDbContext())
        {
            var costs = await db.SaleLines.Where(l => l.SaleId == s1).ToDictionaryAsync(l => l.StockItemId, l => l.Cost);
            Assert.Equal(1100m, costs[paintId]);
            Assert.Equal(200m, costs[primerId]);
        }

        // ---------- Sales return ----------
        // Ali returns 1 paint @1300 less the 10% invoice discount = 1,170; 300 cash refunded, 870 credited → Ali owes 3,400; paint +1
        await Assert.ThrowsAsync<BusinessException>(() => saleReturns.CreateAsync(new SaleReturn
        {
            SaleId = s1, ReturnDate = today, Lines = [new() { StockItemId = paintId, Qty = 4, Rate = 1300 }], // only 3 sold
        }));
        var r1 = await saleReturns.CreateAsync(new SaleReturn
        {
            SaleId = s1, ReturnDate = today, Refunded = 300,
            Lines = [new() { StockItemId = paintId, Qty = 1, Rate = 1300 }],
        });
        Assert.Equal(3400m, await reports.GetCustomerBalanceAsync(aliId));
        await Assert.ThrowsAsync<BusinessException>(() => sales.DeleteAsync(s1)); // has a return

        // ---------- Customer payment ----------
        await using (var db = factory.CreateDbContext())
        {
            db.CustomerPayments.Add(new CustomerPayment { CustomerId = aliId, PaymentDate = today, Amount = 1500, Method = PaymentMethod.Cash });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1900m, await reports.GetCustomerBalanceAsync(aliId)); // 3,400 − 1,500

        // ---------- Purchase return ----------
        // 5 primer back to supplier @200 = 1,000, nothing received → we owe 16,500; primer stock −5
        var pr1 = await purchaseReturns.CreateAsync(new PurchaseReturn
        {
            PurchaseId = p1, ReturnDate = today, Received = 0,
            Lines = [new() { StockItemId = primerId, Qty = 5 }],
        });
        Assert.Equal(16500m, await reports.GetSupplierBalanceAsync(supplierId));

        // ---------- Supplier payment ----------
        await using (var db = factory.CreateDbContext())
        {
            db.SupplierPayments.Add(new SupplierPayment { SupplierId = supplierId, PaymentDate = today, Amount = 6500, Method = PaymentMethod.Cash });
            await db.SaveChangesAsync();
        }
        Assert.Equal(10000m, await reports.GetSupplierBalanceAsync(supplierId));

        // ---------- Edit + delete a purchase ----------
        // Paint on hand = 10 + 5 − 3 + 1 = 13, so P2 (5) can be reduced to 2 → bill 2,200 → we owe 10,000 − 3,300 = 6,700
        await purchases.UpdateAsync(new Purchase
        {
            Id = p2, PurchaseDate = today, SupplierId = supplierId, BillNo = "B-2", Paid = 0,
            Lines = [new() { StockItemId = paintId, Qty = 2, Cost = 1100 }],
        });
        Assert.Equal(6700m, await reports.GetSupplierBalanceAsync(supplierId));
        Assert.Equal(10m, await stock.GetOnHandAsync(paintId));

        // Delete P2 → paint's purchase price goes back to the latest remaining bill (P1 @1000); we owe 4,500; paint 8
        await purchases.DeleteAsync(p2);
        Assert.Equal(1000m, await PurchasePrice(factory, paintId));
        Assert.Equal(4500m, await reports.GetSupplierBalanceAsync(supplierId));
        Assert.Equal(8m, await stock.GetOnHandAsync(paintId));
        await Assert.ThrowsAsync<BusinessException>(() => purchases.DeleteAsync(p1)); // sold from + has a return

        // ---------- Stock count ----------
        // Primer on hand = 20 − 5 − 2 − 5 = 8; counted 7 → one AdjustOut of 1
        Assert.Equal(8m, await stock.GetOnHandAsync(primerId));
        Assert.Equal(-1m, await stock.CountAsync(primerId, today, 7, "shelf count"));
        Assert.Equal(7m, await stock.GetOnHandAsync(primerId));
        Assert.Equal(0m, await stock.CountAsync(primerId, today, 7, null)); // nothing to change

        // ---------- Ledgers ----------
        // Ali: 500 + 4,770 − 1,000 − 1,170 + 300 − 1,500 = 1,900
        var aliLedger = await reports.GetCustomerLedgerAsync(aliId, today.AddDays(-30), today);
        Assert.Equal(500m, aliLedger.Opening);
        Assert.Equal(1900m, aliLedger.Closing);
        Assert.Equal(5, aliLedger.Rows.Count); // invoice, paid at counter, return, refund, payment

        // Supplier: 2,000 + 14,000 − 4,000 − 1,000 (return) − 6,500 = 4,500 (P2 deleted)
        var supLedger = await reports.GetSupplierLedgerAsync(supplierId, today.AddDays(-30), today);
        Assert.Equal(2000m, supLedger.Opening);
        Assert.Equal(4500m, supLedger.Closing);

        // ---------- Reports ----------
        // Sales 4,770 + 560 = 5,330; returns 1,170 → net 4,160
        // Cost: S1 paint 3×1100 + primer 5×200 + S2 primer 2×200 = 4,700; return 1×1100 → COGS 3,600; profit 560
        var profit = (await reports.GetSalesProfitAsync(today, today, byMonth: false)).Single();
        Assert.Equal(5330m, profit.SalesTotal);
        Assert.Equal(1170m, profit.Returns);
        Assert.Equal(4160m, profit.NetSales);
        Assert.Equal(3600m, profit.Cost);
        Assert.Equal(560m, profit.Profit);

        // Item sales spread the invoice discount, so they must add up to the same net sales and cost.
        var items = await reports.GetItemSalesAsync(today, today, ItemSalesGroup.Product, null, null);
        Assert.Equal(4160m, Math.Round(items.Sum(i => i.NetSales), 0));
        Assert.Equal(3600m, items.Sum(i => i.Cost));
        Assert.Equal(2m, items.Single(i => i.Key == paintProductId).NetQty); // 3 sold − 1 returned

        // Day book (all cash): in = 1,000 + 560 + 1,500 = 3,060; out = 4,000 (P1) + 6,500 + 300 refund = 10,800
        var dayBook = await reports.GetDayBookAsync(today, today);
        Assert.Equal(3060m, dayBook.CashIn);
        Assert.Equal(10800m, dayBook.CashOut);
        Assert.Equal(-7740m, dayBook.NetCash);

        // Dashboard agrees with the reports.
        var dash = await reports.GetDashboardAsync();
        Assert.Equal(4160m, dash.SalesToday);
        Assert.Equal(-7740m, dash.CashToday);
        Assert.Equal(1900m, dash.Receivable);
        Assert.Equal(4500m, dash.Payable);
        Assert.Equal(560m, dash.GrossProfitThisMonth);
        // Sales returns summary: r1 = 1,170 (300 refunded) of 5,330 invoiced → 22.0%
        Assert.Equal(1170m, dash.ReturnsToday);
        Assert.Equal(1, dash.ReturnsTodayCount);
        Assert.Equal(1170m, dash.ReturnsThisMonth);
        Assert.Equal(1, dash.ReturnsThisMonthCount);
        Assert.Equal(300m, dash.RefundedThisMonth);
        Assert.Equal(22.0m, dash.ReturnRateThisMonth);

        // Stock card explains on-hand: paint 10 in − 3 out + 1 return in + (P2: 2 in, then deleted) = 8
        var card = await reports.GetStockCardAsync(paintId, today.AddDays(-30), today);
        Assert.Equal(0m, card.Opening);
        Assert.Equal(8m, card.Closing);

        // ---------- Deleting returns puts everything back ----------
        await saleReturns.DeleteAsync(r1);
        Assert.Equal(2770m, await reports.GetCustomerBalanceAsync(aliId)); // 1,900 + 870 credit removed
        Assert.Equal(7m, await stock.GetOnHandAsync(paintId));
        await purchaseReturns.DeleteAsync(pr1);
        Assert.Equal(5500m, await reports.GetSupplierBalanceAsync(supplierId)); // 4,500 + 1,000
        Assert.Equal(12m, await stock.GetOnHandAsync(primerId));

        // Now the invoice has no returns, so it can be deleted: stock back, balance back.
        await sales.DeleteAsync(s2);
        Assert.Equal(14m, await stock.GetOnHandAsync(primerId));
    }

    private static async Task<decimal> PurchasePrice(IDbContextFactory<AppDbContext> f, int variantId)
    {
        await using var db = f.CreateDbContext();
        return await db.StockItems.Where(v => v.Id == variantId).Select(v => v.PurchasePrice).SingleAsync();
    }
}
