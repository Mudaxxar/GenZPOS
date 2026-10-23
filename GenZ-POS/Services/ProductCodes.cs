using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>
/// Article no. and barcode rules, used by every way a stock item is created (product dialog, purchase panel, Excel
/// import):
/// <list type="bullet">
/// <item>Barcode: unique per stock item (DB unique index). Empty → "SH000001", "SH000002"… from the BarcodeSeq sequence
/// (letters + digits: print labels as Code 128).</item>
/// <item>Article no.: on the stock item; the colours / sizes of one design share it, and it belongs to one product only.
/// A new article → "ART000001"… from the ArticleNoSeq sequence (never a guess by name).</item>
/// </list>
/// Sequences (not "max + 1") so two counters creating items at the same moment never get the same code.
/// </summary>
public static class ProductCodes
{
    public const string BarcodeSequence = "BarcodeSeq";
    public const string ArticleNoSequence = "ArticleNoSeq";

    public const string BarcodePrefix = "SH";
    public const string ArticleNoPrefix = "ART";

    /// <summary>SH000001 … SH999999, then SH1000000 (never cut off).</summary>
    public static string ToBarcode(long number) => $"{BarcodePrefix}{number:D6}";

    /// <summary>ART000001 … ART999999, then ART1000000.</summary>
    public static string ToArticleNo(long number) => $"{ArticleNoPrefix}{number:D6}";

    /// <summary>A barcode no product has (nor any in <paramref name="reserved"/>, e.g. codes typed elsewhere in an import).</summary>
    public static async Task<string> NextBarcodeAsync(AppDbContext db, ISet<string>? reserved = null)
    {
        while (true)
        {
            var code = ToBarcode(await NextValueAsync(db, BarcodeSequence));
            if (reserved?.Contains(code) == true || await db.StockItems.AnyAsync(v => v.Barcode == code)) continue;
            reserved?.Add(code);
            return code;
        }
    }

    public static async Task<string> NextArticleNoAsync(AppDbContext db, ISet<string>? reserved = null)
    {
        while (true)
        {
            var code = ToArticleNo(await NextValueAsync(db, ArticleNoSequence));
            if (reserved?.Contains(code) == true || await db.StockItems.AnyAsync(i => i.ArticleNo == code)) continue;
            reserved?.Add(code);
            return code;
        }
    }

    /// <summary>
    /// The article no. for stock items of a product: the given one after checking no OTHER product's items have it,
    /// or (empty) a new one. <paramref name="productId"/> = the product the items belong to (0 for a new product).
    /// </summary>
    public static async Task<string> ResolveArticleNoAsync(AppDbContext db, string? articleNo, int productId = 0, ISet<string>? reserved = null)
    {
        var typed = string.IsNullOrWhiteSpace(articleNo) ? null : articleNo.Trim();
        if (typed is null)
            return await NextArticleNoAsync(db, reserved);

        var other = await db.StockItems.AsNoTracking()
            .Where(i => i.ArticleNo == typed && i.ProductId != productId)
            .Select(i => new { Company = i.Product!.Company!.Name, i.Product.Name })
            .FirstOrDefaultAsync();
        if (other is not null)
            throw new BusinessException(
                $"Article no. {typed} already belongs to {other.Company} · {other.Name}. Use another one, or leave it empty to get a new one.");
        return typed;
    }

    /// <summary>The barcode for a variant: the typed one after checking it is free, or (empty) a new one.</summary>
    public static async Task<string> ResolveBarcodeAsync(AppDbContext db, string? barcode, int variantId = 0, ISet<string>? reserved = null)
    {
        var typed = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
        if (typed is null)
            return await NextBarcodeAsync(db, reserved);
        if (await db.StockItems.AnyAsync(v => v.Barcode == typed && v.Id != variantId))
            throw new BusinessException($"Barcode {typed} is already used by another size / colour. Leave it empty to get a new one.");
        return typed;
    }

    private static async Task<long> NextValueAsync(AppDbContext db, string sequence) =>
        // Not composed (NEXT VALUE FOR isn't allowed in a sub-query); joins the context's transaction if any.
        (await db.Database.SqlQueryRaw<long>($"SELECT NEXT VALUE FOR dbo.[{sequence}] AS [Value]").ToListAsync()).Single();
}
