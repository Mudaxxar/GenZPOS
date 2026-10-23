using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>One line read from the Excel file = one size / colour (Row = Excel row number, for messages).</summary>
/// <remarks>
/// <see cref="Shade"/> = the Shade (colour) column. <see cref="ClearMaxDiscount"/> = the cell said "none" (remove the
/// limit); a blank cell keeps it. <see cref="OpeningQty"/> = "Opening stock" cell (each colour / size of an expanded
/// article row gets it).
/// </remarks>
public sealed record ProductImportRow(
    int Row, int? Id, string? ArticleNo, string Company, string Category, string SubCategory, string Name, string? Shade,
    string? PackSize, string? Size, string? Unit, string? Barcode, decimal? PurchasePrice, decimal? SalePrice,
    decimal? MaxDiscountPercent, bool ClearMaxDiscount, decimal? ReorderLevel, bool? IsActive, decimal? OpeningQty = null);

public enum ProductImportAction { Add, Update, Unchanged }

/// <param name="StockItemId">The size / colour the row updates (null = a new one).</param>
/// <param name="OpeningQty">
/// Opening stock this import will add (0 = none, or skipped — see <see cref="ProductImportPlan.OpeningSkipped"/>); on a
/// stock import as a purchase (<see cref="ProductImportPlan.ForPurchase"/>) the qty bought (1 per piece).
/// </param>
/// <param name="ChangesProduct">The row changes the product itself (name, category, pack…), not only the stock item.</param>
/// <param name="Cost">Purchase price after the import (the row's, else the stock item's current one).</param>
public sealed record ProductImportLine(ProductImportRow Source, ProductImportAction Action, int? StockItemId, string Changes,
    decimal OpeningQty = 0, bool ChangesProduct = false, decimal Cost = 0);

public sealed record ProductImportIssue(int Row, string Message);

/// <summary>What an import would do. Nothing is saved until <see cref="ProductExcelService.ImportAsync"/>.</summary>
public sealed class ProductImportPlan
{
    public required IReadOnlyList<ProductImportRow> Rows { get; init; }
    public required bool CreateMissing { get; init; }

    /// <summary>
    /// Stock import as a purchase bill (Purchases → Import from Excel): every row is a piece bought (Qty column, default
    /// 1; Qty N without a barcode = N new pieces). Rows without a barcode are always new pieces; pieces that already have
    /// stock history are skipped (listed in <see cref="OpeningSkipped"/>), so a file imported twice never doubles stock.
    /// </summary>
    public bool ForPurchase { get; init; }

    /// <summary>Columns present in the file. A missing column (e.g. an older export) keeps the product's current value.</summary>
    public required IReadOnlySet<string> Columns { get; init; }

    public List<ProductImportLine> Lines { get; } = [];
    public List<ProductImportIssue> Errors { get; } = [];
    public SortedSet<string> NewCompanies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedSet<string> NewSubCategories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Opening stock typed for sizes / colours that already have stock movements: not added (a re-imported file would
    /// double the stock). Such stock comes in with a purchase or Stock In / Out.
    /// </summary>
    public List<ProductImportIssue> OpeningSkipped { get; } = [];

    /// <summary>Total opening stock the import adds, and on how many sizes / colours.</summary>
    public decimal OpeningTotal => Lines.Sum(l => l.OpeningQty);
    public int OpeningCount => Lines.Count(l => l.OpeningQty > 0);

    public int AddCount => Lines.Count(l => l.Action == ProductImportAction.Add);
    public int UpdateCount => Lines.Count(l => l.Action == ProductImportAction.Update);
    public int UnchangedCount => Lines.Count(l => l.Action == ProductImportAction.Unchanged);
    public bool CanImport => Errors.Count == 0 && (ForPurchase ? OpeningCount > 0 : AddCount + UpdateCount > 0);

    /// <summary>Stock import: the bill's sub total (Σ qty × cost).</summary>
    public decimal PurchaseTotal => Lines.Sum(l => l.OpeningQty * l.Cost);
}

/// <summary>
/// Products to / from Excel (.xlsx, ClosedXML). One row = one size / colour (<see cref="StockItem"/>); its
/// article columns (company, category, product, article no., pack, unit, max discount) belong to the
/// <see cref="Product"/> and must be the same on every row of that article. Export writes every size / colour; the same
/// file, edited, imports back: rows with an Id (the size / colour's) update it, rows without one are matched by
/// barcode, then article no. (or, without one, company + product + pack) + shade + size, else added. Blank price /
/// max discount / reorder / active cells keep the current value; a column missing from the file keeps it too. An
/// import is all-or-nothing: any error row stops it.
/// </summary>
public class ProductExcelService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser)
{
    public const string FileFilter = "Excel workbook (*.xlsx)|*.xlsx";
    private const string SheetName = "Products";
    private const int MaxRows = 20000;
    private const int MaxCombinationsPerRow = 400;

    /// <summary>
    /// A row with an article no. whose Shade (colour) or Size cell holds a list ("Blue, Orange") or a whole-number
    /// range ("40-45") stands for every colour × size of that article. Rows without an article no. are never split,
    /// so a paint shade like "White, Matt" stays one size / colour.
    /// </summary>
    public static List<ProductImportRow> Expand(ProductImportRow row)
    {
        if (row.ArticleNo is null || !(IsList(row.Shade) || IsList(row.Size)))
            return [row];
        List<string?> colours = IsList(row.Shade) ? [.. StockItemService.ParseList(row.Shade)] : [row.Shade];
        List<string?> sizes = IsList(row.Size) ? [.. StockItemService.ParseList(row.Size)] : [row.Size];
        return colours.SelectMany(c => sizes.Select(s => row with { Shade = c, Size = s })).ToList();
    }

    private static bool IsList(string? value) =>
        value is not null && (value.IndexOfAny([',', ';']) >= 0 || System.Text.RegularExpressions.Regex.IsMatch(value, @"^\s*\d+\s*-\s*\d+\s*$"));

    private static readonly string[] Headers =
    [
        "Id", "Article no", "Company", "Category", "Sub-category", "Product", "Shade", "Pack size", "Size", "Unit", "Barcode",
        "Purchase price", "Sale price", "Max discount %", "Reorder level", "Active", "Opening stock",
    ];

    private static int Col(string header) => Array.IndexOf(Headers, header) + 1;

    // Header text in the file (lower case, no spaces / dashes) → column. Lets people reorder or rename slightly.
    private static readonly Dictionary<string, string> HeaderAliases = new()
    {
        ["id"] = "Id",
        ["articleno"] = "Article no", ["article"] = "Article no", ["articlenumber"] = "Article no", ["artno"] = "Article no",
        ["articalno"] = "Article no",
        ["company"] = "Company", ["brand"] = "Company",
        ["category"] = "Category",
        ["subcategory"] = "Sub-category",
        ["product"] = "Product", ["name"] = "Product", ["productname"] = "Product",
        ["shade"] = "Shade", ["colour"] = "Shade", ["color"] = "Shade",
        ["packsize"] = "Pack size", ["pack"] = "Pack size",
        ["size"] = "Size",
        ["unit"] = "Unit",
        ["barcode"] = "Barcode",
        ["purchaseprice"] = "Purchase price", ["cost"] = "Purchase price",
        ["saleprice"] = "Sale price", ["price"] = "Sale price",
        ["maxdiscount"] = "Max discount %", ["maximumdiscount"] = "Max discount %", ["maxdiscountpercent"] = "Max discount %",
        ["reorderlevel"] = "Reorder level", ["reorder"] = "Reorder level",
        ["active"] = "Active",
        ["openingstock"] = "Opening stock", ["opening"] = "Opening stock", ["openingqty"] = "Opening stock",
        ["qty"] = "Opening stock", ["quantity"] = "Opening stock",
    };

