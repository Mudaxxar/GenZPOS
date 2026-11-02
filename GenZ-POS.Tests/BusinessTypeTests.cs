using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>
/// Business type (owner 2026-10-28): Fashion (default) or Retail &amp; grocery, saved in Settings; it decides how new
/// products are stocked. A shop that had "new products are stocked by Quantity" before the setting existed counts as
/// Retail (database GenZPOS_BusinessTypeTest).
/// </summary>
public class BusinessTypeTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_BusinessTypeTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Business_type_is_saved_and_old_stock_mode_setting_still_counts()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }

        var shop = new ShopInfo(factory);
        await shop.LoadAsync();
        Assert.Equal(BusinessType.Fashion, shop.BusinessType);
        Assert.Equal(StockMode.Piece, shop.DefaultStockMode);

        // Before Business type: the old "new products are stocked by" setting.
        await using (var db = factory.CreateDbContext())
        {
            db.Settings.Add(new AppSetting { Key = AppSetting.DefaultStockMode, Value = nameof(StockMode.Quantity) });
            await db.SaveChangesAsync();
        }
        await shop.LoadAsync();
        Assert.True(shop.IsRetail);
        Assert.Equal(StockMode.Quantity, shop.DefaultStockMode);

        // Business type wins once it is saved.
        await shop.SaveBusinessTypeAsync(BusinessType.Fashion);
        var reloaded = new ShopInfo(factory);
        await reloaded.LoadAsync();
        Assert.Equal(BusinessType.Fashion, reloaded.BusinessType);
        Assert.Equal(StockMode.Piece, reloaded.DefaultStockMode);

        await reloaded.SaveBusinessTypeAsync(BusinessType.Retail);
        await shop.LoadAsync();
        Assert.True(shop.IsRetail);

        // New Purchase: the business type's screen, until another is chosen; a new business type starts over.
        Assert.Equal("purchases/scan", shop.NewPurchasePath);
        await shop.SavePurchaseScreenAsync(PurchaseScreen.Articles);
        await reloaded.LoadAsync();
        Assert.Equal("purchases/new", reloaded.NewPurchasePath);
        Assert.Equal("purchases/5/edit", reloaded.EditPurchasePath(5));
        await reloaded.SaveBusinessTypeAsync(BusinessType.Retail);
        Assert.Equal("purchases/scan", reloaded.NewPurchasePath);
    }
}
