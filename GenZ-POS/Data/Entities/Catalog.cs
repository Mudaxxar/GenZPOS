using System.ComponentModel.DataAnnotations;

namespace GenZPOS.Data.Entities;

/// <summary>Brand / manufacturer, e.g. Zee Shoes, Berger.</summary>
public class Company
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = "";

    [MaxLength(30)]
    public string? Phone { get; set; }

    [MaxLength(250)]
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public List<Product> Products { get; set; } = [];
}

/// <summary>Top-level product group, e.g. Shoes, Emulsion.</summary>
public class Category
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = "";

    public List<SubCategory> SubCategories { get; set; } = [];
}

/// <summary>Second-level group under a category, e.g. Shoes → Sports.</summary>
public class SubCategory
{
    public int Id { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Select a category")]
    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = "";

    public List<Product> Products { get; set; } = [];
}

/// <summary>
/// A product master, e.g. "Zee Shoes · Shoes" or "Berger · Weather Shield · Gallon": company, category, name, pack,
/// unit, max discount. What is bought, sold and stocked are its <see cref="StockItems"/> (article no. + colour + size,
/// each with its own barcode, prices and stock).
/// </summary>
public class Product
{
    public int Id { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Select a company")]
    public int CompanyId { get; set; }
    public Company? Company { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Select a sub-category")]
    public int SubCategoryId { get; set; }
    public SubCategory? SubCategory { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = "";

    /// <summary>Pack size, e.g. "Quarter", "Gallon", "Box of 12" (optional).</summary>
    [MaxLength(40)]
    public string? PackSize { get; set; }

    /// <summary>Counting unit for stock, e.g. "Pcs", "Pair".</summary>
    [Required, MaxLength(20)]
    public string Unit { get; set; } = "Pcs";

    /// <summary>
    /// Most discount allowed at the till, as % of a stock item's sale price (rate cut + share of the invoice discount).
    /// Null = no limit, 0 = no discount. Rule: <c>Services/DiscountRules.cs</c>.
    /// </summary>
    [Range(0, 100)]
    public decimal? MaxDiscountPercent { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Piece = one barcode per piece (shoes, clothes: buying 3 makes 3 stock items, each sold once, qty 1).
    /// Quantity = one stock item bought and sold again and again in any qty (rice by the kg, sugar, oil, paint tins).
    /// New products start with the shop's default (Settings).
    /// </summary>
    public StockMode StockMode { get; set; } = StockMode.Piece;

    /// <summary>Quantity products: the unit bought in, e.g. "Bag" (null = bought in <see cref="Unit"/>).</summary>
    [MaxLength(20)]
    public string? PurchaseUnit { get; set; }

    /// <summary>Quantity products: <see cref="Unit"/>s in one <see cref="PurchaseUnit"/>, e.g. 50 (kg in a bag).</summary>
    public decimal? PurchaseUnitQty { get; set; }

    /// <summary>True when bought in a bigger unit than sold (Bag of 50 kg).</summary>
    public bool HasPurchaseUnit => StockMode == StockMode.Quantity && !string.IsNullOrWhiteSpace(PurchaseUnit) && PurchaseUnitQty > 0;

    public List<StockItem> StockItems { get; set; } = [];

    /// <summary>"Zee Shoes · Shoes" (+ pack size).</summary>
    public string DisplayName =>
        string.Join(" · ", new[] { Company?.Name, Name, PackSize }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// One stock item of a product: article no. (the design, ART000001…; several colours / sizes share it) + colour + size,
/// with its own unique barcode (SH000001…) and prices. The thing that is bought, sold and stocked:
/// stock = Σ StockMovements of this item.
/// </summary>
public class StockItem
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Article no. (automatic, ProductCodes). One article no. belongs to one product.</summary>
    [Required, MaxLength(50)]
    public string ArticleNo { get; set; } = "";

    /// <summary>Colour / shade, e.g. "Black", "Brilliant White" (optional).</summary>
    [MaxLength(60)]
    public string? Colour { get; set; }

    /// <summary>Size, e.g. "42", "M", "2 inch" (optional).</summary>
    [MaxLength(40)]
    public string? Size { get; set; }

    /// <summary>Unique barcode (automatic SH000001… unless one is typed / scanned).</summary>
    [Required, MaxLength(50)]
    public string Barcode { get; set; } = "";

    /// <summary>Last purchase cost per unit; updated when a purchase is saved.</summary>
    [Range(0, double.MaxValue)]
    public decimal PurchasePrice { get; set; }

    /// <summary>Retail (sale) price.</summary>
    [Range(0, double.MaxValue)]
    public decimal SalePrice { get; set; }

    /// <summary>Show as low stock when on-hand qty is at or below this.</summary>
    [Range(0, double.MaxValue)]
    public decimal ReorderLevel { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>"Black · 42".</summary>
    public string VariantName =>
        string.Join(" · ", new[] { Colour, Size }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>"Zee Shoes · Shoes · Black · 42" — needs <see cref="Product"/> (and its Company) loaded.</summary>
    public string DisplayName =>
        string.Join(" · ", new[] { Product?.Company?.Name, Product?.Name, Product?.PackSize, Colour, Size }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>How a product is stocked (owner 2026-10-24). Stored as int — never renumber.</summary>
public enum StockMode
{
    /// <summary>One barcode per piece (shoes, clothes): qty 1 per stock item.</summary>
    Piece = 0,
    /// <summary>One barcode for the item, any qty bought / sold many times (rice per kg, sugar, oil).</summary>
    Quantity = 1,
}
