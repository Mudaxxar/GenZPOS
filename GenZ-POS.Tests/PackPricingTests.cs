using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>
/// Pepsi bought in boxes of 6 at Rs 950, retail Rs 1,250 a box (owner 2026-10-31): stock and the till use the bottle, the
/// supplier's bill stays exact (database GenZPOS_PackTest).
/// </summary>
public class PackPricingTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_PackTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Theory]
    [InlineData(950, 6, 158.33)]
    [InlineData(1250, 6, 208.33)]
    [InlineData(9000, 50, 180)]
    public void Pack_price_goes_to_the_piece_and_back(decimal pack, decimal per, decimal piece)
    {
        Assert.Equal(piece, Pricing.UnitPrice(pack, per));
        Assert.Equal(pack, Pricing.PackPrice(piece, per));
    }

    [Fact]
    public void Price_without_a_pack_is_unchanged() => Assert.Equal(158.33m, Pricing.PackPrice(158.33m, 1));

    [Fact]
    public async Task Two_boxes_put_12_bottles_in_stock_and_the_bill_stays_exact()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int itemId, supplierId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var pepsi = new Product
            {
                Company = new Company { Name = "PepsiCo" }, SubCategoryId = 1, Name = "Pepsi", Unit = "Pcs",
                StockMode = StockMode.Quantity, PurchaseUnit = "Box", PurchaseUnitQty = 6,
                StockItems = [new() { ArticleNo = "ART-P", Barcode = "PEPSI", PurchasePrice = 158.33m, SalePrice = 208.33m }],
            };
            var supplier = new Supplier { Name = "Pepsi Agency" };
            db.AddRange(pepsi, supplier);
            await db.SaveChangesAsync();
            (itemId, supplierId) = (pepsi.StockItems[0].Id, supplier.Id);
        }

        var purchases = new PurchaseService(factory, CurrentUser.ForSystem());
        var reports = new ReportService(factory);

        // What the scan screen sends for 2 boxes at 950 / 1,250.
        var id = await purchases.CreateAsync(new Purchase
        {
            SupplierId = supplierId, PurchaseDate = DateTime.Today,
            Lines = [new() { StockItemId = itemId, Qty = 12, Cost = 158.33m, SalePrice = 208.33m, Amount = 1900 }],
        });

        var saved = await purchases.GetAsync(id);
        Assert.Equal(1900m, saved!.Total);
        Assert.Equal(1900m, await reports.GetSupplierBalanceAsync(supplierId));

        var item = (await reports.GetProductLookupAsync()).Single(i => i.Id == itemId);
        Assert.Equal(12m, item.OnHand);
        Assert.Equal(158.33m, item.PurchasePrice);
        Assert.Equal(208.33m, item.SalePrice);

        // An amount rounding can't explain is ignored: qty × cost.
        var wrong = await purchases.CreateAsync(new Purchase
        {
            SupplierId = supplierId, PurchaseDate = DateTime.Today,
            Lines = [new() { StockItemId = itemId, Qty = 12, Cost = 158.33m, Amount = 5000 }],
        });
        Assert.Equal(1899.96m, (await purchases.GetAsync(wrong))!.Total);

        // A bill dated ahead counts in stock, and its date is what lets the stock card reach it.
        var ahead = DateTime.Today.AddDays(5);
        await purchases.CreateAsync(new Purchase
        {
            SupplierId = supplierId, PurchaseDate = ahead,
            Lines = [new() { StockItemId = itemId, Qty = 6, Cost = 158.33m }],
        });
        Assert.Equal(ahead, await reports.GetLastMovementDateAsync(itemId));
        var card = await reports.GetStockCardAsync(itemId, DateTime.Today, ahead);
        Assert.Equal(card.Product!.OnHand, card.Closing);
        Assert.Contains(card.Rows, r => r.Date == ahead && r.QtyIn == 6);
    }
}
