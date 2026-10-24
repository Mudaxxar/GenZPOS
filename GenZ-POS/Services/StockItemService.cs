using System.Globalization;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>One stock item (colour + size of an article) to create; opening stock may be 0.</summary>
/// <param name="Barcode">The barcode to give it (typed / shown on screen first); null = the next SH… no.</param>
public sealed record StockItemToCreate(string? Colour, string? Size, decimal OpeningQty, string? Barcode = null);

/// <summary>Prices and reorder level a new stock item starts with.</summary>
public sealed record StockItemPrices(decimal PurchasePrice, decimal SalePrice, decimal ReorderLevel = 0);

/// <summary>What <see cref="StockItemService.SaveAsync"/> did.</summary>
/// <param name="ArticleNo">The article no. the new stock items got (null when none were created).</param>
/// <param name="Skipped">Colours / sizes asked for that the article already had ("Black / 7").</param>
public sealed record ProductSaveResult(int ProductId, string? ArticleNo, int Created, List<string> Skipped);

/// <summary>An article of a product (for the purchase article picker): its no., colours, sizes and how many stock items.</summary>
public sealed record ArticleSummary(string ArticleNo, List<string> Colours, List<string> Sizes, int Items)
{
    public string Text => string.Join(" · ", new[]
    {
        ArticleNo,
        Colours.Count > 0 ? string.Join(", ", Colours) : null,
        Sizes.Count > 0 ? string.Join(", ", Sizes) : null,
    }.Where(s => s is not null));
}

/// <summary>
/// Products and their stock items. A <see cref="Product"/> is the master (company, category, name, pack, unit, max
/// discount); each <see cref="StockItem"/> = article no. + colour + size with its own barcode, prices and stock.
/// Sales, purchases, returns and stock movements point at the stock item. One article no. belongs to one product;
/// within it each colour + size is there once.
/// </summary>
public class StockItemService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser)
{
    private const int MaxCombinations = 400;

