using System.IO;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>
/// Stock from Excel as a purchase bill (one row = one piece) and a purchase back to Excel (database GenZPOS_PurchaseExcelTest).
/// The file is laid out like the owner's sheet: no Id, no qty column, pieces of one article + colour + size repeated with
/// their own barcodes, some Active cells blank.
/// </summary>
public class PurchaseExcelTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_PurchaseExcelTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private static readonly string[] OwnerHeaders =
    [
        "Id", "Article no", "Company", "Category", "Sub-category", "Product", "Shade", "Pack size", "Size", "Unit", "Barcode",
        "Purchase price", "Sale price", "Max discount %", "Reorder level", "Active",
    ];

    [Fact]
    public async Task Owner_sheet_imports_as_one_purchase_and_exports_back()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int firstSupplier;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var zee = new Supplier { Name = "Zee Traders" };
            db.Suppliers.Add(zee);
            await db.SaveChangesAsync();
            db.Suppliers.Add(new Supplier { Name = "Another Vendor" });
            await db.SaveChangesAsync();
            firstSupplier = zee.Id;
        }
        var excel = new ProductExcelService(factory, CurrentUser.ForSystem());

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        for (var c = 0; c < OwnerHeaders.Length; c++) ws.Cell(1, c + 1).Value = OwnerHeaders[c];
        var r = 2;
        void Piece(string article, string shade, int size, string barcode, decimal cost, decimal price, string? active = "Yes")
        {
            object?[] values = [null, article, "Zee Shoes", "Shoes", "Shoes", "Shoes", shade, "1", size, "Pcs", barcode, cost, price, null, null, active];
            for (var c = 0; c < values.Length; c++)
                ws.Cell(r, c + 1).Value = values[c] switch
                {
                    null => Blank.Value, string s => s, int i => i, decimal d => d, _ => throw new InvalidOperationException(),
                };
            r++;
        }
        Piece("ART000001", "Orange", 40, "SH000001", 2500, 4250);
        Piece("ART000001", "Orange", 41, "SH000002", 2500, 4250);
        Piece("ART000001", "Blue", 44, "SH000011", 2500, 4250, active: null);
        Piece("ART000002", "Black", 42, "SH000020", 2650, 4500);
        Piece("ART000002", "Black", 42, "SH000021", 2650, 4500);
        Piece("ART000002", "Black", 42, "SH000022", 2650, 4500, active: null);
        Piece("ART000002", "Cream", 42, "SH000015", 2650, 4500);
        Piece("ART000002", "Cream", 42, "SH000016", 2650, 4500);
        // A Qty column: 3 more Blue 44 of article 1, no barcode → 3 new pieces with new barcodes.
        ws.Cell(1, 17).Value = "Qty";
        Piece("ART000001", "Blue", 44, "", 2500, 4250);
        ws.Cell(r - 1, 11).Value = Blank.Value;
        ws.Cell(r - 1, 17).Value = 3;

        // Product import would refuse nothing here either: pieces with their own barcodes may share colour + size.
        var asProducts = await excel.PreviewAsync(Save(wb), createMissing: true);
        Assert.Empty(asProducts.Errors);

        // ---------- preview as a purchase ----------
        var plan = await excel.PreviewAsync(Save(wb), createMissing: true, forPurchase: true);
        Assert.Empty(plan.Errors);
        Assert.Equal(11, plan.OpeningTotal);
        Assert.Equal(11, plan.AddCount);
        Assert.Equal(6 * 2500m + 5 * 2650m, plan.PurchaseTotal);
        Assert.True(plan.CanImport);

        var id = await excel.ImportPurchaseAsync(plan, new Purchase
        {
            SupplierId = firstSupplier, PurchaseDate = new DateTime(2026, 10, 23), BillNo = "Z-1", Paid = 10000,
        });

        await using (var db = factory.CreateDbContext())
        {
            var bill = await db.Purchases.Include(p => p.Lines).SingleAsync(p => p.Id == id);
            Assert.Equal(11, bill.Lines.Count);
            Assert.All(bill.Lines, l => Assert.Equal(1, l.Qty));
            Assert.Equal(6 * 2500m + 5 * 2650m, bill.Total);
            Assert.Equal(firstSupplier, bill.SupplierId);

            var items = await db.StockItems.AsNoTracking().ToListAsync();
            Assert.Equal(11, items.Count);
            Assert.Equal(3, items.Count(i => i.ArticleNo == "ART000002" && i.Colour == "Black" && i.Size == "42"));
            Assert.Equal(4, items.Count(i => i.ArticleNo == "ART000001" && i.Colour == "Blue" && i.Size == "44"));
            Assert.All(items, i => Assert.True(i.IsActive));
            Assert.Equal(4500, items.Single(i => i.Barcode == "SH000022").SalePrice);
            Assert.Equal(1, await db.Products.CountAsync());

            var stock = await db.StockMovements.SumAsync(m => m.QtyIn - m.QtyOut);
            Assert.Equal(11, stock);
            Assert.All(await db.StockMovements.ToListAsync(), m => Assert.Equal(StockMovementType.Purchase, m.Type));
        }

        // ---------- the same file again: barcoded pieces are skipped, the Qty-3 row is 3 more new pieces ----------
        var again = await excel.PreviewAsync(Save(wb), createMissing: true, forPurchase: true);
        Assert.Empty(again.Errors);
        Assert.Equal(8, again.OpeningSkipped.Count);
        Assert.Equal(3, again.OpeningTotal);

        // ---------- export the purchase ----------
        using var exported = new MemoryStream();
        var name = await excel.ExportPurchaseAsync(id, exported);
        Assert.StartsWith($"Purchase {id} Zee Traders 2026-10-23", name);
        exported.Position = 0;
        using var outWb = new XLWorkbook(exported);
        var sheet = outWb.Worksheet("Products");
        for (var c = 0; c < OwnerHeaders.Length; c++)
            Assert.Equal(OwnerHeaders[c], sheet.Cell(1, c + 1).GetString()); // the owner's columns, same order
        Assert.Equal("Qty", sheet.Cell(1, 17).GetString());
        Assert.Equal(12, sheet.LastRowUsed()!.RowNumber());                 // 11 lines + title
        Assert.Equal(11, Enumerable.Range(2, 11).Sum(row => sheet.Cell(row, 17).GetDouble()));
        Assert.Equal("Zee Traders", outWb.Worksheet("Purchase").Cell(3, 2).GetString());
        Assert.Equal((double)(6 * 2500m + 5 * 2650m), outWb.Worksheet("Purchase").Cell(8, 2).GetDouble()); // Total

        // Importing the exported bill as stock again buys nothing (every piece is in stock).
        exported.Position = 0;
        var back = await excel.PreviewAsync(exported, createMissing: false, forPurchase: true);
        Assert.Empty(back.Errors);
        Assert.Equal(0, back.OpeningTotal);
        Assert.False(back.CanImport);
        await Assert.ThrowsAsync<BusinessException>(() => excel.ImportPurchaseAsync(back, new Purchase { SupplierId = firstSupplier }));

        // Qty 2 on a row with a barcode is an error (one barcode = one piece).
        ws.Cell(2, 17).Value = 2;
        var wrong = await excel.PreviewAsync(Save(wb), createMissing: true, forPurchase: true);
        Assert.Contains(wrong.Errors, e => e.Row == 2 && e.Message.Contains("one barcode is one piece"));
    }

    private static MemoryStream Save(XLWorkbook wb)
    {
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }
}
