using System.IO;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>Products → Excel → edited → back in: matching, updates, new rows, errors (database GenZPOS_ExcelTest).</summary>
public class ProductExcelTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_ExcelTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    // Columns as exported.
    private const int Id = 1, Article = 2, Company = 3, Category = 4, Sub = 5, Name = 6, Shade = 7, Pack = 8, Size = 9, Unit = 10,
        Barcode = 11, Cost = 12, Price = 13, MaxDiscount = 14, Reorder = 15, Active = 16, Opening = 17, StockedBy = 18, InStock = 19;

    private const int BrushesSubCategoryId = 16; // seeded: Tools & Accessories › Brushes

    [Fact]
    public async Task Export_edit_import_round_trip()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int shieldId; // the Weather Shield White size / colour (the Id column = the variant's Id)
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var berger = new Company { Name = "Berger" };
            db.Companies.Add(berger);
            var shield = new Product
            {
                Company = berger, SubCategoryId = 2, Name = "Weather Shield", PackSize = "Gallon", MaxDiscountPercent = 10,
                StockItems = [new() { ArticleNo = "WS-01", Colour = "White", Barcode = "111", PurchasePrice = 1000, SalePrice = 1300 }],
            };
            db.Products.AddRange(
                shield,
                new Product
                {
                    Company = berger, SubCategoryId = 1, Name = "Silk", PackSize = "Quarter",
                    StockItems = [new() { ArticleNo = "SILK-1", Colour = "Ivory", Barcode = "S-IV", PurchasePrice = 300, SalePrice = 400, ReorderLevel = 5 }],
                },
                new Product // one article, two sizes
                {
                    Company = berger, SubCategoryId = BrushesSubCategoryId, Name = "Paint Brush",
                    StockItems = [new() { ArticleNo = "PB-1", Size = "2 inch", Barcode = "PB2", SalePrice = 150 }, new() { ArticleNo = "PB-1", Size = "3 inch", Barcode = "PB3", SalePrice = 220 }],
                });
            await db.SaveChangesAsync();
            shieldId = shield.StockItems[0].Id;
        }

        var excel = new ProductExcelService(factory, CurrentUser.ForSystem());

        // ---------- export: 4 products + help/lists sheets ----------
        using var exported = new MemoryStream();
        await excel.ExportAsync(exported);
        exported.Position = 0;
        using var wb = new XLWorkbook(exported);
        var ws = wb.Worksheet("Products");
        Assert.Equal("Article no", ws.Cell(1, Article).GetString());
        Assert.Equal("Size", ws.Cell(1, Size).GetString());
        Assert.Equal("Max discount %", ws.Cell(1, MaxDiscount).GetString());
        Assert.Equal(5, ws.LastRowUsed()!.RowNumber());
        var shieldRow = RowOf(ws, "Weather Shield");
        var silkRow = RowOf(ws, "Silk");
        var brush2Row = RowOf(ws, "Paint Brush", "2 inch");
        Assert.Equal("WS-01", ws.Cell(shieldRow, Article).GetString());
        Assert.Equal(10, ws.Cell(shieldRow, MaxDiscount).GetDouble());

        // Unchanged file → nothing to import.
        var same = await excel.PreviewAsync(Save(wb), createMissing: false);
        Assert.Empty(same.Errors);
        Assert.Equal(4, same.UnchangedCount);
        Assert.False(same.CanImport);

        // ---------- edit ----------
        ws.Cell(shieldRow, Price).Value = 1350;                 // price change by Id
        ws.Cell(shieldRow, MaxDiscount).Value = "none";         // remove the limit
        ws.Cell(silkRow, Id).Value = Blank.Value;               // no Id: matched by article no. + shade + size
        ws.Cell(silkRow, Active).Value = "No";
        ws.Cell(silkRow, Reorder).Value = Blank.Value;          // blank keeps reorder level 5
        ws.Cell(brush2Row, Id).Value = Blank.Value;             // no Id, no article no.: company + name + pack, then the size
        ws.Cell(brush2Row, Article).Value = Blank.Value;
        ws.Cell(brush2Row, MaxDiscount).Value = 5;              // the article's limit (both sizes)
        AddRow(ws, 6, "Dulux", "Emulsion", "Interior Emulsion", "Velvet Touch", "Pearl", "Gallon", null, 8901234567890m, 2000, 2600);
        ws.Cell(6, Article).Value = 7701;                       // typed as a number
        ws.Cell(6, MaxDiscount).Value = 0.15;                   // a %-formatted cell: 15%
        ws.Cell(6, MaxDiscount).Style.NumberFormat.Format = "0%";
        AddRow(ws, 7, "berger", "Emulsion", "Ceiling", "Easy Clean", null, "Drum", "Ltr", null, 5000, null);

        // Without "create missing": Dulux and the Ceiling sub-category are errors.
        var strict = await excel.PreviewAsync(Save(wb), createMissing: false);
        Assert.Equal([6, 7], strict.Errors.Select(e => e.Row));
        Assert.False(strict.CanImport);
        await Assert.ThrowsAsync<BusinessException>(() => excel.ImportAsync(strict));

        var plan = await excel.PreviewAsync(Save(wb), createMissing: true);
        Assert.Empty(plan.Errors);
        Assert.Equal(2, plan.AddCount);
        Assert.Equal(3, plan.UpdateCount);
        Assert.Equal(["Dulux"], plan.NewCompanies);
        Assert.Equal(["Emulsion › Ceiling"], plan.NewSubCategories);
        var shieldChanges = plan.Lines.Single(l => l.Source.Row == shieldRow).Changes;
        Assert.Contains("Price 1,300.00 → 1,350.00", shieldChanges);
        Assert.Contains("Max discount 10% → none", shieldChanges);

        Assert.Equal((2, 3), await excel.ImportAsync(plan));

        await using (var db = factory.CreateDbContext())
        {
            var all = await db.Products.Include(p => p.Company).Include(p => p.SubCategory).Include(p => p.StockItems).ToListAsync();
            Assert.Equal(5, all.Count);
            Assert.Equal(6, all.Sum(p => p.StockItems.Count));
            var shield = all.Single(p => p.Name == "Weather Shield");
            Assert.Equal(1350, shield.StockItems.Single().SalePrice);
            Assert.Null(shield.MaxDiscountPercent);
            Assert.Equal("WS-01", shield.StockItems.Single().ArticleNo);
            var silk = all.Single(p => p.Name == "Silk").StockItems.Single();
            Assert.False(silk.IsActive);
            Assert.Equal(5, silk.ReorderLevel);
            var brush = all.Single(p => p.Name == "Paint Brush");
            Assert.All(brush.StockItems, i => Assert.Equal("PB-1", i.ArticleNo));
            Assert.Equal(5, brush.MaxDiscountPercent);
            Assert.Equal(2, brush.StockItems.Count);
            var velvet = all.Single(p => p.Name == "Velvet Touch");
            Assert.Equal("Dulux", velvet.Company!.Name);
            Assert.Equal("8901234567890", velvet.StockItems.Single().Barcode); // typed as a number in Excel
            Assert.Equal("Pearl", velvet.StockItems.Single().Colour);
            Assert.Equal("7701", velvet.StockItems.Single().ArticleNo);
            Assert.Equal(15, velvet.MaxDiscountPercent);
            Assert.Equal("Pcs", velvet.Unit);
            var easy = all.Single(p => p.Name == "Easy Clean");
            Assert.Equal("Berger", easy.Company!.Name);       // "berger" matched case-insensitively
            Assert.Equal("Ceiling", easy.SubCategory!.Name);
            Assert.Equal(0, easy.StockItems.Single().SalePrice);
            Assert.Matches("^SH[0-9]{6}$", easy.StockItems.Single().Barcode);
            Assert.Matches("^ART[0-9]{6}$", easy.StockItems.Single().ArticleNo);
            Assert.Null(easy.MaxDiscountPercent);
            Assert.Equal("Ltr", easy.Unit);
        }

        // Importing the same file again changes nothing.
        var again = await excel.PreviewAsync(Save(wb), createMissing: true);
        Assert.Empty(again.Errors);
        Assert.Equal(6, again.UnchangedCount);

        // ---------- an older export (no Article no / Size / Max discount columns) keeps those values ----------
        using var old = new XLWorkbook();
        var o = old.AddWorksheet("Products");
        string[] oldHeaders = ["Id", "Company", "Category", "Sub-category", "Product", "Shade", "Pack size", "Unit", "Barcode", "Purchase price", "Sale price", "Reorder level", "Active"];
        for (var c = 0; c < oldHeaders.Length; c++) o.Cell(1, c + 1).Value = oldHeaders[c];
        o.Cell(2, 1).Value = shieldId;
        o.Cell(2, 2).Value = "Berger";
        o.Cell(2, 3).Value = "Emulsion";
        o.Cell(2, 4).Value = "Exterior Emulsion";
        o.Cell(2, 5).Value = "Weather Shield";
        o.Cell(2, 6).Value = "White";
        o.Cell(2, 7).Value = "Gallon";
        o.Cell(2, 9).Value = "111";
        o.Cell(2, 11).Value = 1400;
        o.Cell(3, 2).Value = "Berger";                           // no Id, no Size column: both brush sizes match
        o.Cell(3, 3).Value = "Tools & Accessories";
        o.Cell(3, 4).Value = "Brushes";
        o.Cell(3, 5).Value = "Paint Brush";
        var oldPlan = await excel.PreviewAsync(Save(old), createMissing: false);
        Assert.Equal([3], oldPlan.Errors.Select(e => e.Row));
        Assert.Contains("Put the Id", oldPlan.Errors[0].Message);
        o.Row(3).Delete();
        oldPlan = await excel.PreviewAsync(Save(old), createMissing: false);
        Assert.Equal("Price 1,350.00 → 1,400.00", oldPlan.Lines.Single().Changes);
        await excel.ImportAsync(oldPlan);
        await using (var db = factory.CreateDbContext())
        {
            var shield = await db.StockItems.Include(v => v.Product).SingleAsync(v => v.Id == shieldId);
            Assert.Equal(1400, shield.SalePrice);
            Assert.Equal("WS-01", shield.ArticleNo);
        }

        // ---------- errors: bad number, unknown Id, missing name, duplicate barcode, bad max discount ----------
        using var bad = new XLWorkbook();
        var b = bad.AddWorksheet("Products");
        string[] headers = ["Id", "Article no", "Company", "Category", "Sub-category", "Product", "Shade", "Pack size", "Size", "Unit", "Barcode",
            "Purchase price", "Sale price", "Max discount %", "Reorder level", "Active"];
        for (var c = 0; c < headers.Length; c++) b.Cell(1, c + 1).Value = headers[c];
        AddRow(b, 2, "Berger", "Emulsion", "Interior Emulsion", "A", null, null, null, null, null, null);
        b.Cell(2, Price).Value = "twelve";
        AddRow(b, 3, "Berger", "Emulsion", "Interior Emulsion", "B", null, null, null, 333m, null, null);
        AddRow(b, 4, "Berger", "Emulsion", "Interior Emulsion", "C", null, null, null, null, null, null);
        b.Cell(4, Id).Value = 999;
        AddRow(b, 5, "Berger", "Emulsion", "Interior Emulsion", "", null, null, null, null, null, null);
        AddRow(b, 6, "Berger", "Emulsion", "Interior Emulsion", "D", null, null, null, 333m, null, null);   // same barcode as row 3
        AddRow(b, 7, "Berger", "Emulsion", "Interior Emulsion", "E", null, null, null, null, null, null);
        b.Cell(7, MaxDiscount).Value = 120;
        var errors = await excel.PreviewAsync(Save(bad), createMissing: true);
        Assert.Equal([2, 4, 5, 6, 7], errors.Errors.Select(e => e.Row));
        Assert.Contains("not a number", errors.Errors[0].Message);
        Assert.Contains("no product with Id 999", errors.Errors[1].Message);
        Assert.Contains("Product is empty", errors.Errors[2].Message);
        Assert.Contains("also on row 3", errors.Errors[3].Message);
        Assert.Contains("between 0 and 100", errors.Errors[4].Message);

        // A file without the title row is refused with a clear message.
        using var noHeaders = new XLWorkbook();
        noHeaders.AddWorksheet("Sheet1").Cell(1, 1).Value = "hello";
        await Assert.ThrowsAsync<BusinessException>(() => excel.PreviewAsync(Save(noHeaders), true));
    }

    /// <summary>One row = one article in many colours × sizes (A-001, Blue / Orange, 40–45), and its export.</summary>
    [Fact]
    public async Task One_article_row_with_colours_and_sizes()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }
        var excel = new ProductExcelService(factory, CurrentUser.ForSystem());
        string[] headers = ["Id", "Article no", "Company", "Category", "Sub-category", "Product", "Shade", "Pack size", "Size", "Unit", "Barcode",
            "Purchase price", "Sale price", "Max discount %", "Reorder level", "Active"];

        XLWorkbook Sheet(params Action<IXLWorksheet>[] rows)
        {
            var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Products");
            for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            for (var i = 0; i < rows.Length; i++) rows[i](ws);
            return wb;
        }
        void AddArticle(IXLWorksheet ws, int row, string colours, string sizes, decimal price)
        {
            AddRow(ws, row, "Stride", "Footwear", "Shoes", "Jogger", colours, null, "Pair", null, 1800, price);
            ws.Cell(row, Article).Value = "A-001";
            ws.Cell(row, Size).Value = sizes;
        }

        // ---------- one row → 12 products ----------
        using (var wb = Sheet(ws => AddArticle(ws, 2, "Blue, Orange", "40-45", 2500)))
        {
            var plan = await excel.PreviewAsync(Save(wb), createMissing: true);
            Assert.Empty(plan.Errors);
            Assert.Equal(12, plan.AddCount);
            Assert.All(plan.Lines, l => Assert.Equal(2, l.Source.Row));            // all from Excel row 2
            Assert.Equal(["Stride"], plan.NewCompanies);
            Assert.Equal((12, 0), await excel.ImportAsync(plan));
        }

        await using (var db = factory.CreateDbContext())
        {
            var jogger = await db.Products.Include(p => p.StockItems).SingleAsync(p => p.Name == "Jogger");
            Assert.Equal(1, await db.Products.CountAsync());   // one product, article A-001 …
            Assert.Equal("Pair", jogger.Unit);
            var all = jogger.StockItems;
            Assert.Equal(12, all.Count);                        // … in 12 sizes / colours
            Assert.Equal(["Blue", "Orange"], all.Select(v => v.Colour!).Distinct().Order());
            Assert.Equal(["40", "41", "42", "43", "44", "45"], all.Select(v => v.Size!).Distinct().Order());
            Assert.All(all, v => { Assert.Equal(2500, v.SalePrice); Assert.Matches("^SH[0-9]{6}$", v.Barcode); });
            Assert.Equal(12, all.Select(v => v.Barcode).Distinct().Count()); // each size / colour its own barcode
            // stock for the export check
            db.StockMovements.Add(new StockMovement
            {
                StockItemId = all.Single(v => v.Colour == "Blue" && v.Size == "42").Id, MovementDate = DateTime.Today,
                Type = StockMovementType.Opening, QtyIn = 3,
            });
            await db.SaveChangesAsync();
        }

        // ---------- new price for every Blue size + a new size 46 in one row ----------
        using (var wb = Sheet(ws => AddArticle(ws, 2, "Blue", "40-46", 2600)))
        {
            var plan = await excel.PreviewAsync(Save(wb), createMissing: false);
            Assert.Empty(plan.Errors);
            Assert.Equal(1, plan.AddCount);    // Blue 46
            Assert.Equal(6, plan.UpdateCount); // Blue 40–45: price 2,500 → 2,600
            await excel.ImportAsync(plan);
        }
        await using (var db = factory.CreateDbContext())
        {
            Assert.All(await db.StockItems.Where(v => v.Colour == "Blue").ToListAsync(), v => Assert.Equal(2600, v.SalePrice));
            Assert.All(await db.StockItems.Where(v => v.Colour == "Orange").ToListAsync(), v => Assert.Equal(2500, v.SalePrice));
            Assert.Equal(1, await db.Products.CountAsync());
        }

        // ---------- rules for such a row; a paint shade with a comma and no article no. is NOT split ----------
        using (var wb = Sheet(
                   ws => { AddArticle(ws, 2, "Red, Green", "40", 2500); ws.Cell(2, Id).Value = 5; },
                   ws => { AddArticle(ws, 3, "Red, Green", "40", 2500); ws.Cell(3, Barcode).Value = "999"; },
                   ws => AddRow(ws, 4, "Stride", "Footwear", "Shoes", "Polish", "White, Matt", null, null, null, 100, 150)))
        {
            var plan = await excel.PreviewAsync(Save(wb), createMissing: true);
            Assert.Equal([2, 3], plan.Errors.Select(e => e.Row));
            Assert.Contains("Leave the Id empty", plan.Errors[0].Message);
            Assert.Contains("Leave the barcode empty", plan.Errors[1].Message);
            Assert.Equal("White, Matt", plan.Lines.Single(l => l.Source.Row == 4).Source.Shade);
        }

        // ---------- export: sizes in order, In stock column, Articles sheet ----------
        using var exported = new MemoryStream();
        await excel.ExportAsync(exported);
        exported.Position = 0;
        using var book = new XLWorkbook(exported);
        var products = book.Worksheet("Products");
        Assert.Equal("In stock (not imported)", products.Cell(1, InStock).GetString());
        var blueSizes = products.RowsUsed().Skip(1).Where(r => r.Cell(Shade).GetString() == "Blue").Select(r => r.Cell(Size).GetString()).ToList();
        Assert.Equal(["40", "41", "42", "43", "44", "45", "46"], blueSizes);
        var blue42 = products.RowsUsed().Single(r => r.Cell(Shade).GetString() == "Blue" && r.Cell(Size).GetString() == "42");
        Assert.Equal(3, blue42.Cell(InStock).GetDouble());

        var articles = book.Worksheet("Articles");
        Assert.Equal("A-001", articles.Cell(2, 1).GetString());
        Assert.Equal("Blue, Orange", articles.Cell(2, 5).GetString());
        Assert.Equal("40, 41, 42, 43, 44, 45, 46", articles.Cell(2, 6).GetString());
        Assert.Equal(13, articles.Cell(2, 7).GetDouble());
        Assert.Equal(3, articles.Cell(2, 8).GetDouble());
        Assert.Equal("2,500.00 – 2,600.00", articles.Cell(2, 9).GetString());

        // The exported file imports back unchanged (In stock / Articles are not read).
        exported.Position = 0;
        var back = await excel.PreviewAsync(exported, createMissing: false);
        Assert.Empty(back.Errors);
        Assert.Equal(13, back.UnchangedCount);
    }

    /// <summary>Opening stock column: added for new / never-stocked products, never twice.</summary>
    [Fact]
    public async Task Opening_stock_is_added_once()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int emptyId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var stride = new Company { Name = "Stride" };
            // Exists already, but never had stock (e.g. added by an earlier import without quantities).
            var empty = new Product
            {
                Company = stride, SubCategoryId = 1, Name = "Sandal",
                StockItems = [new() { ArticleNo = "SAN-1", Size = "40", Barcode = "SAN-40", SalePrice = 900 }],
            };
            db.Products.Add(empty);
            await db.SaveChangesAsync();
            emptyId = empty.StockItems[0].Id;
        }
        var excel = new ProductExcelService(factory, CurrentUser.ForSystem());
        string[] headers = ["Id", "Article no", "Company", "Category", "Sub-category", "Product", "Shade", "Pack size", "Size", "Unit", "Barcode",
            "Purchase price", "Sale price", "Max discount %", "Reorder level", "Active", "Opening stock"];
        XLWorkbook Book()
        {
            var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Products");
            for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            // A new single product with 5 in the shop.
            AddRow(ws, 2, "Stride", "Emulsion", "Interior Emulsion", "Laces", null, null, null, null, 20, 50);
            ws.Cell(2, Opening).Value = 5;
            // One article row: 2 colours × 3 sizes, 2 pairs of EACH.
            AddRow(ws, 3, "Stride", "Emulsion", "Interior Emulsion", "Jogger", "Blue, Orange", null, "Pair", null, 1800, 2500);
            ws.Cell(3, Article).Value = "A-001";
            ws.Cell(3, Size).Value = "40-42";
            ws.Cell(3, Opening).Value = 2;
            // The existing never-stocked sandal: gets its opening stock too.
            AddRow(ws, 4, "Stride", "Emulsion", "Interior Emulsion", "Sandal", null, null, null, null, null, null);
            ws.Cell(4, Size).Value = "40";
            ws.Cell(4, Opening).Value = 4;
            return wb;
        }

        async Task<Dictionary<string, decimal>> StockByName()
        {
            await using var db = factory.CreateDbContext();
            var all = await db.StockItems.Include(v => v.Product).ToListAsync();
            var onHand = await StockService.GetOnHandAsync(db, all.Select(v => v.Id));
            return all.GroupBy(v => v.Product!.Name).ToDictionary(g => g.Key, g => g.Sum(v => onHand.GetValueOrDefault(v.Id)));
        }

        using (var wb = Book())
        {
            var plan = await excel.PreviewAsync(Save(wb), createMissing: true);
            Assert.Empty(plan.Errors);
            Assert.Equal(7, plan.AddCount);          // Laces + 6 Jogger sizes/colours
            Assert.Equal(1, plan.UpdateCount);       // Sandal: only its opening stock
            Assert.Equal("Opening stock +4", plan.Lines.Single(l => l.StockItemId == emptyId).Changes);
            Assert.Equal(5 + 6 * 2 + 4, plan.OpeningTotal);
            Assert.Equal(8, plan.OpeningCount);
            Assert.Empty(plan.OpeningSkipped);
            await excel.ImportAsync(plan);
        }
        var stock = await StockByName();
        Assert.Equal(5, stock["Laces"]);
        Assert.Equal(12, stock["Jogger"]);
        Assert.Equal(4, stock["Sandal"]);
        await using (var db = factory.CreateDbContext())
            Assert.All(await db.StockMovements.ToListAsync(), m =>
            {
                Assert.Equal(StockMovementType.Opening, m.Type);
                Assert.Equal("Opening stock (Excel import)", m.Notes);
            });

        // The same file again: everything already has stock → nothing added, nothing doubled.
        using (var wb = Book())
        {
            var again = await excel.PreviewAsync(Save(wb), createMissing: true);
            Assert.Empty(again.Errors);
            Assert.Equal(0, again.OpeningTotal);
            Assert.Equal(8, again.OpeningSkipped.Count);
            Assert.Contains("already has stock", again.OpeningSkipped[0].Message);
            Assert.False(again.CanImport);           // nothing else changed either
        }
        Assert.Equal(stock, await StockByName());

        // Negative opening stock is an error.
        using (var wb = Book())
        {
            wb.Worksheet("Products").Cell(2, Opening).Value = -1;
            var bad = await excel.PreviewAsync(Save(wb), createMissing: true);
            Assert.Equal([2], bad.Errors.Select(e => e.Row));
        }
    }

    /// <summary>
    /// The shoe store's file: every article is named "Shoes", articles are told apart only by their article no.
    /// (ART000001 Blue 40 and ART000003 Blue 40 are different products). Its two mistakes are caught.
    /// </summary>
    [Fact]
    public async Task Shoe_articles_sharing_one_product_name()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }
        var excel = new ProductExcelService(factory, CurrentUser.ForSystem());
        string[] headers = ["Id", "Article no", "Company", "Category", "Sub-category", "Product", "Shade", "Pack size", "Size", "Unit", "Barcode",
            "Purchase price", "Sale price", "Max discount %", "Reorder level", "Active", "Opening stock"];
        (string Art, string Colour, string Size, string? Barcode)[] rows =
        [
            ("ART000001", "Orange", "40", "SH000001"), ("ART000001", "Orange", "41", "SH000002"),
            ("ART000001", "Blue", "40", "SH000003"), ("ART000001", "Blue", "41", "SH000004"),
            ("ART000002", "Black", "40", "SH000004"),   // mistake 1: barcode of the row above (row 5)
            ("ART000002", "Cream", "42", "SH000005"),
            ("ART000002", "Cream", "42", "SH000006"),   // Cream 42 twice with own barcodes: two pieces, fine (one barcode per piece)
            ("ART000003", "Blue", "40", null), ("ART000003", "Blue", "41", null), // same colour/size as ART000001: fine
        ];
        XLWorkbook Book(bool fixedFile)
        {
            var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Products");
            for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            var r = 2;
            foreach (var (art, colour, size, barcode) in rows)
            {
                if (fixedFile && ((art == "ART000002" && colour == "Black") || (art == "ART000002" && barcode == "SH000006"))) continue;
                AddRow(ws, r, "Zee Shoes", "Shoes", "Shoes", "Shoes", colour, null, "Pair", null, 2500, 4250);
                ws.Cell(r, Article).Value = art;
                ws.Cell(r, Size).Value = size;
                ws.Cell(r, Opening).Value = 2;
                if (barcode is not null) ws.Cell(r, Barcode).Value = barcode;
                r++;
            }
            return wb;
        }

        // As sent: the barcode mistake stops the import, nothing else is reported.
        using (var wb = Book(fixedFile: false))
        {
            var plan = await excel.PreviewAsync(Save(wb), createMissing: true);
            Assert.Equal([6], plan.Errors.Select(e => e.Row));
            Assert.Contains("Barcode SH000004 is also on row 5", plan.Errors[0].Message);
            Assert.False(plan.CanImport);
        }

        // Fixed: ONE product "Shoes" with 3 articles in 7 stock items; ART000001 Blue 40 and ART000003 Blue 40 separate,
        // missing barcodes generated.
        using (var wb = Book(fixedFile: true))
        {
            var plan = await excel.PreviewAsync(Save(wb), createMissing: true);
            Assert.Empty(plan.Errors);
            Assert.Equal(7, plan.AddCount);
            await excel.ImportAsync(plan);

            await using (var db = factory.CreateDbContext())
            {
                var product = await db.Products.SingleAsync();
                Assert.Equal("Shoes", product.Name);
                var all = await db.StockItems.ToListAsync();
                Assert.Equal(7, all.Count);
                Assert.All(all, v => Assert.Equal(product.Id, v.ProductId));
                Assert.Equal(["ART000001", "ART000002", "ART000003"], all.Select(v => v.ArticleNo).Distinct().Order());
                Assert.Equal(2, all.Count(v => v.Colour == "Blue" && v.Size == "40"));
                Assert.Equal(["ART000001", "ART000003"], all.Where(v => v.Colour == "Blue" && v.Size == "40").Select(v => v.ArticleNo).Order());
                Assert.Equal(7, all.Select(v => v.Barcode).Distinct().Count());
                Assert.All(all, v => Assert.False(string.IsNullOrEmpty(v.Barcode)));
                var stock = await StockService.GetOnHandAsync(db, all.Select(v => v.Id));
                Assert.Equal(14, stock.Values.Sum());
            }

            // Same file again: each row finds its own size / colour by article no. + colour + size; nothing doubles.
            var again = await excel.PreviewAsync(Save(wb), createMissing: true);
            Assert.Empty(again.Errors);
            Assert.Equal(7, again.UnchangedCount);
        }
    }

    private static int RowOf(IXLWorksheet ws, string name, string? size = null) =>
        ws.RowsUsed().Single(r => r.Cell(Name).GetString() == name && (size is null || r.Cell(Size).GetString() == size)).RowNumber();

    private static void AddRow(IXLWorksheet ws, int row, string company, string category, string sub, string name, string? shade,
        string? pack, string? unit, decimal? barcode, decimal? cost, decimal? price)
    {
        ws.Cell(row, Company).Value = company;
        ws.Cell(row, Category).Value = category;
        ws.Cell(row, Sub).Value = sub;
        ws.Cell(row, Name).Value = name;
        ws.Cell(row, Shade).Value = shade;
        ws.Cell(row, Pack).Value = pack;
        ws.Cell(row, Unit).Value = unit;
        if (barcode is { } bc) ws.Cell(row, Barcode).Value = bc; // a number cell, as when typed into Excel
        if (cost is { } c) ws.Cell(row, Cost).Value = c;
        if (price is { } p) ws.Cell(row, Price).Value = p;
    }

    private static MemoryStream Save(XLWorkbook wb)
    {
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }
}