    // ---------------- export ----------------

    /// <summary>Writes every size / colour (or only the headers when <paramref name="templateOnly"/>) plus help sheets.</summary>
    public async Task ExportAsync(Stream output, bool templateOnly = false)
    {
        currentUser.Demand(Permissions.CatalogManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        var loaded = await db.StockItems.AsNoTracking()
            .Where(v => !templateOnly)
            .OrderBy(v => v.Product!.Company!.Name).ThenBy(v => v.Product!.Name).ThenBy(v => v.Product!.PackSize).ThenBy(v => v.ArticleNo)
            .ThenBy(v => v.Colour).ThenBy(v => v.Id)
            .Select(v => new
            {
                v.Id, v.ProductId, Company = v.Product!.Company!.Name, Category = v.Product.SubCategory!.Category!.Name,
                SubCategory = v.Product.SubCategory.Name, v.Product.Name, v.Colour, v.Product.PackSize, v.Size, v.ArticleNo,
                v.Product.Unit, v.Barcode, v.PurchasePrice, v.SalePrice, v.Product.MaxDiscountPercent, v.ReorderLevel, v.IsActive,
            })
            .ToListAsync();
        // Sizes of one article in size order (40, 41 … 45; S, M, L), not text order (10 before 6).
        var variants = loaded
            .GroupBy(v => (v.ProductId, v.ArticleNo, v.Colour))
            .SelectMany(g =>
            {
                var order = StockItemService.SortSizes(g.Select(v => v.Size));
                return g.OrderBy(v => v.Size is null ? -1 : order.FindIndex(s => string.Equals(s, v.Size.Trim(), StringComparison.OrdinalIgnoreCase)))
                        .ThenBy(v => v.Id);
            })
            .ToList();
        var onHand = templateOnly ? [] : await db.StockMovements.AsNoTracking()
            .GroupBy(m => m.StockItemId)
            .Select(g => new { g.Key, Qty = g.Sum(m => m.QtyIn - m.QtyOut) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty);
        var lists = await db.SubCategories.AsNoTracking()
            .OrderBy(s => s.Category!.Name).ThenBy(s => s.Name)
            .Select(s => new { Category = s.Category!.Name, s.Name })
            .ToListAsync();
        var companyNames = await db.Companies.AsNoTracking().OrderBy(c => c.Name).Select(c => c.Name).ToListAsync();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(SheetName);
        for (var c = 0; c < Headers.Length; c++)
            ws.Cell(1, c + 1).Value = Headers[c];
        // Information only: no import alias, so it is ignored when the file comes back.
        var stockCol = Headers.Length + 1;
        ws.Cell(1, stockCol).Value = "In stock (not imported)";

        var r = 2;
        foreach (var v in variants)
        {
            ws.Cell(r, Col("Id")).Value = v.Id;
            ws.Cell(r, Col("Article no")).Value = v.ArticleNo;
            ws.Cell(r, Col("Company")).Value = v.Company;
            ws.Cell(r, Col("Category")).Value = v.Category;
            ws.Cell(r, Col("Sub-category")).Value = v.SubCategory;
            ws.Cell(r, Col("Product")).Value = v.Name;
            ws.Cell(r, Col("Shade")).Value = v.Colour;
            ws.Cell(r, Col("Pack size")).Value = v.PackSize;
            ws.Cell(r, Col("Size")).Value = v.Size;
            ws.Cell(r, Col("Unit")).Value = v.Unit;
            ws.Cell(r, Col("Barcode")).Value = v.Barcode;
            ws.Cell(r, Col("Purchase price")).Value = v.PurchasePrice;
            ws.Cell(r, Col("Sale price")).Value = v.SalePrice;
            ws.Cell(r, Col("Max discount %")).Value = v.MaxDiscountPercent;
            ws.Cell(r, Col("Reorder level")).Value = v.ReorderLevel;
            ws.Cell(r, Col("Active")).Value = v.IsActive ? "Yes" : "No";
            ws.Cell(r, stockCol).Value = onHand.GetValueOrDefault(v.Id);
            r++;
        }

        var header = ws.Range(1, 1, 1, Headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        ws.Cell(1, stockCol).Style.Font.Bold = true;
        ws.Cell(1, stockCol).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEEEEE");
        ws.Column(stockCol).Style.Font.FontColor = XLColor.Gray;
        ws.Column(stockCol).Style.NumberFormat.Format = "0.##";
        ws.Column(Col("Id")).Style.Font.FontColor = XLColor.Gray;
        // Barcodes and article numbers stay text (no 8.90E+12, no lost leading zeros).
        ws.Column(Col("Barcode")).Style.NumberFormat.Format = "@";
        ws.Column(Col("Article no")).Style.NumberFormat.Format = "@";
        ws.Range(2, Col("Purchase price"), Math.Max(r, 2), Col("Sale price")).Style.NumberFormat.Format = "#,##0.00";
        ws.Column(Col("Max discount %")).Style.NumberFormat.Format = "0.##";
        // Exported empty on purpose: type the qty in the shop for new products (see Help).
        ws.Column(Col("Opening stock")).Style.NumberFormat.Format = "0.##";
        ws.Cell(1, Col("Opening stock")).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2EFDA");
        ws.Range(1, 1, Math.Max(r - 1, 1), stockCol).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(r, 500));

        var help = wb.AddWorksheet("Help");
        string[] lines =
        [
            "How to add or update products with this file",
            "",
            "• One row = one stock item: a product's article no. + colour (Shade) + size, with its own barcode, prices and stock.",
            "• Change a stock item: edit its row. Keep the Id as it is.",
            "• Add a product or a stock item: type a new row and leave the Id empty.",
            "• Company, Category, Sub-category, Product, Pack size, Unit and Max discount % belong to the product:",
            "  give them the same value on every row of one product.",
            "• Article no. and Barcode: leave them empty on a new row and they are made automatically (ART000001…, SH000001…",
            "  for barcodes). A barcode belongs to one stock item only; an article no. to one product (its colours / sizes share it).",
            "  An empty cell never clears an existing code.",
            "• Required: Company, Category, Sub-category, Product. Unit is Pcs when left empty.",
            "• A row without an Id still updates an existing stock item with the same barcode, or the same",
            "  Article no. + shade (colour) + size — or, on a row without an article no., the product with the same",
            "  company + product + pack size and that shade + size.",
            "• Empty Purchase price / Sale price / Max discount % / Reorder level / Active cells keep the current value",
            "  (a new one gets 0 / no discount limit / active).",
            "• Max discount %: the most discount allowed at the till, e.g. 10. 0 = no discount. Type none to remove the limit.",
            "• Active: Yes or No (this size / colour). To stop selling one, set it to No (nothing is deleted by import).",
            "• New companies and sub-categories are created if you tick that option when importing.",
            "• Opening stock: the qty already in the shop. It is added as opening stock for a NEW size / colour, or for one",
            "  that has no stock yet. Ones that already have stock are skipped (so importing a file twice never doubles it);",
            "  for those use a purchase or Stock In / Out. The In stock column is for reading only and is not imported.",
            "",
            "One article in many colours and sizes (shoes, clothes…)",
            "• Every colour + size is its own row (own barcode, stock, price), all with the same Article no.",
            "• Shortcut for adding an article: ONE row with the Article no., colours in Shade and sizes in Size, e.g.",
            "  Shade = Blue, Orange   Size = 40-45   → 12 sizes / colours: Blue 40 … Blue 45, Orange 40 … Orange 45.",
            "  Separate with commas; 40-45 means every whole size from 40 to 45. Leave Id and Barcode empty on such a row.",
            "  Sizes / colours that already exist are updated (e.g. a new price for all), the others are added.",
            "  Opening stock on such a row is for EACH size and colour; for different quantities use one row per size.",
            "• The Articles sheet shows each article's colours, sizes and stock (for reading; it is not imported).",
            "• GenZ-POS shows a preview first; nothing is saved if any row has an error.",
            "",
            "Existing companies and categories are listed on the Lists sheet.",
        ];
        for (var i = 0; i < lines.Length; i++)
            help.Cell(i + 1, 1).Value = lines[i];
        help.Cell(1, 1).Style.Font.Bold = true;
        help.Column(1).Width = 100;

        var listSheet = wb.AddWorksheet("Lists");
        listSheet.Cell(1, 1).Value = "Company";
        listSheet.Cell(1, 3).Value = "Category";
        listSheet.Cell(1, 4).Value = "Sub-category";
        for (var i = 0; i < companyNames.Count; i++)
            listSheet.Cell(i + 2, 1).Value = companyNames[i];
        for (var i = 0; i < lists.Count; i++)
        {
            listSheet.Cell(i + 2, 3).Value = lists[i].Category;
            listSheet.Cell(i + 2, 4).Value = lists[i].Name;
        }
        listSheet.Row(1).Style.Font.Bold = true;
        listSheet.Columns().AdjustToContents();

        // Articles: one row per article no. (of one product) with its colours, sizes and stock.
        var articles = variants
            .GroupBy(v => (v.ProductId, v.ArticleNo, v.Company, v.Name, v.PackSize))
            .OrderBy(g => g.Key.ArticleNo).ThenBy(g => g.Key.Company).ThenBy(g => g.Key.Name)
            .ToList();
        var articleSheet = wb.AddWorksheet("Articles");
        string[] articleHeaders = ["Article no", "Company", "Product", "Pack size", "Colours", "Sizes", "Sizes / colours", "In stock", "Sale price"];
        for (var c = 0; c < articleHeaders.Length; c++)
            articleSheet.Cell(1, c + 1).Value = articleHeaders[c];
        var ar = 2;
        foreach (var g in articles)
        {
            var prices = g.Select(v => v.SalePrice).Distinct().Order().ToList();
            articleSheet.Cell(ar, 1).Value = g.Key.ArticleNo;
            articleSheet.Cell(ar, 2).Value = g.Key.Company;
            articleSheet.Cell(ar, 3).Value = g.Key.Name;
            articleSheet.Cell(ar, 4).Value = g.Key.PackSize;
            articleSheet.Cell(ar, 5).Value = string.Join(", ", g.Select(v => v.Colour).Where(s => s is not null).Distinct(StringComparer.OrdinalIgnoreCase));
            articleSheet.Cell(ar, 6).Value = string.Join(", ", StockItemService.SortSizes(g.Select(v => v.Size)));
            articleSheet.Cell(ar, 7).Value = g.Count();
            articleSheet.Cell(ar, 8).Value = g.Sum(v => onHand.GetValueOrDefault(v.Id));
            articleSheet.Cell(ar, 9).Value = prices.Count == 1 ? $"{prices[0]:N2}" : $"{prices[0]:N2} – {prices[^1]:N2}";
            ar++;
        }
        var articleHeader = articleSheet.Range(1, 1, 1, articleHeaders.Length);
        articleHeader.Style.Font.Bold = true;
        articleHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEEEEE");
        articleSheet.Column(1).Style.NumberFormat.Format = "@";
        articleSheet.Column(8).Style.NumberFormat.Format = "0.##";
        articleSheet.SheetView.FreezeRows(1);
        articleSheet.Columns().AdjustToContents(1, Math.Min(ar, 500));

        ws.SetTabActive();
        await Task.Run(() => wb.SaveAs(output));
    }

    /// <summary>
    /// One purchase bill to Excel: the products sheet layout (same columns as <see cref="ExportAsync"/>, so the file can
    /// be imported again) with one row per bill line — Purchase price = the line's cost, Sale price = the retail set on
    /// the bill (else the piece's current price), "Qty" bought and "Amount" — plus a "Purchase" sheet with the bill.
    /// Returns a file name suggestion.
    /// </summary>
    public async Task<string> ExportPurchaseAsync(int purchaseId, Stream output)
    {
        currentUser.Demand(Permissions.PurchasesManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        var bill = await db.Purchases.AsNoTracking().Include(p => p.Supplier).FirstOrDefaultAsync(p => p.Id == purchaseId)
                   ?? throw new BusinessException("Purchase not found. It may have been deleted.");
        var lines = await db.PurchaseLines.AsNoTracking()
            .Where(l => l.PurchaseId == purchaseId)
            .OrderBy(l => l.Id)
            .Select(l => new
            {
                l.StockItemId, l.StockItem!.ArticleNo, Company = l.StockItem.Product!.Company!.Name,
                Category = l.StockItem.Product.SubCategory!.Category!.Name, SubCategory = l.StockItem.Product.SubCategory.Name,
                l.StockItem.Product.Name, l.StockItem.Colour, l.StockItem.Product.PackSize, l.StockItem.Size, l.StockItem.Product.Unit,
                l.StockItem.Barcode, l.Cost, SalePrice = l.SalePrice ?? l.StockItem.SalePrice, l.StockItem.Product.MaxDiscountPercent,
                l.StockItem.ReorderLevel, l.StockItem.IsActive, l.Qty, l.Amount,
            })
            .ToListAsync();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(SheetName);
        for (var c = 0; c < Headers.Length; c++)
            ws.Cell(1, c + 1).Value = Headers[c];
        ws.Cell(1, Col("Opening stock")).Value = "Qty"; // read back as the qty (Import from Excel on Purchases)
        var amountCol = Headers.Length + 1;
        ws.Cell(1, amountCol).Value = "Amount (not imported)";

        var r = 2;
        foreach (var l in lines)
        {
            ws.Cell(r, Col("Id")).Value = l.StockItemId;
            ws.Cell(r, Col("Article no")).Value = l.ArticleNo;
            ws.Cell(r, Col("Company")).Value = l.Company;
            ws.Cell(r, Col("Category")).Value = l.Category;
            ws.Cell(r, Col("Sub-category")).Value = l.SubCategory;
            ws.Cell(r, Col("Product")).Value = l.Name;
            ws.Cell(r, Col("Shade")).Value = l.Colour;
            ws.Cell(r, Col("Pack size")).Value = l.PackSize;
            ws.Cell(r, Col("Size")).Value = l.Size;
            ws.Cell(r, Col("Unit")).Value = l.Unit;
            ws.Cell(r, Col("Barcode")).Value = l.Barcode;
            ws.Cell(r, Col("Purchase price")).Value = l.Cost;
            ws.Cell(r, Col("Sale price")).Value = l.SalePrice;
            ws.Cell(r, Col("Max discount %")).Value = l.MaxDiscountPercent;
            ws.Cell(r, Col("Reorder level")).Value = l.ReorderLevel;
            ws.Cell(r, Col("Active")).Value = l.IsActive ? "Yes" : "No";
            ws.Cell(r, Col("Opening stock")).Value = l.Qty;
            ws.Cell(r, amountCol).Value = l.Amount;
            r++;
        }
        // No total row here (it would be read back as a product); totals are on the Purchase sheet.
        var header = ws.Range(1, 1, 1, amountCol);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        ws.Column(Col("Id")).Style.Font.FontColor = XLColor.Gray;
        ws.Column(Col("Barcode")).Style.NumberFormat.Format = "@";
        ws.Column(Col("Article no")).Style.NumberFormat.Format = "@";
        ws.Range(2, Col("Purchase price"), r, Col("Sale price")).Style.NumberFormat.Format = "#,##0.00";
        ws.Column(Col("Max discount %")).Style.NumberFormat.Format = "0.##";
        ws.Column(Col("Opening stock")).Style.NumberFormat.Format = "0.##";
        ws.Column(amountCol).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(1, 1, Math.Max(r - 1, 1), amountCol).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(r, 500));

        var info = wb.AddWorksheet("Purchase");
        (string Label, XLCellValue Value)[] facts =
        [
            ("Purchase #", bill.Id), ("Date", bill.PurchaseDate), ("Supplier", bill.Supplier?.Name ?? ""), ("Bill #", bill.BillNo ?? ""),
            ("Pieces / qty", lines.Sum(l => l.Qty)), ("Sub total", bill.SubTotal), ("Discount", bill.Discount), ("Total", bill.Total),
            ("Paid on bill", bill.Paid), ("Pending", bill.Total - bill.Paid), ("Notes", bill.Notes ?? ""),
        ];
        for (var i = 0; i < facts.Length; i++)
        {
            info.Cell(i + 1, 1).Value = facts[i].Label;
            info.Cell(i + 1, 2).Value = facts[i].Value;
        }
        info.Column(1).Style.Font.Bold = true;
        info.Cell(2, 2).Style.DateFormat.Format = "dd/MM/yyyy";
        info.Range(6, 2, 10, 2).Style.NumberFormat.Format = "#,##0.00";
        info.Columns().AdjustToContents();

        ws.SetTabActive();
        await Task.Run(() => wb.SaveAs(output));
        var name = string.Concat($"Purchase {bill.Id} {bill.Supplier?.Name} {bill.PurchaseDate:yyyy-MM-dd}".Split(Path.GetInvalidFileNameChars()));
        return name + ".xlsx";
    }

    // ---------------- import ----------------

    /// <summary>Reads the file and works out what an import would add / change. Saves nothing.</summary>
    /// <param name="forPurchase">Stock import as a purchase bill (see <see cref="ProductImportPlan.ForPurchase"/>).</param>
    public async Task<ProductImportPlan> PreviewAsync(Stream input, bool createMissing, bool forPurchase = false)
    {
        currentUser.Demand(forPurchase ? Permissions.PurchasesManage : Permissions.CatalogManage);
        var (rows, columns, readErrors) = await Task.Run(() => Read(input, forPurchase));
        var plan = new ProductImportPlan { Rows = rows, CreateMissing = createMissing, Columns = columns, ForPurchase = forPurchase };
        plan.Errors.AddRange(readErrors);

        await using var db = await dbFactory.CreateDbContextAsync();
        await AnalyzeAsync(db, plan);
        return plan;
    }

    /// <summary>Applies a preview in one transaction (checked again against the database as it is now).</summary>
    public async Task<(int Added, int Updated)> ImportAsync(ProductImportPlan preview)
    {
        currentUser.Demand(Permissions.CatalogManage);
        if (preview.ForPurchase)
            throw new InvalidOperationException("A stock import is saved with ImportPurchaseAsync.");
        if (preview.Errors.Count > 0)
            throw new BusinessException("The file has errors. Fix them in Excel and import again.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var (plan, _) = await ApplyAsync(db, preview);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        AppLog.Info($"Product import by \"{currentUser.Session?.UserName}\": {plan.AddCount} added, {plan.UpdateCount} updated, " +
                    $"opening stock {plan.OpeningTotal:0.##} on {plan.OpeningCount} size(s) / colour(s)");
        return (plan.AddCount, plan.UpdateCount);
    }

    /// <summary>
    /// Stock from Excel as one purchase bill (owner: "upload it for stock" from a supplier): creates / updates the
    /// products and pieces like <see cref="ImportAsync"/> and saves <paramref name="bill"/> (supplier, date, bill no.,
    /// paid, notes) with one line per piece bought (cost = Purchase price, retail = Sale price) — all in one transaction.
    /// Returns the new purchase id.
    /// </summary>
    public async Task<int> ImportPurchaseAsync(ProductImportPlan preview, Purchase bill)
    {
        currentUser.Demand(Permissions.PurchasesManage);
        // New companies / sub-categories or changed products are catalogue changes.
        if (preview.NewCompanies.Count > 0 || preview.NewSubCategories.Count > 0 || preview.Lines.Any(l => l.ChangesProduct))
            currentUser.Demand(Permissions.CatalogManage);
        if (!preview.ForPurchase)
            throw new InvalidOperationException("Preview the file with forPurchase: true.");
        if (preview.Errors.Count > 0)
            throw new BusinessException("The file has errors. Fix them in Excel and import again.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var (plan, items) = await ApplyAsync(db, preview);
        if (plan.OpeningCount == 0)
            throw new BusinessException("Nothing to buy: every piece in the file is already in stock.");
        await db.SaveChangesAsync();

        bill.Lines = plan.Lines
            .Select((line, i) => (line, item: items[i]))
            .Where(x => x.line.OpeningQty > 0 && x.item is not null)
            .Select(x => new PurchaseLine
            {
                StockItemId = x.item!.Id, Qty = x.line.OpeningQty, Cost = x.item.PurchasePrice, SalePrice = x.line.Source.SalePrice,
            })
            .ToList();
        var id = await PurchaseService.CreateInAsync(db, bill);
        await tx.CommitAsync();
        AppLog.Info($"Stock import by \"{currentUser.Session?.UserName}\" as purchase #{id}: {plan.OpeningTotal:0.##} piece(s), " +
                    $"{plan.AddCount} new, {plan.OpeningSkipped.Count} skipped (already in stock)");
        return id;
    }

    /// <summary>
    /// Checks the preview again against the database as it is now (tracked) and applies it to <paramref name="db"/>
    /// without saving. Returns the fresh plan and, per plan line, its stock item (null for an unchanged line of a
    /// product import).
    /// </summary>
    private async Task<(ProductImportPlan Plan, List<StockItem?> Items)> ApplyAsync(AppDbContext db, ProductImportPlan preview)
    {
        var plan = new ProductImportPlan
        {
            Rows = preview.Rows, CreateMissing = preview.CreateMissing, Columns = preview.Columns, ForPurchase = preview.ForPurchase,
        };
        var (companies, subs, targets) = await AnalyzeAsync(db, plan, tracking: true);
        if (plan.Errors.Count > 0)
            throw new BusinessException($"Products changed since the preview: {plan.Errors[0].Message} (row {plan.Errors[0].Row}). Please import again.");
        var items = new List<StockItem?>();

        // New stock items get an article no. (typed, or the next one: shared by the product's other new rows without one
        // in this file) and their own barcode (ProductCodes).
        var newProducts = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        var newArticles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // product key → its automatic article no.
        var reservedBarcodes = plan.Rows.Where(r => r.Barcode is not null).Select(r => r.Barcode!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reservedArticles = plan.Rows.Where(r => r.ArticleNo is not null).Select(r => r.ArticleNo!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < plan.Lines.Count; i++)
        {
            var line = plan.Lines[i];
            var target = targets[i];
            if (line.Action == ProductImportAction.Unchanged)
            {
                items.Add(target.StockItem);
                continue;
            }
            var s = line.Source;

            var company = companies.GetValueOrDefault(s.Company);
            if (company is null)
            {
                company = new Company { Name = s.Company };
                db.Companies.Add(company);
                companies[s.Company] = company;
            }

            var subKey = SubKey(s.Category, s.SubCategory);
            var sub = subs.GetValueOrDefault(subKey);
            if (sub is null)
            {
                var category = subs.Values.Select(x => x.Category!).FirstOrDefault(c => Same(c.Name, s.Category))
                               ?? await db.Categories.FirstOrDefaultAsync(c => c.Name == s.Category)
                               ?? db.Categories.Add(new Category { Name = s.Category }).Entity;
                sub = new SubCategory { Category = category, Name = s.SubCategory };
                db.SubCategories.Add(sub);
                subs[subKey] = sub;
            }

            var product = target.Product;
            if (product is null && !newProducts.TryGetValue(target.NewKey!, out product))
            {
                product = new Product();
                db.Products.Add(product);
                newProducts[target.NewKey!] = product;
            }
            product.Company = company;
            product.SubCategory = sub;
            ApplyProduct(s, product, plan.Columns);

            var item = target.StockItem;
            if (item is null)
            {
                var productKey = target.Product is { } p ? $"P{p.Id}" : target.NewKey!;
                if (target.ArticleNo is null && !newArticles.ContainsKey(productKey))
                    newArticles[productKey] = await ProductCodes.NextArticleNoAsync(db, reservedArticles);
                item = new StockItem { ArticleNo = target.ArticleNo ?? newArticles[productKey] };
                product.StockItems.Add(item);
            }
            ApplyStockItem(s, item, plan.Columns);
            if (string.IsNullOrEmpty(item.Barcode))
                item.Barcode = await ProductCodes.NextBarcodeAsync(db, reservedBarcodes);
            items.Add(item);

            // Opening stock in the same transaction as the stock item (stock = Σ StockMovements). A stock import's qty
            // comes in with its purchase bill instead.
            if (line.OpeningQty > 0 && !plan.ForPurchase)
                db.StockMovements.Add(new StockMovement
                {
                    StockItem = item, MovementDate = DateTime.Today, Type = StockMovementType.Opening,
                    QtyIn = line.OpeningQty, Notes = "Opening stock (Excel import)",
                });
        }
        return (plan, items);
    }

    // ---------------- reading ----------------

    private static (List<ProductImportRow> Rows, IReadOnlySet<string> Columns, List<ProductImportIssue> Errors) Read(Stream input, bool forPurchase)
    {
        var rows = new List<ProductImportRow>();
        var errors = new List<ProductImportIssue>();
        var columns = new Dictionary<string, int>();

        XLWorkbook wb;
        try
        {
            wb = new XLWorkbook(input);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not open Excel file for product import", ex);
            throw new BusinessException("This file can't be read as an Excel workbook (.xlsx). Save it from Excel as .xlsx and try again.");
        }

        using (wb)
        {
            var ws = wb.Worksheets.FirstOrDefault(w => Same(w.Name, SheetName)) ?? wb.Worksheets.First();
            foreach (var cell in ws.Row(1).CellsUsed())
            {
                var key = new string(cell.GetString().Where(char.IsLetter).ToArray()).ToLowerInvariant();
                if (HeaderAliases.TryGetValue(key, out var name))
                    columns.TryAdd(name, cell.Address.ColumnNumber);
            }
            var missing = new[] { "Company", "Category", "Sub-category", "Product" }.Where(h => !columns.ContainsKey(h)).ToList();
            if (missing.Count > 0)
                throw new BusinessException(
                    $"The first row of sheet \"{ws.Name}\" must have the column titles. Missing: {string.Join(", ", missing)}. " +
                    "Use \"Export to Excel\" or \"Empty template\" to get a file in the right layout.");

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow - 1 > MaxRows)
                throw new BusinessException($"The file has {lastRow - 1} rows; import at most {MaxRows} at a time.");

            for (var r = 2; r <= lastRow; r++)
            {
                var row = ws.Row(r);
                if (row.IsEmpty()) continue;

                var rowErrors = new List<string>();
                string? Text(string col)
                {
                    if (!columns.TryGetValue(col, out var c)) return null;
                    var v = row.Cell(c).Value;
                    // Numbers typed as numbers (barcode 8901234567890, shade 8045) must not come back as "8.90123E+12".
                    var t = v.IsNumber ? v.GetNumber().ToString("0.##########", CultureInfo.InvariantCulture) : row.Cell(c).GetFormattedString();
                    return t.Trim() is { Length: > 0 } s ? s : null;
                }
                decimal? Number(string col)
                {
                    if (!columns.TryGetValue(col, out var c)) return null;
                    var v = row.Cell(c).Value;
                    if (v.IsBlank) return null;
                    if (v.IsNumber) return Math.Round((decimal)v.GetNumber(), 2);
                    var t = row.Cell(c).GetFormattedString().Replace("Rs", "", StringComparison.OrdinalIgnoreCase).Replace(",", "").Trim();
                    if (t.Length == 0) return null;
                    if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return Math.Round(d, 2);
                    rowErrors.Add($"{col} \"{t}\" is not a number");
                    return null;
                }
                // Max discount: a number 0–100 (a cell formatted as % holds 0.1 for 10%), "none" removes the limit.
                var clearMaxDiscount = false;
                decimal? Percent(string col)
                {
                    if (!columns.TryGetValue(col, out var c)) return null;
                    var t = Text(col);
                    if (t is null) return null;
                    if (t.ToLowerInvariant() is "none" or "no limit" or "-")
                    {
                        clearMaxDiscount = true;
                        return null;
                    }
                    var cell = row.Cell(c);
                    decimal? value = cell.Value.IsNumber && cell.Style.NumberFormat.Format.Contains('%')
                        ? Math.Round((decimal)cell.Value.GetNumber() * 100, 2)
                        : decimal.TryParse(t.TrimEnd('%').Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? Math.Round(d, 2) : null;
                    if (value is null) rowErrors.Add($"{col} \"{t}\" is not a number (or none)");
                    else if (value is < 0 or > 100) rowErrors.Add($"{col} must be between 0 and 100");
                    return value;
                }
                bool? YesNo(string col)
                {
                    var t = Text(col)?.ToLowerInvariant();
                    switch (t)
                    {
                        case null: return null;
                        case "yes" or "y" or "true" or "1" or "active": return true;
                        case "no" or "n" or "false" or "0" or "inactive": return false;
                        default: rowErrors.Add($"Active must be Yes or No, not \"{t}\""); return null;
                    }
                }

                int? id = null;
                if (Number("Id") is { } idValue)
                {
                    if (idValue <= 0 || idValue != Math.Floor(idValue)) rowErrors.Add("Id must be a whole number (or empty for a new product)");
                    else id = (int)idValue;
                }

                var maxDiscount = Percent("Max discount %");
                var item = new ProductImportRow(r, id, Text("Article no"),
                    Text("Company") ?? "", Text("Category") ?? "", Text("Sub-category") ?? "", Text("Product") ?? "",
                    Text("Shade"), Text("Pack size"), Text("Size"), Text("Unit"), Text("Barcode"),
                    Number("Purchase price"), Number("Sale price"), maxDiscount, clearMaxDiscount,
                    Number("Reorder level"), YesNo("Active"), Number("Opening stock"));

                // One row for a whole article: "Blue, Orange" × "40-45" → a product per colour + size.
                var expanded = Expand(item);
                if (expanded.Count > 1)
                {
                    if (item.Id is not null)
                        rowErrors.Add("Leave the Id empty on a row with several colours / sizes (or use one row per size)");
                    if (item.Barcode is not null)
                        rowErrors.Add("Leave the barcode empty on a row with several colours / sizes; each size gets its own barcode later");
                    if (expanded.Count > MaxCombinationsPerRow)
                        rowErrors.Add($"{expanded.Count} colour / size combinations; at most {MaxCombinationsPerRow} on one row");
                }

                // Stock import: one barcode per piece, so Qty N (no barcode) = N new pieces of qty 1.
                if (forPurchase)
                {
                    var qty = item.OpeningQty ?? 1;
                    if (qty < 1 || qty != Math.Floor(qty))
                        rowErrors.Add("Qty must be a whole number, 1 or more (one row or one count per piece)");
                    else if (qty > 1 && (item.Id is not null || item.Barcode is not null))
                        rowErrors.Add($"Qty {qty:0} with a barcode: one barcode is one piece. Leave Id and Barcode empty to get {qty:0} new barcodes, or use one row per piece");
                    else if (qty * expanded.Count > MaxCombinationsPerRow)
                        rowErrors.Add($"{qty * expanded.Count:0} pieces on one row; at most {MaxCombinationsPerRow}");
                    else
                        expanded = expanded.SelectMany(e => Enumerable.Repeat(e with { OpeningQty = 1 }, (int)qty)).ToList();
                }

                Require(item.Company, "Company", 100);
                Require(item.Category, "Category", 100);
                Require(item.SubCategory, "Sub-category", 100);
                Require(item.Name, "Product", 150);
                foreach (var shade in expanded.Select(e => e.Shade).Distinct()) Limit(shade, "Shade", 60);
                Limit(item.PackSize, "Pack size", 40);
                foreach (var size in expanded.Select(e => e.Size).Distinct()) Limit(size, "Size", 40);
                Limit(item.ArticleNo, "Article no", 50);
                Limit(item.Unit, "Unit", 20);
                Limit(item.Barcode, "Barcode", 50);
                if (item.PurchasePrice < 0 || item.SalePrice < 0 || item.ReorderLevel < 0 || item.OpeningQty < 0)
                    rowErrors.Add("Prices, reorder level and opening stock can't be negative");

                if (rowErrors.Count > 0) errors.Add(new ProductImportIssue(r, string.Join("; ", rowErrors.Distinct())));
                else rows.AddRange(expanded);

                void Require(string value, string col, int max)
                {
                    if (value.Length == 0) rowErrors.Add($"{col} is empty");
                    else Limit(value, col, max);
                }
                void Limit(string? value, string col, int max)
                {
                    if (value?.Length > max) rowErrors.Add($"{col} is longer than {max} characters");
                }
            }
        }

        if (rows.Count == 0 && errors.Count == 0)
            throw new BusinessException("The file has no product rows under the title row.");
        return (rows, columns.Keys.ToHashSet(), errors);
    }

    // ---------------- matching ----------------

    /// <summary>
    /// Where a plan line goes: an existing product (or the key of a new one in this file), an existing stock item (or a
    /// new one) and, for a new one, the article no. typed on the row (null = a new automatic ART… no.).
    /// </summary>
    private sealed record ImportTarget(Product? Product, string? NewKey, StockItem? StockItem, string? ArticleNo);

    /// <summary>
    /// Matches every row to a product and a stock item of it and fills <paramref name="plan"/>. Returns the loaded
    /// companies / sub-categories and one target per plan line (same order).
    /// </summary>
    private static async Task<(Dictionary<string, Company> Companies, Dictionary<string, SubCategory> Subs, List<ImportTarget> Targets)>
        AnalyzeAsync(AppDbContext db, ProductImportPlan plan, bool tracking = false)
    {
        IQueryable<Product> productQuery = db.Products.Include(p => p.Company).Include(p => p.SubCategory!).ThenInclude(s => s.Category)
            .Include(p => p.StockItems).AsSplitQuery();
        IQueryable<Company> companyQuery = db.Companies;
        IQueryable<SubCategory> subQuery = db.SubCategories.Include(s => s.Category);
        if (!tracking)
        {
            productQuery = productQuery.AsNoTracking();
            companyQuery = companyQuery.AsNoTracking();
            subQuery = subQuery.AsNoTracking();
        }

        var products = await productQuery.ToListAsync();
        var productOf = new Dictionary<int, Product>();   // stock item id → its product
        var items = new Dictionary<int, StockItem>();
        foreach (var p in products)
            foreach (var i in p.StockItems)
            {
                productOf[i.Id] = p;
                items[i.Id] = i;
            }
        // Only needed when the file has opening stock (or is a stock import): stock items that already have any stock movement.
        var withStock = plan.ForPurchase || plan.Rows.Any(r => r.OpeningQty > 0)
            ? (await db.StockMovements.Select(m => m.StockItemId).Distinct().ToListAsync()).ToHashSet()
            : [];
        var companies = (await companyQuery.ToListAsync()).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var subs = (await subQuery.ToListAsync()).ToDictionary(s => SubKey(s.Category!.Name, s.Name), StringComparer.OrdinalIgnoreCase);
        var byBarcode = items.Values.GroupBy(i => i.Barcode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Article no. → the product its stock items belong to (one article no. = one product).
        var byArticle = items.Values.GroupBy(i => i.ArticleNo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => productOf[g.First().Id], StringComparer.OrdinalIgnoreCase);
        // A product by company + name + pack (several products may share a name, e.g. two packs).
        var byName = products.GroupBy(p => NameKey(p.Company!.Name, p.Name, p.PackSize))
            .ToDictionary(g => g.Key, g => g.ToList());
        // A file without a Size column (an older export) matches without size; two sizes of one item then need the Id.
        var useSize = plan.Columns.Contains("Size");

        var targets = new List<ImportTarget>();
        var seenItems = new Dictionary<int, int>();                                      // stock item id → first row
        var seenNewKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // new stock item key → first row
        var seenBarcodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileArticles = new Dictionary<string, (string Product, int Row)>(StringComparer.OrdinalIgnoreCase); // article no. → product
        // Product columns per product (existing id or new key): the first row's values, which the others must repeat.
        var productValues = new Dictionary<string, (int Row, Dictionary<string, string> Values)>(StringComparer.OrdinalIgnoreCase);
        var describedProducts = new HashSet<int>();

        foreach (var s in plan.Rows)
        {
            void Error(string message) => plan.Errors.Add(new ProductImportIssue(s.Row, message));

            // Which existing stock item (and product) is this row?
            StockItem? item = null;
            Product? product = null;
            string? newKey = null;
            if (s.Id is { } id)
            {
                if (!items.TryGetValue(id, out item))
                {
                    Error($"There is no product with Id {id}. Clear the Id to add it as a new product.");
                    continue;
                }
                product = productOf[id];
            }
            else if (s.Barcode is not null && byBarcode.TryGetValue(s.Barcode, out item))
            {
                product = productOf[item.Id];
            }

            if (product is not null)
            {
                // Its article no. may change, but not to one of another product.
                if (s.ArticleNo is not null && byArticle.TryGetValue(s.ArticleNo, out var other) && other.Id != product.Id)
                {
                    Error($"{(s.Id is not null ? $"Id {s.Id}" : $"Barcode {s.Barcode}")} is {product.DisplayName}; " +
                          $"article no. {s.ArticleNo} belongs to {other.DisplayName}. A stock item can't move to another product.");
                    continue;
                }
            }
            else
            {
                var pack = plan.Columns.Contains("Pack size") ? s.PackSize : null;
                if (s.ArticleNo is not null && byArticle.TryGetValue(s.ArticleNo, out var holder))
                {
                    // Found by its article no.: the row must be that product (a rename needs the Id or barcode).
                    if (NameKey(holder.Company!.Name, holder.Name, plan.Columns.Contains("Pack size") ? holder.PackSize : null) != NameKey(s.Company, s.Name, pack))
                    {
                        Error($"Article no. {s.ArticleNo} already belongs to {holder.DisplayName}. Use another one, or leave it empty for a new one.");
                        continue;
                    }
                    product = holder;
                }
                else if (byName.TryGetValue(NameKey(s.Company, s.Name, s.PackSize), out var named))
                {
                    if (named.Count > 1)
                    {
                        Error($"{named.Count} products have this company, name and pack size. Put the Id or an article no. of one of them in this row.");
                        continue;
                    }
                    product = named[0];
                }
                else
                {
                    newKey = "NEW\u001f" + NameKey(s.Company, s.Name, s.PackSize);
                }

                // One barcode per piece (owner): a barcode GenZ-POS doesn't know is a new piece, even when its article has
                // that colour + size already; so is every row without a barcode in a stock import.
                if (product is not null && s.Barcode is null && !plan.ForPurchase)
                {
                    // Its stock item of this article (or, without an article no., any article) + colour + size.
                    var same = product.StockItems
                        .Where(i => (s.ArticleNo is null || Same(i.ArticleNo, s.ArticleNo)) && Same(i.Colour, s.Shade) && (!useSize || Same(i.Size, s.Size)))
                        .ToList();
                    if (same.Count > 1)
                    {
                        Error($"{product.DisplayName} has {same.Count} stock items like this ({string.Join(", ", same.Select(i => i.ArticleNo).Distinct())}). " +
                              "Put the Id or the article no. in this row.");
                        continue;
                    }
                    item = same.FirstOrDefault();
                }
            }

            // Every row of one product must agree on the product columns; an article no. belongs to one product.
            var productId = product is not null ? $"P{product.Id}" : newKey!;
            if (!productValues.TryGetValue(productId, out var first))
                productValues[productId] = first = (s.Row, new Dictionary<string, string>());
            var conflict = ProductColumns(s, plan.Columns)
                .FirstOrDefault(c => first.Values.TryGetValue(c.Column, out var seen) && !Same(seen, c.Value));
            if (conflict.Column is not null)
            {
                Error($"{conflict.Column} \"{conflict.Value}\" differs from row {first.Row} of the same product. " +
                      "All rows of one product need the same company, category, product, pack size, unit and max discount.");
                continue;
            }
            foreach (var (column, value) in ProductColumns(s, plan.Columns))
                first.Values.TryAdd(column, value);
            if (s.ArticleNo is not null)
            {
                if (fileArticles.TryGetValue(s.ArticleNo, out var owner) && owner.Product != productId)
                {
                    Error($"Article no. {s.ArticleNo} is also on row {owner.Row} for another product (one article no. = one product).");
                    continue;
                }
                fileArticles.TryAdd(s.ArticleNo, (productId, s.Row));
            }

            if (item is not null)
            {
                if (seenItems.TryGetValue(item.Id, out var firstRow)) { Error($"Same product as row {firstRow}."); continue; }
                seenItems[item.Id] = s.Row;
            }
            else if (!(plan.ForPurchase && s.Barcode is null)) // stock import: each row without a barcode is another piece
            {
                // Pieces with different barcodes may share article + colour + size.
                var key = productId + "\u001f" + StockItemService.Key(s.ArticleNo, s.Shade, useSize ? s.Size : null) + "\u001f" + s.Barcode;
                if (seenNewKeys.TryGetValue(key, out var firstRow)) { Error($"Same product as row {firstRow}."); continue; }
                seenNewKeys[key] = s.Row;
            }

            if (s.Barcode is not null)
            {
                if (seenBarcodes.TryGetValue(s.Barcode, out var firstRow)) { Error($"Barcode {s.Barcode} is also on row {firstRow}."); continue; }
                seenBarcodes[s.Barcode] = s.Row;
                if (byBarcode.TryGetValue(s.Barcode, out var holderItem) && holderItem.Id != item?.Id)
                {
                    Error($"Barcode {s.Barcode} already belongs to another product ({productOf[holderItem.Id].DisplayName} · {holderItem.VariantName}).");
                    continue;
                }
            }

            // Company and sub-category must exist, or be created when allowed.
            var companyExists = companies.ContainsKey(s.Company);
            var subExists = subs.ContainsKey(SubKey(s.Category, s.SubCategory));
            if (!plan.CreateMissing && (!companyExists || !subExists))
            {
                Error(!companyExists
                    ? $"Company \"{s.Company}\" doesn't exist. Add it first, or tick \"create missing companies and sub-categories\"."
                    : $"Sub-category \"{s.Category} › {s.SubCategory}\" doesn't exist. Add it first, or tick \"create missing companies and sub-categories\".");
                continue;
            }
            if (!companyExists) plan.NewCompanies.Add(s.Company);
            if (!subExists) plan.NewSubCategories.Add($"{s.Category} › {s.SubCategory}");

            // Changes to an existing product are listed (and applied) once, on its first changed row.
            var productChanges = product is not null && !describedProducts.Contains(product.Id) ? DescribeProduct(s, product, plan.Columns) : "";
            if (productChanges.Length > 0) describedProducts.Add(product!.Id);

            var opening = s.OpeningQty ?? (plan.ForPurchase ? 1 : 0);
            var changesProduct = productChanges.Length > 0;
            if (item is null)
            {
                plan.Lines.Add(new ProductImportLine(s, ProductImportAction.Add, null, productChanges, opening, changesProduct, s.PurchasePrice ?? 0));
                targets.Add(new ImportTarget(product, newKey, null, s.ArticleNo));
                continue;
            }

            // Opening stock (or a stock import's piece) only for a stock item with no stock history yet; otherwise a second
            // import would double it.
            if (opening > 0 && withStock.Contains(item.Id))
            {
                plan.OpeningSkipped.Add(new ProductImportIssue(s.Row, plan.ForPurchase
                    ? $"{item.ArticleNo} · {item.Barcode} · {item.VariantName}: already in stock or bought before — not bought again."
                    : $"{product!.DisplayName} · {item.VariantName}: already has stock — opening stock {opening:0.##} not added (use a purchase or Stock In / Out)."));
                opening = 0;
            }
            var changes = string.Join("; ", new[] { productChanges, DescribeStockItem(s, item, plan.Columns) }.Where(c => c.Length > 0));
            if (opening > 0 && !plan.ForPurchase)
                changes = string.Join("; ", new[] { changes, $"Opening stock +{opening:0.##}" }.Where(c => c.Length > 0));
            plan.Lines.Add(new ProductImportLine(s, changes.Length == 0 ? ProductImportAction.Unchanged : ProductImportAction.Update,
                item.Id, changes, opening, changesProduct, s.PurchasePrice ?? item.PurchasePrice));
            targets.Add(new ImportTarget(product, null, item, null));
        }

        // One article row expands to many stock items: report each problem once per Excel row.
        var distinct = plan.Errors.Distinct().OrderBy(e => e.Row).ToList();
        plan.Errors.Clear();
        plan.Errors.AddRange(distinct);
        return (companies, subs, targets);
    }

    /// <summary>The row's values of the columns that belong to the product (blank / missing ones are left out).</summary>
    private static IEnumerable<(string Column, string Value)> ProductColumns(ProductImportRow s, IReadOnlySet<string> columns)
    {
        yield return ("Company", s.Company);
        yield return ("Category", $"{s.Category} › {s.SubCategory}");
        yield return ("Product", s.Name);
        if (columns.Contains("Pack size")) yield return ("Pack size", s.PackSize ?? "");
        if (s.Unit is not null) yield return ("Unit", s.Unit);
        if (s.ClearMaxDiscount) yield return ("Max discount %", "none");
        else if (s.MaxDiscountPercent is { } max) yield return ("Max discount %", max.ToString("0.##", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Copies the row's product columns onto the product. A blank unit / max discount keeps the product's value (a new
    /// one: Pcs / no limit); a pack column missing from the file keeps it too.
    /// </summary>
    private static void ApplyProduct(ProductImportRow s, Product p, IReadOnlySet<string> columns)
    {
        p.Name = s.Name;
        if (columns.Contains("Pack size")) p.PackSize = s.PackSize;
        p.Unit = s.Unit ?? (p.Id == 0 && string.IsNullOrEmpty(p.Unit) ? "Pcs" : p.Unit);
        p.MaxDiscountPercent = s.ClearMaxDiscount ? null : s.MaxDiscountPercent ?? p.MaxDiscountPercent;
        if (p.Id == 0) p.IsActive = true;
    }

    /// <summary>
    /// Copies the row onto the stock item. Blank numbers / active keep its value (a new one: 0 / active); a colour or
    /// size column missing from the file keeps it. The article no. and barcode are never cleared by a blank cell.
    /// </summary>
    private static void ApplyStockItem(ProductImportRow s, StockItem i, IReadOnlySet<string> columns)
    {
        if (s.ArticleNo is not null) i.ArticleNo = s.ArticleNo;
        if (columns.Contains("Shade")) i.Colour = s.Shade;
        if (columns.Contains("Size")) i.Size = s.Size;
        if (s.Barcode is not null) i.Barcode = s.Barcode;
        i.PurchasePrice = s.PurchasePrice ?? i.PurchasePrice;
        i.SalePrice = s.SalePrice ?? i.SalePrice;
        i.ReorderLevel = s.ReorderLevel ?? i.ReorderLevel;
        i.IsActive = s.IsActive ?? (i.Id == 0 || i.IsActive);
    }

    /// <summary>"Company "A" → "B"; Max discount none → 10%" — the product's changes; empty when the row changes nothing.</summary>
    private static string DescribeProduct(ProductImportRow s, Product p, IReadOnlySet<string> columns)
    {
        var parts = new List<string>();
        void Text(string label, string? before, string? after)
        {
            if (!string.Equals(before ?? "", after ?? "", StringComparison.Ordinal))
                parts.Add($"{label} \"{before}\" → \"{after}\"");
        }

        if (!Same(p.Company!.Name, s.Company)) Text("Company", p.Company.Name, s.Company);
        if (!Same(p.SubCategory!.Category!.Name, s.Category) || !Same(p.SubCategory.Name, s.SubCategory))
            Text("Category", $"{p.SubCategory.Category.Name} › {p.SubCategory.Name}", $"{s.Category} › {s.SubCategory}");
        Text("Product", p.Name, s.Name);
        if (columns.Contains("Pack size")) Text("Pack", p.PackSize, s.PackSize);
        if (s.Unit is not null) Text("Unit", p.Unit, s.Unit);
        var maxDiscount = s.ClearMaxDiscount ? null : s.MaxDiscountPercent ?? p.MaxDiscountPercent;
        if (maxDiscount != p.MaxDiscountPercent)
            parts.Add($"Max discount {(p.MaxDiscountPercent is { } b ? $"{b:0.##}%" : "none")} → {(maxDiscount is { } a ? $"{a:0.##}%" : "none")}");
        return string.Join("; ", parts);
    }

    /// <summary>"Sale price 1,300.00 → 1,350.00; Active Yes → No" — the stock item's changes; empty when none.</summary>
    private static string DescribeStockItem(ProductImportRow s, StockItem i, IReadOnlySet<string> columns)
    {
        var parts = new List<string>();
        void Text(string label, string? before, string? after)
        {
            if (!string.Equals(before ?? "", after ?? "", StringComparison.Ordinal))
                parts.Add($"{label} \"{before}\" → \"{after}\"");
        }
        void Money(string label, decimal before, decimal? after)
        {
            if (after is { } a && a != before) parts.Add($"{label} {before:N2} → {a:N2}");
        }

        if (s.ArticleNo is not null) Text("Article no", i.ArticleNo, s.ArticleNo);
        if (columns.Contains("Shade")) Text("Shade", i.Colour, s.Shade);
        if (columns.Contains("Size")) Text("Size", i.Size, s.Size);
        if (s.Barcode is not null) Text("Barcode", i.Barcode, s.Barcode);
        Money("Cost", i.PurchasePrice, s.PurchasePrice);
        Money("Price", i.SalePrice, s.SalePrice);
        Money("Reorder", i.ReorderLevel, s.ReorderLevel);
        if (s.IsActive is { } active && active != i.IsActive) parts.Add($"Active {(i.IsActive ? "Yes" : "No")} → {(active ? "Yes" : "No")}");
        return string.Join("; ", parts);
    }

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    private static string SubKey(string category, string sub) => $"{category.Trim()}\u001f{sub.Trim()}";

    /// <summary>A product by name: company + product name + pack size.</summary>
    private static string NameKey(string company, string name, string? pack) =>
        string.Join('\u001f', company.Trim(), name.Trim(), pack?.Trim() ?? "").ToUpperInvariant();
}
