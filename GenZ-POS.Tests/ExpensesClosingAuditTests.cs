using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>
/// Owner 2026-10-24: expenses (Day Book cash, net profit, dashboard), day closing (expected / short / opening carried to
/// the next day), the audit log, held till carts and the backup copy to a second folder (database GenZPOS_OfficeTest).
/// </summary>
public class ExpensesClosingAuditTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_OfficeTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Expenses_day_closing_audit_and_held_sales()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int itemId, supplierId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var product = new Product
            {
                Company = new Company { Name = "Zee" }, SubCategoryId = 1, Name = "Shoes",
                StockItems = [new() { ArticleNo = "A1", Barcode = "B1", PurchasePrice = 1000, SalePrice = 2000 }],
            };
            var supplier = new Supplier { Name = "Mill" };
            db.AddRange(product, supplier);
            await db.SaveChangesAsync();
            (itemId, supplierId) = (product.StockItems[0].Id, supplier.Id);
        }

        var system = CurrentUser.ForSystem();
        var reports = new ReportService(factory);
        var purchases = new PurchaseService(factory, system);
        var sales = new SaleService(factory, system);
        var stock = new StockService(factory, system);
        var expenses = new ExpenseService(factory, system);
        var closings = new DayClosingService(factory, system, reports);
        var held = new HeldSaleService(factory, system);
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);

        // Yesterday: 10 pairs bought (not paid), 1 sold for 2,000 cash; closed with 2,000 counted, 1,500 taken out → 500 left.
        await purchases.CreateAsync(new Purchase { SupplierId = supplierId, PurchaseDate = yesterday, Lines = [new() { StockItemId = itemId, Qty = 10, Cost = 1000 }] });
        await sales.CreateAsync(new Sale { CustomerId = Customer.WalkInId, SaleDate = yesterday, Paid = 2000, Lines = [new() { StockItemId = itemId, Qty = 1, Rate = 2000 }] });
        var first = await closings.CloseAsync(yesterday, opening: 0, counted: 2000, takenOut: 1500, notes: null);
        Assert.Equal(2000m, first.Expected);
        Assert.Equal(0m, first.Difference);
        Assert.Equal(500m, first.LeftInDrawer);

        // Today: 2 sold (one at 1,600 = 20% off → audited as a big discount), expenses: tea 300 cash, rent 20,000 by bank.
        await sales.CreateAsync(new Sale { CustomerId = Customer.WalkInId, SaleDate = today, Paid = 2000, Lines = [new() { StockItemId = itemId, Qty = 1, Rate = 2000 }] });
        var discounted = await sales.CreateAsync(new Sale { CustomerId = Customer.WalkInId, SaleDate = today, Paid = 1600, Lines = [new() { StockItemId = itemId, Qty = 1, Rate = 1600 }] });
        var categories = await GetCategoriesAsync(factory);
        await expenses.AddAsync(new Expense { ExpenseDate = today, CategoryId = categories["Tea & food"], Amount = 300, Method = PaymentMethod.Cash, PaidTo = "Tea boy" });
        var rentId = await expenses.AddAsync(new Expense { ExpenseDate = today, CategoryId = categories["Rent"], Amount = 20000, Method = PaymentMethod.Bank });
        await Assert.ThrowsAsync<BusinessException>(() => expenses.AddAsync(new Expense { ExpenseDate = today, CategoryId = categories["Rent"], Amount = 0 }));

        // Day Book: cash in 3,600, cash out 300 (tea); rent is non-cash.
        var book = await reports.GetDayBookAsync(today, today);
        Assert.Equal(3600m, book.CashIn);
        Assert.Equal(300m, book.CashOut);
        Assert.Equal(20000m, book.NonCashOut);
        Assert.Equal(20300m, book.Totals.Expenses);

        // Profit today: net sales 3,600 − cost 2,000 = 1,600 gross; − 20,300 expenses = −18,700 net.
        var profit = (await reports.GetSalesProfitAsync(today, today, byMonth: false)).Single();
        Assert.Equal(1600m, profit.Profit);
        Assert.Equal(20300m, profit.Expenses);
        Assert.Equal(-18700m, profit.NetProfit);
        var dash = await reports.GetDashboardAsync();
        Assert.Equal(300m, dash.CashOutToday);
        Assert.Equal(dash.GrossProfitThisMonth - dash.ExpensesThisMonth, dash.NetProfitThisMonth);

        // Z-report / closing today: opening 500 (left yesterday) + 3,600 − 300 = 3,800 expected; 3,700 counted = 100 short.
        var z = await closings.GetZReportAsync(today);
        Assert.Equal(500m, z.Opening);
        Assert.Equal(yesterday, z.OpeningFrom);
        Assert.Equal(3800m, z.Expected);
        Assert.Equal(0m, z.InvoiceDiscounts); // the 400 off was a rate cut, not an invoice discount
        Assert.Contains(z.Expenses, e => e.Category == "Rent" && e.Amount == 20000 && e.Cash == 0);
        var closed = await closings.CloseAsync(today, opening: 999, counted: 3700, takenOut: 3000, notes: "change given wrong");
        Assert.Equal(500m, closed.OpeningCash); // the typed opening is ignored: it comes from yesterday
        Assert.Equal(-100m, closed.Difference);
        Assert.Equal(700m, closed.LeftInDrawer);
        // Closing again replaces it (one per day).
        await closings.CloseAsync(today, 0, 3800, 3000, null);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(2, await db.CashClosings.CountAsync());
            Assert.Equal(0m, (await db.CashClosings.SingleAsync(c => c.ClosingDate == today)).Difference);
        }
        await Assert.ThrowsAsync<BusinessException>(() => closings.CloseAsync(today, 0, 100, 200, null)); // taken out > counted

        // ---------- Audit ----------
        await stock.AdjustAsync(itemId, today, StockMovementType.AdjustOut, 1, "damaged");
        await sales.DeleteAsync(discounted);
        await expenses.DeleteAsync(rentId);
        await using (var db = factory.CreateDbContext())
        {
            var log = await db.AuditLog.OrderBy(a => a.Id).ToListAsync();
            Assert.Contains(log, a => a.Action == Audit.BigDiscount && a.RefId == discounted && a.Details!.Contains("20%"));
            Assert.Contains(log, a => a.Action == Audit.SaleDeleted && a.RefId == discounted);
            Assert.Contains(log, a => a.Action == Audit.StockAdjusted && a.Details!.Contains("damaged"));
            Assert.Contains(log, a => a.Action == Audit.ExpenseDeleted && a.RefId == rentId);
            Assert.Equal(3, log.Count(a => a.Action == Audit.DayClosed));
            Assert.DoesNotContain(log, a => a.Action == Audit.BigDiscount && a.RefId != discounted); // full-price sales aren't logged
            Assert.All(log, a => Assert.Equal("system", a.UserName));
        }

        // ---------- Held sale: hold, list, resume once ----------
        var heldId = await held.HoldAsync(new HeldCart(Customer.WalkInId, [new HeldLine(itemId, 1, 1900)], 5, true, "blue bag"), "Lady in red", 1805);
        Assert.Equal(1, await held.CountAsync());
        Assert.Equal("Lady in red", (await held.ListAsync()).Single().Label);
        var cart = await held.ResumeAsync(heldId);
        Assert.NotNull(cart);
        Assert.Equal(1900m, cart!.Lines.Single().Rate);
        Assert.True(cart.DiscountIsPercent);
        Assert.Null(await held.ResumeAsync(heldId)); // already taken (another counter)
        Assert.Equal(0, await held.CountAsync());
        await Assert.ThrowsAsync<BusinessException>(() => held.HoldAsync(new HeldCart(1, [], 0, false, null), null, 0));
    }

    [Fact]
    public async Task Backup_is_copied_to_the_second_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "GenZPOS-backup-test-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "main");
        var copies = Path.Combine(root, "usb");
        Directory.CreateDirectory(folder);
        try
        {
            var connection = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "GenZPOS_BackupTest" }.ConnectionString;
            var builder = new DbContextOptionsBuilder<AppDbContext>();
            AppDbContext.Configure(builder, connection);
            var factory = new Factory(builder.Options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.MigrateAsync();
            }
            var cfg = new AppConfig { ConnectionString = connection, BackupFolder = folder, KeepBackups = 2, BackupCopyFolder = copies };
            var backup = new BackupService(factory, cfg);
            if (!backup.IsLocalServer) return; // backups only run next to SQL Server

            var made = await backup.BackupAsync();
            Assert.Null(backup.LastCopyError);
            Assert.True(File.Exists(Path.Combine(copies, made.Name)));
            Assert.Empty(Directory.GetFiles(copies, "*.copying"));

            // A missing drive only warns; the backup itself still succeeds.
            cfg.BackupCopyFolder = @"Q:\no-such-usb\GenZ";
            if (!Directory.Exists(@"Q:\"))
            {
                await backup.BackupAsync();
                Assert.Contains("not connected", backup.LastCopyError);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* SQL Server may still hold a file briefly */ }
        }
    }

    private static async Task<Dictionary<string, int>> GetCategoriesAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = factory.CreateDbContext();
        return await db.ExpenseCategories.ToDictionaryAsync(c => c.Name, c => c.Id);
    }
}
