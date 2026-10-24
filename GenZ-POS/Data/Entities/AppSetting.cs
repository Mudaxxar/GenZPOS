using System.ComponentModel.DataAnnotations;

namespace GenZPOS.Data.Entities;

/// <summary>Simple key/value settings stored in the database (so they are included in backups), e.g. shop name on invoices.</summary>
public class AppSetting
{
    public const string ShopName = "Shop.Name";
    public const string ShopPhone = "Shop.Phone";
    public const string ShopAddress = "Shop.Address";
    public const string InvoiceFooter = "Invoice.Footer";
    /// <summary>What "Save &amp; Print" prints: Receipt80 (default), Receipt58 or A4 (ShopInfo.PrintFormat).</summary>
    public const string PrintFormat = "Invoice.PrintFormat";
    /// <summary>Shop logo: PNG as base64 (ShopInfo.Logo; prepared by LogoImage). Why Value has no length limit.</summary>
    public const string ShopLogo = "Shop.Logo";
    /// <summary>"true" = print the shop name under the logo too.</summary>
    public const string ShowNameWithLogo = "Shop.ShowNameWithLogo";
    /// <summary>Barcode label layout as JSON (Services/LabelDesign; designed in Settings → Barcode label).</summary>
    public const string LabelDesign = "Label.Design";
    /// <summary>How new products are stocked (StockMode): Piece (one barcode per piece) or Quantity.</summary>
    public const string DefaultStockMode = "Product.DefaultStockMode";
    public const string BackupFolder = "Backup.Folder";
    public const string LastBackupAt = "Backup.LastAt";

    [Key, MaxLength(100)]
    public string Key { get; set; } = "";

    /// <summary>nvarchar(max): most values are short text, the logo is a base64 image.</summary>
    public string? Value { get; set; }
}
