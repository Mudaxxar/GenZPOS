using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>Cost ↔ profit % ↔ retail on the purchase screen, and the purchase panel adding sizes / colours (GenZPOS_PricingTest).</summary>
public class PricingTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_PricingTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public void Profit_percent_and_retail_price()
    {
        Assert.Equal(900, Pricing.RetailFromProfit(600, 50));          // 600 + 50%
        Assert.Equal(50, Pricing.ProfitPercent(600, 900));
        Assert.Equal(-10, Pricing.ProfitPercent(1000, 900));           // selling below cost shows negative
        Assert.Equal(733.33m, Pricing.RetailFromProfit(666.66m, 10));  // to paisa
        Assert.Null(Pricing.ProfitPercent(0, 500));                    // no cost: no %
        // Round trip: the % worked out from a retail gives that retail back.
        Assert.Equal(4250, Pricing.RetailFromProfit(2500, Pricing.ProfitPercent(2500, 4250)!.Value));
    }

    [Fact]
    public async Task Purchase_panel_adds_sizes_colours_and_purchase_sets_sale_price()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int productId, itemId, supplierId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var zee = new Company { Name = "Zee Shoes" };
            var supplier = new Supplier { Name = "Adeel" };
            // "Zee Shoes · Shoes · Black · 1".
            var product = new Product
            {
                Company = zee, SubCategoryId = 1, Name = "Shoes", 
                StockItems = [new() { ArticleNo = "ART000500", Colour = "Black", Size = "1", Barcode = "OLD-1", PurchasePrice = 600, SalePrice = 800 }],
            };
            db.AddRange(supplier, product);
            await db.SaveChangesAsync();
            (productId, itemId, supplierId) = (product.Id, product.StockItems[0].Id, supplier.Id);
        }

        var system = CurrentUser.ForSystem();
        var variants = new StockItemService(factory, system);
        var prices = new StockItemPrices(600, 900);

        // "+" panel on that item: Black 1 is the item itself; Black 2 and Blue 1 are added to its product.
        var black1 = await variants.EnsureOneAsync(productId, "ART000500", "black", "1", null, prices);
        Assert.False(black1.Created);
        Assert.Equal(itemId, black1.StockItemId);                       // the item itself, not a copy
        Assert.Equal("OLD-1", black1.Barcode);
        var black2 = await variants.EnsureOneAsync(productId, "ART000500", "Black", "2", null, prices);
        var blue1 = await variants.EnsureOneAsync(productId, "ART000500", "Blue", "1", null, prices);
        Assert.True(black2.Created && blue1.Created);
        Assert.Matches("^SH[0-9]{6}$", black2.Barcode);
        Assert.NotEqual(black2.Barcode, blue1.Barcode);

        // Purchase at cost 600 + 50% = retail 900: every line's sale price becomes 900 (the item's 800 → 900).
        await new PurchaseService(factory, system).CreateAsync(new Purchase
        {
            PurchaseDate = DateTime.Today, SupplierId = supplierId,
            Lines = new[] { itemId, black2.StockItemId, blue1.StockItemId }
                .Select(id => new PurchaseLine { StockItemId = id, Qty = 2, Cost = 600, SalePrice = Pricing.RetailFromProfit(600, 50) }).ToList(),
        });
        await using (var db = factory.CreateDbContext())
        {
            var all = await db.StockItems.Where(v => v.ProductId == productId).ToListAsync();
            Assert.Equal(3, all.Count);
            Assert.All(all, v => Assert.Equal(900, v.SalePrice));
            Assert.All(all, v => Assert.Equal(600, v.PurchasePrice));
            Assert.Equal(3, all.Select(v => v.Barcode).Distinct().Count());
            Assert.Equal(1, await db.Products.CountAsync()); // one article, three sizes / colours
        }

        // The panel adds one size / colour at a time with the barcode it showed (read-only) for a new one.
        var one = await variants.EnsureOneAsync(productId, "ART000500", "Red", "3", ProductCodes.ToBarcode(900), prices);
        Assert.True(one.Created);
        Assert.Equal(ProductCodes.ToBarcode(900), one.Barcode);
        var again = await variants.EnsureOneAsync(productId, "ART000500", "red", "3", null, prices);       // existing: same variant, its own barcode
        Assert.False(again.Created);
        Assert.Equal(one.StockItemId, again.StockItemId);
        Assert.Equal(ProductCodes.ToBarcode(900), again.Barcode);
        await Assert.ThrowsAsync<BusinessException>(() =>                                     // a taken barcode is refused
            variants.EnsureOneAsync(productId, "ART000500", "Green", "3", ProductCodes.ToBarcode(900), prices));

        // A line without a retail price (older bills) leaves the sale price alone.
        await new PurchaseService(factory, system).CreateAsync(new Purchase
        {
            PurchaseDate = DateTime.Today, SupplierId = supplierId,
            Lines = [new PurchaseLine { StockItemId = itemId, Qty = 1, Cost = 650 }],
        });
        await using (var db = factory.CreateDbContext())
        {
            var item = await db.StockItems.SingleAsync(v => v.Id == itemId);
            Assert.Equal(900, item.SalePrice);
            Assert.Equal(650, item.PurchasePrice);
        }
    }
}
