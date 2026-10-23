using System.IO;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>Automatic, unique article no. and barcode (ProductCodes), database GenZPOS_CodesTest.</summary>
public class ProductCodesTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_CodesTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public void Code_formats()
    {
        Assert.Equal("SH000001", ProductCodes.ToBarcode(1));
        Assert.Equal("SH000123", ProductCodes.ToBarcode(123));
        Assert.Equal("SH1000000", ProductCodes.ToBarcode(1_000_000));    // grows, never cut off
        Assert.Equal("ART000001", ProductCodes.ToArticleNo(1));
        Assert.Equal("ART012345", ProductCodes.ToArticleNo(12345));
    }

    [Fact]
    public async Task Codes_are_generated_and_unique()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int stride, oldProductId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var c = new Company { Name = "Stride" };
            db.Companies.Add(c);
            // Hand-typed codes in the generated range must be skipped, not duplicated.
            var old = new Product
            {
                Company = c, SubCategoryId = 1, Name = "Old", 
                StockItems = [new() { ArticleNo = ProductCodes.ToArticleNo(1), Barcode = ProductCodes.ToBarcode(1) }],
            };
            db.Products.Add(old);
            await db.SaveChangesAsync();
            (stride, oldProductId) = (c.Id, old.Id);
        }

        // ---------- single codes ----------
        await using (var db = factory.CreateDbContext())
        {
            var barcode = await ProductCodes.ResolveBarcodeAsync(db, null);
            Assert.Equal(ProductCodes.ToBarcode(2), barcode);                          // 1 is taken → next
            Assert.Equal("ART000002", await ProductCodes.ResolveArticleNoAsync(db, null));
            // The article no. is the design: an empty no. always starts a new one.
            Assert.Equal(ProductCodes.ToArticleNo(3), await ProductCodes.ResolveArticleNoAsync(db, null));
            Assert.Equal("ZZ-1", await ProductCodes.ResolveArticleNoAsync(db, " ZZ-1 ")); // typed, trimmed
            Assert.Equal(ProductCodes.ToArticleNo(1), await ProductCodes.ResolveArticleNoAsync(db, ProductCodes.ToArticleNo(1), oldProductId)); // its own

            var article = await Assert.ThrowsAsync<BusinessException>(() => ProductCodes.ResolveArticleNoAsync(db, ProductCodes.ToArticleNo(1)));
            Assert.Contains("already belongs to Stride · Old", article.Message);
            var taken = await Assert.ThrowsAsync<BusinessException>(() => ProductCodes.ResolveBarcodeAsync(db, ProductCodes.ToBarcode(1)));
            Assert.Contains("already used", taken.Message);
        }

        // The database itself refuses a duplicate barcode (unique index).
        await using (var db = factory.CreateDbContext())
        {
            db.StockItems.Add(new StockItem { ProductId = oldProductId, Colour = "Copy", Barcode = ProductCodes.ToBarcode(1) });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // ---------- sizes × colours: one article no., a barcode each ----------
        var variants = new StockItemService(factory, CurrentUser.ForSystem());
        var saved = await variants.SaveAsync(new Product { CompanyId = stride, SubCategoryId = 1, Name = "Jogger", Unit = "Pair" }, [],
            [new("Blue", "40", 0), new("Blue", "41", 0), new("Orange", "40", 0)], new StockItemPrices(0, 2500));
        var jogger = saved.ArticleNo;
        Assert.Matches("^ART[0-9]{6}$", jogger);
        Assert.Equal(3, saved.Created);
        // More sizes later: added to that article no. of the product.
        await variants.EnsureOneAsync(saved.ProductId, jogger, "Orange", "41", null, new StockItemPrices(0, 2500));
        // Another product can't take it.
        await Assert.ThrowsAsync<BusinessException>(() => variants.SaveAsync(
            new Product { CompanyId = stride, SubCategoryId = 1, Name = "Sandal" }, [], [new("Black", "40", 0)], new StockItemPrices(0, 0), jogger));

        await using (var db = factory.CreateDbContext())
        {
            var joggers = await db.Products.Include(p => p.StockItems).Where(p => p.Name == "Jogger").ToListAsync();
            Assert.Single(joggers);
            Assert.All(joggers[0].StockItems, i => Assert.Equal(jogger, i.ArticleNo));
            Assert.Equal(4, joggers[0].StockItems.Count);
            Assert.Equal(4, joggers[0].StockItems.Select(v => v.Barcode).Distinct().Count());
            Assert.All(joggers[0].StockItems, v => Assert.Matches("^SH[0-9]{6}$", v.Barcode));
        }

        // ---------- Excel: new rows get codes; one article shares its no.; blank keeps; conflicts are errors ----------
        var excel = new ProductExcelService(factory, CurrentUser.ForSystem());
        string[] headers = ["Id", "Article no", "Company", "Category", "Sub-category", "Product", "Shade", "Pack size", "Size", "Unit", "Barcode"];
        MemoryStream File(params string?[][] rows)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Products");
            for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    if (rows[r][c] is { } v) ws.Cell(r + 2, c + 1).Value = v;
            var ms = new MemoryStream();
            wb.SaveAs(ms);
            ms.Position = 0;
            return ms;
        }

        // Two sizes of a new article (no article no. typed) + a single new product.
        var plan = await excel.PreviewAsync(File(
            [null, null, "Stride", "Emulsion", "Interior Emulsion", "Runner", "Red", null, "40", "Pair", null],
            [null, null, "Stride", "Emulsion", "Interior Emulsion", "Runner", "Red", null, "41", "Pair", null],
            [null, null, "Stride", "Emulsion", "Interior Emulsion", "Polish", null, null, null, null, null]), createMissing: true);
        Assert.Empty(plan.Errors);
        await excel.ImportAsync(plan);
        await using (var db = factory.CreateDbContext())
        {
            var runner = await db.Products.Include(p => p.StockItems).SingleAsync(p => p.Name == "Runner");
            Assert.Equal(2, runner.StockItems.Count);
            Assert.Single(runner.StockItems.Select(i => i.ArticleNo).Distinct());          // one new article for both sizes
            Assert.Matches("^ART[0-9]{6}$", runner.StockItems[0].ArticleNo);
            var polish = await db.Products.Include(p => p.StockItems).SingleAsync(p => p.Name == "Polish");
            Assert.NotEqual(runner.StockItems[0].ArticleNo, polish.StockItems.Single().ArticleNo);
            var all = runner.StockItems.Concat(polish.StockItems).ToList();
            Assert.All(all, v => Assert.Matches("^SH[0-9]{6}$", v.Barcode));
            Assert.Equal(3, all.Select(v => v.Barcode).Distinct().Count());
        }

        // Re-import with blank codes: nothing changes (blank never clears a code).
        var again = await excel.PreviewAsync(File(
            [null, null, "Stride", "Emulsion", "Interior Emulsion", "Polish", null, null, null, null, null]), createMissing: true);
        Assert.Equal(1, again.UnchangedCount);

        // An article no. of another article, or one article no. on two different products in a file: errors.
        var conflicts = await excel.PreviewAsync(File(
            [null, jogger, "Stride", "Emulsion", "Interior Emulsion", "Sandal", "Black", null, "40", null, null],
            [null, "NEW-1", "Stride", "Emulsion", "Interior Emulsion", "Boot", null, null, "40", null, null],
            [null, "NEW-1", "Stride", "Emulsion", "Interior Emulsion", "Slipper", null, null, "40", null, null],
            [null, "NEW-1", "Stride", "Emulsion", "Interior Emulsion", "Boot", null, null, "41", null, null]), createMissing: true);
        Assert.Equal([2, 4], conflicts.Errors.Select(e => e.Row));
        Assert.Contains("already belongs to Stride · Jogger", conflicts.Errors[0].Message);
        Assert.Contains("row 3", conflicts.Errors[1].Message);
    }
}
