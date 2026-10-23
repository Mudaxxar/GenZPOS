using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>Per-product maximum discount at the till (DiscountRules + SaleService), database GenZPOS_DiscountTest.</summary>
public class MaxDiscountTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_DiscountTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public void Rule_math()
    {
        Assert.Null(DiscountRules.MinRate(1000, null));            // no limit
        Assert.Null(DiscountRules.MinRate(0, 10));                 // no sale price to measure from
        Assert.Equal(900, DiscountRules.MinRate(1000, 10));
        Assert.Equal(1000, DiscountRules.MinRate(1000, 0));        // 0% = no discount
        Assert.Equal(200, DiscountRules.Room(2, 1000, 1000, 10));  // 2 × 100 room at list price
        Assert.Equal(100, DiscountRules.Room(2, 950, 1000, 10));   // rate cut already used half
        Assert.Equal(0, DiscountRules.Room(2, 850, 1000, 10));     // below the floor: no room
        Assert.Equal(500, DiscountRules.Room(1, 500, 500, null));  // no limit: whole amount
    }

    [Fact]
    public void Discount_in_rupees_or_percent()
    {
        Assert.Equal(50, DiscountRules.Amount(1000, 50, isPercent: false));      // Rs as typed
        Assert.Equal(50, DiscountRules.Amount(1000, 5, isPercent: true));        // 5% of 1,000
        Assert.Equal(33.33m, DiscountRules.Amount(333.30m, 10, isPercent: true)); // rounded to paisa
        Assert.Equal(1000, DiscountRules.Amount(1000, 150, isPercent: true));    // never more than 100%
        Assert.Equal(5, DiscountRules.ToPercent(1000, 50));
        Assert.Equal(0, DiscountRules.ToPercent(0, 50));                          // empty cart

        // "use max" in % mode: highest % that stays within the rupee limit, never a paisa over.
        Assert.Equal(10, DiscountRules.MaxPercent(1000, 100));
        var pct = DiscountRules.MaxPercent(2999, 200);                            // 6.669…% → 6.66%
        Assert.Equal(6.66m, pct);
        Assert.True(DiscountRules.Amount(2999, pct, isPercent: true) <= 200);
        for (var sub = 1m; sub < 3000; sub += 7.37m)                              // any sub total / limit pair
        {
            var max = Math.Round(sub * 0.137m, 2);
            Assert.True(DiscountRules.Amount(sub, DiscountRules.MaxPercent(sub, max), isPercent: true) <= max);
        }
    }

    [Fact]
    public async Task Till_enforces_maximum_discount()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int paintId, paintProductId, brushId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var berger = new Company { Name = "Berger" };
            var paint = new Product
            {
                Company = berger, SubCategoryId = 1, Name = "Silk", MaxDiscountPercent = 10,
                StockItems = [new() { ArticleNo = "ART-SILK", Barcode = "SILK", SalePrice = 1000, PurchasePrice = 800 }],
            };
            var brush = new Product // no limit
            {
                Company = berger, SubCategoryId = 16, Name = "Brush", 
                StockItems = [new() { ArticleNo = "ART-BRUSH", Size = "2 inch", Barcode = "BR2", SalePrice = 200, PurchasePrice = 120 }],
            };
            db.Products.AddRange(paint, brush);
            await db.SaveChangesAsync();
            paintId = paint.StockItems[0].Id;
            paintProductId = paint.Id;
            brushId = brush.StockItems[0].Id;
            db.StockMovements.AddRange(
                new StockMovement { StockItemId = paintId, MovementDate = DateTime.Today, Type = StockMovementType.Opening, QtyIn = 100 },
                new StockMovement { StockItemId = brushId, MovementDate = DateTime.Today, Type = StockMovementType.Opening, QtyIn = 100 });
            await db.SaveChangesAsync();
        }

        var sales = new SaleService(factory, CurrentUser.ForSystem());
        Sale NewSale(decimal discount, params (int Product, decimal Qty, decimal Rate)[] lines) => new()
        {
            CustomerId = Customer.WalkInId, SaleDate = DateTime.Today, Discount = discount,
            Lines = lines.Select(l => new SaleLine { StockItemId = l.Product, Qty = l.Qty, Rate = l.Rate }).ToList(),
            Paid = lines.Sum(l => l.Qty * l.Rate) - discount,
        };

        // Rate below the 10% floor (900) is refused.
        var rate = await Assert.ThrowsAsync<BusinessException>(() => sales.CreateAsync(NewSale(0, (paintId, 1, 890))));
        Assert.Contains("can't be below 900.00", rate.Message);

        // Invoice discount over the items' room: 2 × 1000 at 10% = 200 room.
        var disc = await Assert.ThrowsAsync<BusinessException>(() => sales.CreateAsync(NewSale(201, (paintId, 2, 1000))));
        Assert.Contains("at most 200.00", disc.Message);

        // Exactly at the limit is fine: rate 950 (50 used) + 100 invoice discount on 2 tins = 900 each.
        await sales.CreateAsync(NewSale(100, (paintId, 2, 950)));

        // An item without a limit adds its whole amount to the room.
        await sales.CreateAsync(NewSale(300, (paintId, 1, 1000), (brushId, 1, 200)));
        await Assert.ThrowsAsync<BusinessException>(() => sales.CreateAsync(NewSale(301, (paintId, 1, 1000), (brushId, 1, 200))));

        // 0% = no discount at all on that item.
        await using (var db = factory.CreateDbContext())
            await db.Products.Where(p => p.Id == paintProductId).ExecuteUpdateAsync(s => s.SetProperty(p => p.MaxDiscountPercent, 0m));
        await Assert.ThrowsAsync<BusinessException>(() => sales.CreateAsync(NewSale(1, (paintId, 1, 1000))));
        await sales.CreateAsync(NewSale(0, (paintId, 1, 1000)));
    }
}