    /// <summary>
    /// "6, 7, 8" / "S,M,L" / "6-10" (whole-number range) → distinct, trimmed values in typed order.
    /// Empty → an empty list (no sizes / colours).
    /// </summary>
    public static List<string> ParseList(string? text)
    {
        var values = new List<string>();
        foreach (var raw in (text ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = raw.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 2 && int.TryParse(range[0], out var from) && int.TryParse(range[1], out var to) && to > from && to - from <= 50)
                values.AddRange(Enumerable.Range(from, to - from + 1).Select(i => i.ToString(CultureInfo.InvariantCulture)));
            else
                values.Add(raw);
        }
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Shoe / clothing sizes in a sensible order: all numbers → numeric; XS…XXXL → that order; else as given.
    /// </summary>
    public static List<string> SortSizes(IEnumerable<string?> sizes)
    {
        var list = sizes.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.All(s => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
            return list.OrderBy(s => decimal.Parse(s, CultureInfo.InvariantCulture)).ToList();
        string[] apparel = ["XXS", "XS", "S", "M", "L", "XL", "XXL", "XXXL", "2XL", "3XL", "4XL"];
        if (list.All(s => apparel.Contains(s, StringComparer.OrdinalIgnoreCase)))
            return list.OrderBy(s => Array.FindIndex(apparel, a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase))).ToList();
        return list;
    }

    /// <summary>A product's stock items, oldest first.</summary>
    public async Task<List<StockItem>> GetStockItemsAsync(int productId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.StockItems.AsNoTracking().Where(i => i.ProductId == productId).OrderBy(i => i.Id).ToListAsync();
    }

    /// <summary>A product's articles with their colours and sizes, newest article first.</summary>
    public async Task<List<ArticleSummary>> GetArticlesAsync(int productId)
    {
        var items = await GetStockItemsAsync(productId);
        return items.GroupBy(i => i.ArticleNo, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(i => i.Id))
            .Select(g => new ArticleSummary(g.Key,
                g.Select(i => i.Colour).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                SortSizes(g.Select(i => i.Size)), g.Count()))
            .ToList();
    }

    /// <summary>
    /// Saves a product (new: <c>Id = 0</c>), changes to its existing stock items (<paramref name="edited"/>, matched by
    /// Id) and new ones (<paramref name="toCreate"/>, priced with <paramref name="prices"/>) under article
    /// <paramref name="articleNo"/> (null = a new ART… no.; combinations that article already has are skipped).
    /// One transaction.
    /// </summary>
    public async Task<ProductSaveResult> SaveAsync(Product product, IReadOnlyList<StockItem> edited,
        IReadOnlyList<StockItemToCreate> toCreate, StockItemPrices prices, string? articleNo = null)
    {
        currentUser.Demand(Permissions.CatalogManage);
        if (product.CompanyId <= 0 || product.SubCategoryId <= 0 || string.IsNullOrWhiteSpace(product.Name))
            throw new BusinessException("Company, category, sub-category and product name are required.");
        if (edited.Any(i => i.PurchasePrice < 0 || i.SalePrice < 0 || i.ReorderLevel < 0) ||
            prices.PurchasePrice < 0 || prices.SalePrice < 0 || prices.ReorderLevel < 0)
            throw new BusinessException("Prices and reorder level can't be negative.");
        CheckLengths(edited.Select(i => (i.Colour, i.Size)).Concat(toCreate.Select(i => (i.Colour, i.Size))));

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

        Product entity;
        if (product.Id == 0)
        {
            entity = new Product();
            db.Products.Add(entity);
        }
        else
        {
            entity = await db.Products.Include(p => p.StockItems).FirstOrDefaultAsync(p => p.Id == product.Id)
                     ?? throw new BusinessException("This product no longer exists.");
        }

        entity.CompanyId = product.CompanyId;
        entity.SubCategoryId = product.SubCategoryId;
        entity.Name = product.Name.Trim();
        entity.PackSize = Clean(product.PackSize);
        entity.Unit = string.IsNullOrWhiteSpace(product.Unit) ? "Pcs" : product.Unit.Trim();
        entity.MaxDiscountPercent = product.MaxDiscountPercent is { } max ? Math.Clamp(Math.Round(max, 2), 0, 100) : null;
        entity.IsActive = product.IsActive;
        entity.StockMode = product.StockMode;
        entity.PurchaseUnit = product.StockMode == StockMode.Quantity ? Clean(product.PurchaseUnit) : null;
        entity.PurchaseUnitQty = entity.PurchaseUnit is null ? null : product.PurchaseUnitQty is decimal q && q > 0 ? Math.Round(q, 3) : null;
        if (entity.PurchaseUnit is not null && entity.PurchaseUnitQty is null)
            throw new BusinessException($"How many {entity.Unit} are in one {entity.PurchaseUnit}? (e.g. 50 kg in a bag)");

        // Existing stock items: colour, size, barcode, prices, active.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var priceChanges = new List<string>();
        foreach (var change in edited)
        {
            var item = entity.StockItems.FirstOrDefault(x => x.Id == change.Id)
                       ?? throw new BusinessException("A stock item of this product was deleted meanwhile. Open the product again.");
            item.Colour = Clean(change.Colour);
            item.Size = Clean(change.Size);
            // An empty barcode box keeps the current one (every stock item must have one).
            if (Clean(change.Barcode) is { } barcode && !string.Equals(barcode, item.Barcode, StringComparison.OrdinalIgnoreCase))
                item.Barcode = await ProductCodes.ResolveBarcodeAsync(db, barcode, item.Id, reserved);
            if (item.SalePrice != change.SalePrice || item.PurchasePrice != change.PurchasePrice)
                priceChanges.Add($"{item.Barcode} {item.VariantName}: price {item.SalePrice:N2} → {change.SalePrice:N2}" +
                                 (item.PurchasePrice != change.PurchasePrice ? $", cost {item.PurchasePrice:N2} → {change.PurchasePrice:N2}" : ""));
            item.PurchasePrice = change.PurchasePrice;
            item.SalePrice = change.SalePrice;
            item.ReorderLevel = change.ReorderLevel;
            item.IsActive = change.IsActive;
        }

        string? article = null;
        var (created, skipped) = (0, new List<string>());
        if (toCreate.Count > 0)
        {
            article = await ProductCodes.ResolveArticleNoAsync(db, articleNo, entity.Id);
            (created, skipped) = await AddItemsAsync(db, entity, article, toCreate, prices, reserved);
        }
        if (priceChanges.Count > 0)
            Audit.Add(db, currentUser, Audit.PriceChanged, $"{entity.Name}: " + string.Join("; ", priceChanges), entity.Id == 0 ? null : entity.Id);

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        if (created > 0)
            AppLog.Info($"\"{currentUser.Session?.UserName}\" saved \"{entity.Name}\" with {created} new stock item(s), article {article}");
        return new ProductSaveResult(entity.Id, created > 0 ? article : null, created, skipped);
    }

    /// <summary>
    /// Purchase panel, one colour + size at a time: the stock item of that colour + size of article
    /// <paramref name="articleNo"/> (null = start a new article: the next ART… no.), created if it doesn't exist yet with
    /// <paramref name="barcode"/> (the barcode shown on screen; null = the next one) and the purchase's prices. Allowed
    /// with the purchase right too, since receiving new items is part of buying stock.
    /// </summary>
    public async Task<(int StockItemId, bool Created, string Barcode, string ArticleNo)> EnsureOneAsync(
        int productId, string? articleNo, string? colour, string? size, string? barcode, StockItemPrices prices)
    {
        if (!currentUser.Has(Permissions.PurchasesManage))
            currentUser.Demand(Permissions.CatalogManage);
        CheckLengths([(colour, size)]);

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var product = await db.Products.Include(p => p.StockItems).FirstOrDefaultAsync(p => p.Id == productId)
                      ?? throw new BusinessException("This product no longer exists.");

        var article = await ProductCodes.ResolveArticleNoAsync(db, articleNo, productId);
        var key = Key(article, colour, size);
        if (product.StockItems.FirstOrDefault(i => string.Equals(Key(i.ArticleNo, i.Colour, i.Size), key, StringComparison.OrdinalIgnoreCase)) is { } found)
            return (found.Id, false, found.Barcode, found.ArticleNo);

        await AddItemsAsync(db, product, article, [new StockItemToCreate(colour, size, 0, barcode)], prices, new HashSet<string>());
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        var item = product.StockItems.Single(i => string.Equals(Key(i.ArticleNo, i.Colour, i.Size), key, StringComparison.OrdinalIgnoreCase));
        AppLog.Info($"\"{currentUser.Session?.UserName}\" added stock item \"{item.VariantName}\" ({article}, {item.Barcode}) to \"{product.Name}\" on a purchase");
        return (item.Id, true, item.Barcode, item.ArticleNo);
    }

    /// <summary>
    /// Purchase panel (owner: one barcode per piece): <paramref name="qty"/> NEW stock items of article
    /// <paramref name="articleNo"/> + colour + size, each with its own barcode — the first gets
    /// <paramref name="firstBarcode"/> (the one shown on screen; null = the next), the others the next SH… nos. —
    /// and the purchase's prices. Returns them in order. Allowed with the purchase right too.
    /// </summary>
    public async Task<List<(int StockItemId, string Barcode)>> AddPiecesAsync(
        int productId, string articleNo, string? colour, string? size, string? firstBarcode, int qty, StockItemPrices prices)
    {
        if (!currentUser.Has(Permissions.PurchasesManage))
            currentUser.Demand(Permissions.CatalogManage);
        CheckLengths([(colour, size)]);
        if (qty < 1)
            throw new BusinessException("Quantity must be at least 1.");
        if (qty > MaxCombinations)
            throw new BusinessException($"Add at most {MaxCombinations} pieces at a time.");
        if (prices.PurchasePrice < 0 || prices.SalePrice < 0 || prices.ReorderLevel < 0)
            throw new BusinessException("Prices and reorder level can't be negative.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId)
                      ?? throw new BusinessException("This product no longer exists.");
        var article = await ProductCodes.ResolveArticleNoAsync(db, articleNo, productId);

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<StockItem>();
        for (var n = 0; n < qty; n++)
        {
            var item = new StockItem
            {
                ProductId = productId,
                ArticleNo = article,
                Colour = Clean(colour),
                Size = Clean(size),
                Barcode = n == 0 && Clean(firstBarcode) is { } shown
                    ? await ProductCodes.ResolveBarcodeAsync(db, shown, reserved: reserved)
                    : await ProductCodes.NextBarcodeAsync(db, reserved),
                PurchasePrice = prices.PurchasePrice,
                SalePrice = prices.SalePrice,
                ReorderLevel = prices.ReorderLevel,
            };
            reserved.Add(item.Barcode);
            items.Add(item);
        }
        db.StockItems.AddRange(items);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        AppLog.Info($"\"{currentUser.Session?.UserName}\" added {qty} piece(s) {Describe(colour, size)} of \"{product.Name}\" ({article}) on a purchase: " +
                    string.Join(", ", items.Select(i => i.Barcode)));
        return items.Select(i => (i.Id, i.Barcode)).ToList();
    }

    /// <summary>
    /// Deletes a product with all its stock items. Refused (friendly message) when any of them has stock movements or
    /// is on a document: mark it inactive instead.
    /// </summary>
    public async Task DeleteProductAsync(int productId)
    {
        currentUser.Demand(Permissions.CatalogManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var product = await db.Products.Include(p => p.StockItems).FirstOrDefaultAsync(p => p.Id == productId)
                      ?? throw new BusinessException("This product no longer exists.");
        var ids = product.StockItems.Select(i => i.Id).ToList();
        if (await IsUsedAsync(db, ids))
            throw new BusinessException($"\"{product.Name}\" has stock or sales / purchase history, so it can't be deleted. Mark it inactive instead.");
        db.StockItems.RemoveRange(product.StockItems);
        db.Products.Remove(product);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Deletes one stock item that has never been used (no stock movements, not on any document).</summary>
    public async Task DeleteStockItemAsync(int stockItemId)
    {
        currentUser.Demand(Permissions.CatalogManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var item = await db.StockItems.FirstOrDefaultAsync(i => i.Id == stockItemId)
                   ?? throw new BusinessException("This stock item no longer exists.");
        if (await IsUsedAsync(db, [stockItemId]))
            throw new BusinessException($"{Describe(item.Colour, item.Size) ?? "This item"} ({item.Barcode}) has stock or sales / purchase history, so it can't be deleted. Mark it inactive instead.");
        db.StockItems.Remove(item);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// Purchase screen: a piece made by "+" whose line was removed before saving goes again (purchase right is enough).
    /// Does nothing when it has been used meanwhile (stock movements or a document line).
    /// </summary>
    public async Task DeleteNewPieceAsync(int stockItemId)
    {
        if (!currentUser.Has(Permissions.PurchasesManage))
            currentUser.Demand(Permissions.CatalogManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (await IsUsedAsync(db, [stockItemId])) return;
        await db.StockItems.Where(i => i.Id == stockItemId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    private static async Task<bool> IsUsedAsync(AppDbContext db, List<int> ids) =>
        await db.StockMovements.AnyAsync(m => ids.Contains(m.StockItemId)) ||
        await db.PurchaseLines.AnyAsync(l => ids.Contains(l.StockItemId)) ||
        await db.SaleLines.AnyAsync(l => ids.Contains(l.StockItemId));

    /// <summary>Adds the colours / sizes the article doesn't have yet (with opening stock); returns how many and the skipped ones.</summary>
    private static async Task<(int Created, List<string> Skipped)> AddItemsAsync(AppDbContext db, Product product, string articleNo,
        IReadOnlyList<StockItemToCreate> items, StockItemPrices prices, ISet<string> reserved)
    {
        if (items.Count > MaxCombinations)
            throw new BusinessException($"That is {items.Count} combinations; create at most {MaxCombinations} at a time.");
        if (items.Any(i => i.OpeningQty < 0))
            throw new BusinessException("Opening stock can't be negative.");

        var existing = product.StockItems.Select(i => Key(i.ArticleNo, i.Colour, i.Size)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        var skipped = new List<string>();
        foreach (var i in items)
        {
            var colour = Clean(i.Colour);
            var size = Clean(i.Size);
            if (!existing.Add(Key(articleNo, colour, size)))
            {
                skipped.Add(Describe(colour, size) ?? "plain");
                continue;
            }

            var item = new StockItem
            {
                ArticleNo = articleNo,
                Colour = colour,
                Size = size,
                Barcode = i.Barcode is { } shown
                    ? await ProductCodes.ResolveBarcodeAsync(db, shown, reserved: reserved)
                    : await ProductCodes.NextBarcodeAsync(db, reserved),
                PurchasePrice = prices.PurchasePrice,
                SalePrice = prices.SalePrice,
                ReorderLevel = prices.ReorderLevel,
            };
            product.StockItems.Add(item);
            if (i.OpeningQty > 0)
                db.StockMovements.Add(new StockMovement
                {
                    StockItem = item, MovementDate = DateTime.Today, Type = StockMovementType.Opening,
                    QtyIn = i.OpeningQty, Notes = "Opening stock",
                });
            created++;
        }
        return (created, skipped);
    }

    private static void CheckLengths(IEnumerable<(string? Colour, string? Size)> items)
    {
        foreach (var (colour, size) in items)
        {
            if (colour?.Trim().Length > 60)
                throw new BusinessException("A colour is longer than 60 characters.");
            if (size?.Trim().Length > 40)
                throw new BusinessException("A size is longer than 40 characters.");
        }
    }

    /// <summary>"Black / 42", "Black", "42", or null for a plain item.</summary>
    private static string? Describe(string? colour, string? size) =>
        string.Join(" / ", new[] { colour, size }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } s ? s : null;

    /// <summary>Colour + size of one article (case / spaces ignored by the callers' comparers).</summary>
    public static string Key(string? colour, string? size) => $"{colour?.Trim()}\u001f{size?.Trim()}";

    public static string Key(string? articleNo, string? colour, string? size) => $"{articleNo?.Trim()}\u001f{colour?.Trim()}\u001f{size?.Trim()}";

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
