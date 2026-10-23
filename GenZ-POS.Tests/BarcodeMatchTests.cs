using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>What a scanned code finds (BarcodeScanBox on New Sale, New Purchase, Item History).</summary>
public class BarcodeMatchTests
{
    private static readonly List<ProductLookup> Products =
    [
        new() { Id = 1, DisplayName = "Shoes Black", Barcode = "8901234567890", ArticleNo = "ZS-5" },
        new() { Id = 2, DisplayName = "Shoes Brown", Barcode = "8901234567891", ArticleNo = "ZS-5" },
        new() { Id = 3, DisplayName = "Brush 2 inch", Barcode = null, ArticleNo = "BR-2" },
        new() { Id = 4, DisplayName = "Odd", Barcode = "BR-2", ArticleNo = null },
    ];

    [Fact]
    public void Barcode_first_then_article_no()
    {
        Assert.Equal([1], ProductLookup.MatchCode(Products, " 8901234567890 ").Select(p => p.Id)); // scanner padding trimmed
        Assert.Equal([4], ProductLookup.MatchCode(Products, "br-2").Select(p => p.Id));             // case-insensitive
        Assert.Equal([4], ProductLookup.MatchCode(Products, "BR-2").Select(p => p.Id));             // a barcode wins over an article no.
        Assert.Equal([1, 2], ProductLookup.MatchCode(Products, "zs-5").Select(p => p.Id));         // shared article no. → both (box asks to pick)
        Assert.Empty(ProductLookup.MatchCode(Products, "nothing"));
    }
}
