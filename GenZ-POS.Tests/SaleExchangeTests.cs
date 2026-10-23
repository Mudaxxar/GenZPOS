using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>
/// Exchange on the New Sale screen (owner 2026-10-23): return + new invoice in one go. Checks balances, cash (Day Book),
/// stock and the links, for paying the difference, getting money back, and a credit customer (database GenZPOS_ExchangeTest).
/// </summary>
public class SaleExchangeTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_ExchangeTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Exchange_pay_difference_get_money_back_and_credit_customer()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int a, b, c, d, e, aliId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var shoes = new Product
            {
                Company = new Company { Name = "Zee Shoes" }, SubCategoryId = 1, Name = "Shoes",
                StockItems =
                [
                    new() { ArticleNo = "ART1", Barcode = "SH-A", Colour = "Black", Size = "42", PurchasePrice = 2500, SalePrice = 4250 },
                    new() { ArticleNo = "ART1", Barcode = "SH-B", Colour = "Black", Size = "43", PurchasePrice = 3000, SalePrice = 5000 },
                    new() { ArticleNo = "ART2", Barcode = "SH-C", Colour = "Blue", Size = "40", PurchasePrice = 1200, SalePrice = 2000 },
                    new() { ArticleNo = "ART2", Barcode = "SH-D", Colour = "Blue", Size = "41", PurchasePrice = 1800, SalePrice = 3000 },
                    new() { ArticleNo = "ART2", Barcode = "SH-E", Colour = "Blue", Size = "42", PurchasePrice = 1500, SalePrice = 2500 },
                ],
            };
            var ali = new Customer { Name = "Ali" };
            db.AddRange(shoes, ali);
            await db.SaveChangesAsync();
            (a, b, c, d, e) = (shoes.StockItems[0].Id, shoes.StockItems[1].Id, shoes.StockItems[2].Id, shoes.StockItems[3].Id, shoes.StockItems[4].Id);
            aliId = ali.Id;
            foreach (var item in shoes.StockItems)
                db.StockMovements.Add(new StockMovement { StockItemId = item.Id, MovementDate = DateTime.Today, Type = StockMovementType.Opening, QtyIn = 1 });
            await db.SaveChangesAsync();
        }

        var system = CurrentUser.ForSystem();
        var sales = new SaleService(factory, system);
        var returns = new SaleReturnService(factory, system);
        var stock = new StockService(factory, system);
        var reports = new ReportService(factory);
        var today = DateTime.Today;

        // Walk-in buys A at 4,250 with 20% off → 3,400 paid.
        var s1 = await sales.CreateAsync(new Sale
        {
            CustomerId = Customer.WalkInId, SaleDate = today, Discount = 850, Paid = 3400,
            Lines = [new() { StockItemId = a, Qty = 1, Rate = 4250 }],
        });

        // The till finds A by its barcode: invoice #s1, refund 3,400 (after the discount).
        var found = await returns.FindForExchangeAsync("SH-A");
        var candidate = Assert.Single(found);
        Assert.Equal(s1, candidate.SaleId);
        Assert.Equal(3400m, candidate.Line.AmountFor(1));
        Assert.Single(await returns.FindForExchangeAsync($"#{s1}"));

        // ---------- 1. Exchange A (3,400) for B (5,000): customer pays 1,600 ----------
        SaleReturn ReturnOf(int saleId, int item, decimal rate) =>
            new() { SaleId = saleId, Lines = [new() { StockItemId = item, Qty = 1, Rate = rate }] };

        // Walk-in must pay the whole difference.
        await Assert.ThrowsAsync<BusinessException>(() => sales.CreateExchangeAsync(
            new Sale { CustomerId = Customer.WalkInId, SaleDate = today, Lines = [new() { StockItemId = b, Qty = 1, Rate = 5000 }] },
            [ReturnOf(s1, a, 4250)], 1000));
        var s2 = await sales.CreateExchangeAsync(
            new Sale { CustomerId = Customer.WalkInId, SaleDate = today, Lines = [new() { StockItemId = b, Qty = 1, Rate = 5000 }] },
            [ReturnOf(s1, a, 4250)], 1600);

        var x2 = await sales.GetAsync(s2);
        Assert.NotNull(x2);
        Assert.Equal(5000m, x2!.Total);
        Assert.Equal(5000m, x2.Paid);                       // 3,400 from the return + 1,600 cash
        var r2 = Assert.Single(x2.ExchangeReturns);
        Assert.Equal(s1, r2.SaleId);
        Assert.Equal(3400m, r2.Total);
        Assert.Equal(3400m, r2.Refunded);                   // all of it set against the new invoice
        Assert.Equal(1m, await stock.GetOnHandAsync(a));    // back in stock
        Assert.Equal(0m, await stock.GetOnHandAsync(b));
        Assert.Empty(await returns.FindForExchangeAsync("SH-A")); // already returned

        // ---------- 2. Exchange B (5,000) for C (2,000): customer gets 3,000 back ----------
        await Assert.ThrowsAsync<BusinessException>(() => sales.CreateExchangeAsync(
            new Sale { CustomerId = Customer.WalkInId, SaleDate = today, Lines = [new() { StockItemId = c, Qty = 1, Rate = 2000 }] },
            [ReturnOf(s2, b, 5000)], -3500)); // more than the difference
        var s3 = await sales.CreateExchangeAsync(
            new Sale { CustomerId = Customer.WalkInId, SaleDate = today, Lines = [new() { StockItemId = c, Qty = 1, Rate = 2000 }] },
            [ReturnOf(s2, b, 5000)], -3000);
        var x3 = await sales.GetAsync(s3);
        Assert.Equal(2000m, x3!.Paid);
        Assert.Equal(5000m, Assert.Single(x3.ExchangeReturns).Refunded); // 2,000 set off + 3,000 cash back


        // Cash in the till: 3,400 + 1,600 − 3,000 = 2,000 (= what the customer finally paid for C).
        var dayBook = await reports.GetDayBookAsync(today, today);
        Assert.Equal(2000m, dayBook.NetCash);
        // Only cash that changed hands: in 3,400 (s1) + 1,600 (difference on s2); out 3,000 (given back on s3).
        Assert.Equal(5000m, dayBook.CashIn);
        Assert.Equal(3000m, dayBook.CashOut);
        var dash = await reports.GetDashboardAsync();
        Assert.Equal(5000m, dash.CashInToday);
        Assert.Equal(3000m, dash.CashOutToday);
        Assert.Equal(3000m, dash.RefundedThisMonth);
        Assert.Equal(0m, await reports.GetCustomerBalanceAsync(Customer.WalkInId));

        // ---------- 3. Credit customer: D (3,000, unpaid) for E (2,500), nothing paid → owes 2,500 ----------
        var s4 = await sales.CreateAsync(new Sale
        {
            CustomerId = aliId, SaleDate = today, Lines = [new() { StockItemId = d, Qty = 1, Rate = 3000 }],
        });
        Assert.Equal(3000m, await reports.GetCustomerBalanceAsync(aliId));
        // An exchange is for the invoice's own customer.
        await Assert.ThrowsAsync<BusinessException>(() => sales.CreateExchangeAsync(
            new Sale { CustomerId = Customer.WalkInId, SaleDate = today, Lines = [new() { StockItemId = e, Qty = 1, Rate = 2500 }] },
            [ReturnOf(s4, d, 3000)], -500));
        var s5 = await sales.CreateExchangeAsync(
            new Sale { CustomerId = aliId, SaleDate = today, Lines = [new() { StockItemId = e, Qty = 1, Rate = 2500 }] },
            [ReturnOf(s4, d, 3000)], 0);
        Assert.Equal(2500m, await reports.GetCustomerBalanceAsync(aliId)); // the 500 difference stays as credit

        // ---------- the exchange invoice can't be deleted while its return exists ----------
        await Assert.ThrowsAsync<BusinessException>(() => sales.DeleteAsync(s5));
        var r5 = Assert.Single((await sales.GetAsync(s5))!.ExchangeReturns);
        await returns.DeleteAsync(r5.Id);
        await sales.DeleteAsync(s5);
        Assert.Equal(3000m, await reports.GetCustomerBalanceAsync(aliId));
        Assert.Equal(1m, await stock.GetOnHandAsync(e));
        Assert.Equal(0m, await stock.GetOnHandAsync(d));
    }
}
