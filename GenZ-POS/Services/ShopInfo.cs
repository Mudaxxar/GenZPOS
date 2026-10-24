using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>
/// Shop details shown on invoices / statements / header, stored in the Settings table (so they're in backups).
/// Singleton: loaded at startup, refreshed when the Settings page saves.
/// </summary>
public class ShopInfo(IDbContextFactory<AppDbContext> dbFactory)
{
    public string Name { get; private set; } = "GenZ-POS";
    public string? Phone { get; private set; }
    public string? Address { get; private set; }
    public string? InvoiceFooter { get; private set; }

    /// <summary>What invoices print on by default (Save &amp; Print, the invoice's Print button).</summary>
    public PrintFormat PrintFormat { get; private set; } = PrintFormat.Receipt80;

    /// <summary>Shop logo as PNG (prepared by <see cref="LogoImage"/>), or null. Printed at the top of receipts / invoices.</summary>
    public byte[]? Logo { get; private set; }

    /// <summary>The logo as a data URI for &lt;img src&gt; on screen (null when there is none).</summary>
    public string? LogoDataUri { get; private set; }

    /// <summary>Print the shop name under the logo too (off by default: most logos already show the name).</summary>
    public bool ShowNameWithLogo { get; private set; }

    /// <summary>Whether the shop name is printed: always without a logo, with one only when asked.</summary>
    public bool PrintName => Logo is null || ShowNameWithLogo;

    /// <summary>The barcode label layout (Settings → Barcode label); the default layout until one is saved.</summary>
    public LabelDesign LabelDesign { get; private set; } = LabelDesign.Default();

    /// <summary>Raised after a reload so open screens (e.g. the header) can re-render.</summary>
    public event Action? Changed;

    public async Task LoadAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var map = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        Name = map.GetValueOrDefault(AppSetting.ShopName) is { Length: > 0 } n ? n : "GenZ-POS";
        Phone = Blank(map.GetValueOrDefault(AppSetting.ShopPhone));
        Address = Blank(map.GetValueOrDefault(AppSetting.ShopAddress));
        InvoiceFooter = Blank(map.GetValueOrDefault(AppSetting.InvoiceFooter));
        PrintFormat = Enum.TryParse<PrintFormat>(map.GetValueOrDefault(AppSetting.PrintFormat), out var format) ? format : PrintFormat.Receipt80;
        ShowNameWithLogo = map.GetValueOrDefault(AppSetting.ShowNameWithLogo) == "true";
        Logo = null;
        if (Blank(map.GetValueOrDefault(AppSetting.ShopLogo)) is { } b64)
        {
            try { Logo = Convert.FromBase64String(b64); }
            catch (FormatException ex) { AppLog.Warn("The saved shop logo is damaged; it is ignored.", ex); }
        }
        LogoDataUri = Logo is null ? null : "data:image/png;base64," + Convert.ToBase64String(Logo);
        DefaultStockMode = Enum.TryParse<StockMode>(map.GetValueOrDefault(AppSetting.DefaultStockMode), out var mode) ? mode : StockMode.Piece;
        LabelDesign = LabelDesign.FromJson(map.GetValueOrDefault(AppSetting.LabelDesign)) ?? LabelDesign.Default(withShopName: Logo is null);
        Changed?.Invoke();
    }

    /// <summary>How new products are stocked (Settings → Products): Piece (shoes) or Quantity (rice per kg).</summary>
    public StockMode DefaultStockMode { get; private set; } = StockMode.Piece;

    public async Task SaveDefaultStockModeAsync(StockMode mode)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await SetAsync(db, AppSetting.DefaultStockMode, mode.ToString());
        await db.SaveChangesAsync();
        await LoadAsync();
    }

    /// <summary>Saves the barcode label layout (null = back to the default layout).</summary>
    public async Task SaveLabelDesignAsync(LabelDesign? design)
    {
        if (design is not null && (design.WidthMm is < 10 or > 300 || design.HeightMm is < 10 or > 600))
            throw new BusinessException("Label size must be 10–300 mm wide and 10–600 mm high.");
        await using var db = await dbFactory.CreateDbContextAsync();
        await SetAsync(db, AppSetting.LabelDesign, design?.ToJson());
        await db.SaveChangesAsync();
        await LoadAsync();
    }

    /// <summary>Saves the logo (a prepared PNG, see <see cref="LogoImage.Prepare"/>), or removes it (null).</summary>
    public async Task SaveLogoAsync(byte[]? png)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await SetAsync(db, AppSetting.ShopLogo, png is null ? null : Convert.ToBase64String(png));
        await db.SaveChangesAsync();
        await LoadAsync();
    }

    public async Task SaveAsync(string name, string? phone, string? address, string? footer, PrintFormat printFormat, bool showNameWithLogo)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessException("Shop name is required.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await SetAsync(db, AppSetting.ShopName, name.Trim());
        await SetAsync(db, AppSetting.ShopPhone, Blank(phone));
        await SetAsync(db, AppSetting.ShopAddress, Blank(address));
        await SetAsync(db, AppSetting.InvoiceFooter, Blank(footer));
        await SetAsync(db, AppSetting.PrintFormat, printFormat.ToString());
        await SetAsync(db, AppSetting.ShowNameWithLogo, showNameWithLogo ? "true" : "false");
        await db.SaveChangesAsync();
        await LoadAsync();
    }

    private static async Task SetAsync(AppDbContext db, string key, string? value)
    {
        var row = await db.Settings.FindAsync(key);
        if (row is null) db.Settings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Paper an invoice is printed on: a thermal receipt roll (80 mm / 58 mm) or an A4 / A5 page.</summary>
public enum PrintFormat
{
    Receipt80 = 0,
    Receipt58 = 1,
    A4 = 2,
}
