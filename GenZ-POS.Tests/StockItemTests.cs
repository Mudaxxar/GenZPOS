using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>One article no. in several sizes × colours (StockItemService), database GenZPOS_VariantTest.</summary>
public class StockItemTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_VariantTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public void Lists_and_size_order()
    {
        Assert.Equal(["6", "7", "8", "9", "10"], StockItemService.ParseList("6-10"));
        Assert.Equal(["Black", "Brown"], StockItemService.ParseList(" Black, Brown ,BLACK; ,brown"));
        Assert.Equal(["S", "M", "L"], StockItemService.ParseList("S, M, L"));
        Assert.Empty(StockItemService.ParseList("  "));
        Assert.Equal(["6", "7", "10"], StockItemService.SortSizes(["10", "6", "7", null]));       // numeric, not text
        Assert.Equal(["S", "M", "XL"], StockItemService.SortSizes(["XL", "S", "M"]));            // clothing order
        Assert.Equal(["2 inch", "1 inch"], StockItemService.SortSizes(["2 inch", "1 inch"]));    // anything else: as given
    }

    [Fact]
    public async Task Creates_every_size_and_colour_once()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int companyId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var zee = new Company { Name = "Zee Shoes" };
            db.Companies.Add(zee);
            await db.SaveChangesAsync();
            companyId = zee.Id;
        }

        var service = new StockItemService(factory, CurrentUser.ForSystem());
        var product = new Product
        {
            CompanyId = companyId, SubCategoryId = 1, Name = "Runner", Unit = "Pair", MaxDiscountPercent = 10, IsActive = true,
        };
        var prices = new StockItemPrices(1500, 2400, 1);

        // 2 colours × 3 sizes, opening stock for two of them.
        var first = new List<StockItemToCreate>();
        foreach (var colour in new[] { "Black", "Brown" })
            foreach (var size in new[] { "7", "8", "9" })
                first.Add(new StockItemToCreate(colour, size, colour == "Black" && size == "8" ? 5 : size == "9" ? 2 : 0));
        var saved = await service.SaveAsync(product, [], first, prices, "ZS-500");
        Assert.Equal(6, saved.Created);
        Assert.Empty(saved.Skipped);
        Assert.Equal("ZS-500", saved.ArticleNo);

        // Adding size 10 later: the existing ones are skipped, only the new size is created.
        product.Id = saved.ProductId;
        var existing = await service.GetStockItemsAsync(saved.ProductId);
        var more = new[] { "Black", "brown" }.SelectMany(c => new[] { "8", "10" }.Select(s => new StockItemToCreate(c, s, 0))).ToList();
        var added = await service.SaveAsync(product, existing, more, prices, "ZS-500");
        Assert.Equal(2, added.Created);
        Assert.Equal(["Black / 8", "brown / 8"], added.Skipped);

        await using (var db = factory.CreateDbContext())
        {
            var runner = await db.Products.Include(p => p.StockItems).SingleAsync(p => p.Name == "Runner");
            Assert.Equal(1, await db.Products.CountAsync());
            Assert.Equal("Runner", runner.Name);
            Assert.Equal("Pair", runner.Unit);
            Assert.Equal(10, runner.MaxDiscountPercent);
            var all = runner.StockItems;
            Assert.Equal(8, all.Count);
            Assert.All(all, v =>
            {
                Assert.Equal(2400, v.SalePrice);
                Assert.Equal(1500, v.PurchasePrice);
                Assert.Matches("^SH[0-9]{6}$", v.Barcode); // generated SH000001…
            });
            var black8 = all.Single(v => v.Colour == "Black" && v.Size == "8");
            var onHand = await StockService.GetOnHandAsync(db, all.Select(v => v.Id));
            Assert.Equal(5, onHand.GetValueOrDefault(black8.Id));
            Assert.Equal(4, all.Where(v => v.Size == "9").Sum(v => onHand.GetValueOrDefault(v.Id)));
            Assert.Equal(9, onHand.Values.Sum());                     // 5 + 2 + 2
        }

        // Editing a size / colour (price, barcode) and a duplicate colour + size.
        existing = await service.GetStockItemsAsync(saved.ProductId);
        var brown7 = existing.Single(v => v.Colour == "Brown" && v.Size == "7");
        brown7.SalePrice = 2600;
        brown7.Barcode = "  ";                                       // empty box keeps the barcode
        await service.SaveAsync(product, existing, [], prices);
        var reloaded = (await service.GetStockItemsAsync(saved.ProductId)).Single(v => v.Id == brown7.Id);
        Assert.Equal(2600, reloaded.SalePrice);
        Assert.Matches("^SH[0-9]{6}$", reloaded.Barcode);
        // One barcode per piece: 3 more Black 7 on a purchase = 3 new stock items, each its own barcode, in order.
        var pieces = await service.AddPiecesAsync(saved.ProductId, "ZS-500", "Black", "7", ProductCodes.ToBarcode(700), 3, prices);
        Assert.Equal(3, pieces.Select(p => p.StockItemId).Distinct().Count());
        Assert.Equal(ProductCodes.ToBarcode(700), pieces[0].Barcode);
        Assert.Equal(3, pieces.Select(p => p.Barcode).Distinct().Count());
        Assert.Equal(4, (await service.GetStockItemsAsync(saved.ProductId)).Count(v => v.Colour == "Black" && v.Size == "7"));
        await Assert.ThrowsAsync<BusinessException>(() => service.AddPiecesAsync(saved.ProductId, "ZS-500", "Black", "7", null, 0, prices));
        await Assert.ThrowsAsync<BusinessException>(() =>                     // the shown barcode is taken now
            service.AddPiecesAsync(saved.ProductId, "ZS-500", "Black", "7", ProductCodes.ToBarcode(700), 1, prices));
        // Its purchase line removed before saving: the unused piece goes again.
        await service.DeleteNewPieceAsync(pieces[2].StockItemId);
        Assert.Equal(3, (await service.GetStockItemsAsync(saved.ProductId)).Count(v => v.Colour == "Black" && v.Size == "7"));
        foreach (var p in pieces.Take(2)) await service.DeleteNewPieceAsync(p.StockItemId);

        // A product master alone (no stock items yet), then one plain stock item: it gets a new automatic article no.
        var master = await service.SaveAsync(new Product { CompanyId = companyId, SubCategoryId = 1, Name = "Polish" }, [], [], new StockItemPrices(0, 0));
        Assert.Equal(0, master.Created);
        Assert.Null(master.ArticleNo);
        Assert.Empty(await service.GetStockItemsAsync(master.ProductId));
        var plain = await service.SaveAsync(new Product { CompanyId = companyId, SubCategoryId = 1, Name = "Laces" }, [], [new StockItemToCreate(null, null, 0)], new StockItemPrices(50, 80));
        Assert.Equal(1, plain.Created);
        Assert.Matches("^ART[0-9]{6}$", plain.ArticleNo);
        // An article no. of another product is refused.
        await Assert.ThrowsAsync<BusinessException>(() => service.SaveAsync(new Product { CompanyId = companyId, SubCategoryId = 1, Name = "Boot" },
            [], [new StockItemToCreate("Black", "8", 0)], prices, "ZS-500"));

        // Deleting: a used stock item (it has opening stock) can't go; an unused one can.
        await Assert.ThrowsAsync<BusinessException>(() => service.DeleteStockItemAsync(black8Id()));
        var black10 = (await service.GetStockItemsAsync(saved.ProductId)).Single(v => v.Colour == "Black" && v.Size == "10");
        await service.DeleteStockItemAsync(black10.Id);
        Assert.Equal(7, (await service.GetStockItemsAsync(saved.ProductId)).Count);
        await Assert.ThrowsAsync<BusinessException>(() => service.DeleteProductAsync(saved.ProductId)); // has stock
        await service.DeleteProductAsync(plain.ProductId);
        await service.DeleteProductAsync(master.ProductId);

        // Rules.
        await Assert.ThrowsAsync<BusinessException>(() => service.SaveAsync(product, [], [new StockItemToCreate("Red", "7", -1)], prices));
        await Assert.ThrowsAsync<BusinessException>(() => service.SaveAsync(new Product { CompanyId = companyId, SubCategoryId = 1, Name = " " }, [], [], prices));
        var noRight = new CurrentUser();
        await Assert.ThrowsAsync<BusinessException>(() => new StockItemService(factory, noRight).SaveAsync(product, [], first, prices));

        int black8Id()
        {
            using var db = factory.CreateDbContext();
            return db.StockItems.Single(v => v.ProductId == saved.ProductId && v.Colour == "Black" && v.Size == "8").Id;
        }
    }

    /// <summary>
    /// The owner's example: article A-001, sizes 40–45, colours Blue and Orange, bought on one purchase
    /// (New Purchase → the item's "+" panel), then sold one pair at a time.
    /// </summary>
    [Fact]
    public async Task Purchase_one_article_in_sizes_and_colours_then_sell_one_by_one()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        int companyId, supplierId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var company = new Company { Name = "Stride" };
            var supplier = new Supplier { Name = "Shoe Mart" };
            db.AddRange(company, supplier);
            await db.SaveChangesAsync();
            (companyId, supplierId) = (company.Id, supplier.Id);
        }

        var system = CurrentUser.ForSystem();
        var variants = new StockItemService(factory, system);
        var purchases = new PurchaseService(factory, system);
        var sales = new SaleService(factory, system);
        var prices = new StockItemPrices(1800, 2500);

        // Purchase panel: each colour + size added one at a time (new ones are created), 3 pairs of each Blue size, 2 of each Orange.
        var sizes = StockItemService.ParseList("40-45");
        var colours = StockItemService.ParseList("Blue, Orange");
        var cells = colours.SelectMany(c => sizes.Select(s => (Colour: c, Size: s, Qty: c == "Blue" ? 3m : 2m))).ToList();
        var productId = (await variants.SaveAsync(new Product { CompanyId = companyId, SubCategoryId = 1, Name = "Jogger", Unit = "Pair" },
            [], [new StockItemToCreate(cells[0].Colour, cells[0].Size, 0)], prices, "A-001")).ProductId;
        var ids = new List<(string Colour, string Size, int StockItemId, bool Created)>();
        foreach (var c in cells)
        {
            var (variantId, created, _, _) = await variants.EnsureOneAsync(productId, "A-001", c.Colour, c.Size, null, prices);
            ids.Add((c.Colour, c.Size, variantId, created));
        }
        Assert.Equal(12, ids.Count);
        Assert.Equal(11, ids.Count(i => i.Created));                       // the first one was made with the product
        Assert.Equal(12, ids.Select(i => i.StockItemId).Distinct().Count());

        await purchases.CreateAsync(new Purchase
        {
            PurchaseDate = DateTime.Today, SupplierId = supplierId, BillNo = "SM-1",
            Lines = cells.Zip(ids, (c, i) => new PurchaseLine { StockItemId = i.StockItemId, Qty = c.Qty, Cost = 1800, SalePrice = Pricing.RetailFromProfit(1800, 50) }).ToList(),
        });

        // Buying the same article again finds the same sizes / colours (nothing new), plus a new size 46.
        var blue41 = await variants.EnsureOneAsync(productId, "A-001", "Blue", "41", null, prices);
        var orange45 = await variants.EnsureOneAsync(productId, "A-001", "orange", "45", null, prices);
        var blue46 = await variants.EnsureOneAsync(productId, "A-001", "Blue", "46", null, prices);
        Assert.Equal([false, false, true], new[] { blue41.Created, orange45.Created, blue46.Created });
        Assert.Equal(ids.Single(i => i.Colour == "Blue" && i.Size == "41").StockItemId, blue41.StockItemId);
        Assert.Equal(ids.Single(i => i.Colour == "Orange" && i.Size == "45").StockItemId, orange45.StockItemId);

        // One article: its sizes / colours in size order.
        var all = await variants.GetStockItemsAsync(productId);
        Assert.Equal(13, all.Count);
        Assert.Equal(["40", "41", "42", "43", "44", "45", "46"], StockItemService.SortSizes(all.Select(v => v.Size)));

        int Id(string colour, string size) => ids.Single(i => i.Colour == colour && i.Size == size).StockItemId;
        async Task<decimal> OnHand(int variantId)
        {
            await using var db = factory.CreateDbContext();
            return (await StockService.GetOnHandAsync(db, [variantId])).GetValueOrDefault(variantId);
        }

        Assert.Equal(3, await OnHand(Id("Blue", "42")));
        // The retail price on the purchase (cost 1,800 + 50% = 2,700) became every size's sale price.
        await using (var db = factory.CreateDbContext())
            Assert.All(await db.StockItems.Where(v => v.ProductId == productId && v.Size != "46").ToListAsync(), v => Assert.Equal(2700, v.SalePrice));
        Assert.Equal(2, await OnHand(Id("Orange", "44")));
        await using (var db = factory.CreateDbContext())
            Assert.Equal(30, (await StockService.GetOnHandAsync(db, ids.Select(i => i.StockItemId))).Values.Sum()); // 6×3 + 6×2

        // Sell Blue 42 one pair at a time: only that size and colour goes down.
        for (var sold = 1; sold <= 3; sold++)
        {
            await sales.CreateAsync(new Sale
            {
                CustomerId = Customer.WalkInId, SaleDate = DateTime.Today, Paid = 2500,
                Lines = [new SaleLine { StockItemId = Id("Blue", "42"), Qty = 1, Rate = 2500 }],
            });
            Assert.Equal(3 - sold, await OnHand(Id("Blue", "42")));
        }
        Assert.Equal(3, await OnHand(Id("Blue", "43")));   // other sizes untouched
        Assert.Equal(2, await OnHand(Id("Orange", "42"))); // other colour untouched

        // Blue 42 is sold out: a 4th pair is refused.
        var soldOut = await Assert.ThrowsAsync<BusinessException>(() => sales.CreateAsync(new Sale
        {
            CustomerId = Customer.WalkInId, SaleDate = DateTime.Today, Paid = 2500,
            Lines = [new SaleLine { StockItemId = Id("Blue", "42"), Qty = 1, Rate = 2500 }],
        }));
        Assert.Contains("Not enough stock", soldOut.Message);
    }

    [Fact]
    public void Article_no_scan_finds_every_variant()
    {
        List<ProductLookup> products =
        [
            new() { Id = 1, ArticleNo = "ZS-500", Colour = "Black", Size = "7" },
            new() { Id = 2, ArticleNo = "ZS-500", Colour = "Black", Size = "8", Barcode = "8900000000008" },
            new() { Id = 3, ArticleNo = "ZS-500", Colour = "Brown", Size = "7" },
            new() { Id = 4, ArticleNo = "ZS-600", Colour = "Black", Size = "7" },
        ];
        Assert.Equal([1, 2, 3], ProductLookup.MatchCode(products, "zs-500").Select(p => p.Id)); // → picker grid
        Assert.Equal([2], ProductLookup.MatchCode(products, "8900000000008").Select(p => p.Id)); // own barcode → that size
    }
}
