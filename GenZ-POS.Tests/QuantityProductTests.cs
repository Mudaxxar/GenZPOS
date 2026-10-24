using System.IO;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Components;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>
/// Quantity products (owner 2026-10-24): rice bought in 50 kg bags and sold by the kg, again and again, on one stock
/// item — purchase, sales of ¼ / 1½ kg, a part return, stock, balances, profit, Excel stock import
/// (database GenZPOS_QuantityTest).
/// </summary>
public class QuantityProductTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_QuantityTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Rice_bought_in_bags_and_sold_by_the_kg()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int riceId, supplierId, aliId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var rice = new Product
            {
                Company = new Company { Name = "Guard" }, SubCategoryId = 1, Name = "Basmati Rice", Unit = "Kg",
                StockMode = StockMode.Quantity, PurchaseUnit = "Bag", PurchaseUnitQty = 50,
                StockItems = [new() { ArticleNo = "ART-R", Barcode = "RICE", PurchasePrice = 180, SalePrice = 220 }],
            };
            var supplier = new Supplier { Name = "Rice Mill" };
            var ali = new Customer { Name = "Ali" };
            db.AddRange(rice, supplier, ali);
            await db.SaveChangesAsync();
            (riceId, supplierId, aliId) = (rice.StockItems[0].Id, supplier.Id, ali.Id);
        }

        var system = CurrentUser.ForSystem();
        var purchases = new PurchaseService(factory, system);
        var sales = new SaleService(factory, system);
        var saleReturns = new SaleReturnService(factory, system);
        var stock = new StockService(factory, system);
        var reports = new ReportService(factory);
        var today = DateTime.Today;

        // 2 bags × 9,000 = 100 kg at 180 / kg, then 1 more bag at 9,500 (190 / kg): the same stock item twice.
        await purchases.CreateAsync(new Purchase
        {
            SupplierId = supplierId, PurchaseDate = today, Paid = 18000,
            Lines = [new() { StockItemId = riceId, Qty = 100, Cost = 180 }],
        });
        await purchases.CreateAsync(new Purchase
        {
            SupplierId = supplierId, PurchaseDate = today, Paid = 0,
            Lines = [new() { StockItemId = riceId, Qty = 50, Cost = 190 }],
        });
        Assert.Equal(150m, await stock.GetOnHandAsync(riceId));
        Assert.Equal(9500m, await reports.GetSupplierBalanceAsync(supplierId));

        // Walk-in: ¼ kg at 220 = 55. Ali: 1.5 kg + 0.125 kg (3 decimals) at 220 = 357.50, on credit.
        await sales.CreateAsync(new Sale
        {
            CustomerId = Customer.WalkInId, SaleDate = today, Paid = 55,
            Lines = [new() { StockItemId = riceId, Qty = 0.25m, Rate = 220 }],
        });
        var s2 = await sales.CreateAsync(new Sale
        {
            CustomerId = aliId, SaleDate = today, Paid = 0,
            Lines = [new() { StockItemId = riceId, Qty = 1.5m, Rate = 220 }, new() { StockItemId = riceId, Qty = 0.125m, Rate = 220 }],
        });
        Assert.Equal(148.125m, await stock.GetOnHandAsync(riceId));
        Assert.Equal(357.50m, await reports.GetCustomerBalanceAsync(aliId));

        // Selling more than is left is refused.
        await Assert.ThrowsAsync<BusinessException>(() => sales.CreateAsync(new Sale
        {
            CustomerId = aliId, SaleDate = today, Lines = [new() { StockItemId = riceId, Qty = 200, Rate = 220 }],
        }));

        // Ali brings back ½ kg of the 1.625 kg: 110 credited, stock +0.5.
        var returnable = await saleReturns.GetReturnableAsync(s2);
        Assert.Equal(1.625m, returnable.Sum(l => l.Returnable));
        await saleReturns.CreateAsync(new SaleReturn
        {
            SaleId = s2, ReturnDate = today, Lines = [new() { StockItemId = riceId, Qty = 0.5m, Rate = 220 }],
        });
        Assert.Equal(148.625m, await stock.GetOnHandAsync(riceId));
        Assert.Equal(247.50m, await reports.GetCustomerBalanceAsync(aliId));

        // Net sales 55 + 357.50 − 110 = 302.50; the cost of each sale line is the stock item's cost then (190 after the
        // second bag): 1.875 kg − 0.5 kg = 1.375 kg × 190 = 261.25.
        var profit = (await reports.GetSalesProfitAsync(today, today, byMonth: false)).Single();
        Assert.Equal(302.50m, profit.NetSales);
        Assert.Equal(261.25m, profit.Cost);

        // Receipts / invoices print the unit.
        Assert.Equal("0.25 Kg", Fmt.QtyUnit(0.25m, "Kg"));
        Assert.Equal("1", Fmt.QtyUnit(1, "Pcs"));

        // ---------- Excel stock import: rice by barcode, Qty 50 → bought again (never skipped like a piece) ----------
        var excel = new ProductExcelService(factory, system);
        string[] headers = ["Company", "Category", "Sub-category", "Product", "Unit", "Barcode", "Purchase price", "Sale price", "Qty"];
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Products");
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        object[] values = ["Guard", "Emulsion", "Interior Emulsion", "Basmati Rice", "Kg", "RICE", 185, 225, 50];
        for (var c = 0; c < values.Length; c++)
            ws.Cell(2, c + 1).Value = values[c] switch { string s => s, int i => i, _ => throw new InvalidOperationException() };
        // A new quantity product in the same file ("Stocked by" column): sugar 20.5 kg.
        ws.Cell(1, 10).Value = "Stocked by";
        object[] sugar = ["Local", "Emulsion", "Interior Emulsion", "Sugar", "Kg", "", 140, 160, 20.5];
        for (var c = 0; c < sugar.Length; c++)
            ws.Cell(3, c + 1).Value = sugar[c] switch { string s => s, int i => i, double d => d, _ => throw new InvalidOperationException() };
        ws.Cell(3, 6).Value = Blank.Value;
        ws.Cell(3, 10).Value = "Quantity";

        using var saved = new MemoryStream();
        wb.SaveAs(saved);
        var file = saved.ToArray();
        for (var round = 1; round <= 2; round++) // twice: rice is bought again both times
        {
            using var ms = new MemoryStream(file);
            var plan = await excel.PreviewAsync(ms, createMissing: true, forPurchase: true);
            Assert.Empty(plan.Errors);
            Assert.Empty(plan.OpeningSkipped);
            Assert.Equal(70.5m, plan.OpeningTotal);
            var id = await excel.ImportPurchaseAsync(plan, new Purchase { SupplierId = supplierId, PurchaseDate = today });
            await using var db = factory.CreateDbContext();
            var bill = await db.Purchases.Include(p => p.Lines).SingleAsync(p => p.Id == id);
            Assert.Equal(2, bill.Lines.Count);
            Assert.Equal(50m, bill.Lines.Single(l => l.StockItemId == riceId).Qty);
            // Sugar: one stock item (created in round 1, bought again in round 2), stocked by quantity.
            var sugarItem = await db.StockItems.Include(i => i.Product).SingleAsync(i => i.Product!.Name == "Sugar");
            Assert.Equal(StockMode.Quantity, sugarItem.Product!.StockMode);
            Assert.Equal(20.5m * round, await stock.GetOnHandAsync(sugarItem.Id));
        }
        Assert.Equal(148.625m + 100, await stock.GetOnHandAsync(riceId));
    }
}
