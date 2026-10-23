using System.Drawing;
using System.Drawing.Printing;
using GenZPOS.Components;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>
/// Prints a sale receipt straight to a thermal receipt printer with GDI (<see cref="PrintDocument"/>), no dialog — the
/// owner's proven way (their earlier PrintToken class): every line is drawn at a known position and the page ends after
/// the last line, so a roll printer feeds exactly the receipt. Same content as the on-screen receipt
/// (Shared/SaleReceipt.razor). Units: 1/100 inch (GDI default for printing).
/// </summary>
public static class ReceiptPrinter
{
    /// <summary>Printers installed on this PC.</summary>
    public static IReadOnlyList<string> InstalledPrinters()
    {
        try { return PrinterSettings.InstalledPrinters.Cast<string>().OrderBy(n => n).ToList(); }
        catch (Exception ex)
        {
            AppLog.Warn("Could not list printers", ex);
            return [];
        }
    }

    /// <summary>The Windows default printer's name (null if none).</summary>
    public static string? DefaultPrinter()
    {
        try { return new PrinterSettings().PrinterName is { Length: > 0 } n ? n : null; }
        catch { return null; }
    }

    /// <summary>
    /// Prints <paramref name="sale"/> (lines with StockItem → Product → Company loaded, and Customer) on
    /// <paramref name="printerName"/> (null = Windows default printer). <paramref name="paperMm"/>: 80 or 58.
    /// Returns null when sent to the printer, else a message for the user.
    /// </summary>
    /// <param name="toFile">Print into this file instead of on paper (e.g. with "Microsoft Print to PDF"; for checks).</param>
    public static string? Print(Sale sale, decimal previousBalance, ShopInfo shop, string? printerName, int paperMm, string? toFile = null)
    {
        try
        {
            using var doc = new PrintDocument();
            doc.DocumentName = $"Receipt {sale.Id}";
            doc.PrintController = new StandardPrintController(); // no "Printing…" pop-up
            if (!string.IsNullOrWhiteSpace(printerName))
                doc.PrinterSettings.PrinterName = printerName;
            if (toFile is not null)
            {
                doc.PrinterSettings.PrintToFile = true;
                doc.PrinterSettings.PrintFileName = toFile;
            }
            if (!doc.PrinterSettings.IsValid)
                return $"Printer \"{doc.PrinterSettings.PrinterName}\" was not found. Choose the receipt printer again.";
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            doc.OriginAtMargins = false;

            var layout = new Layout(sale, previousBalance, shop, paperMm);
            doc.PrintPage += (_, e) =>
            {
                layout.Draw(e.Graphics!);
                e.HasMorePages = false;
            };
            doc.Print();
            AppLog.Info($"Receipt #{sale.Id} printed on \"{doc.PrinterSettings.PrinterName}\" ({paperMm} mm)");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Printing receipt #{sale.Id} failed", ex);
            return $"The receipt could not be printed: {ex.Message}";
        }
    }

    /// <summary>Draws the receipt top to bottom, moving <see cref="y"/> down after each line.</summary>
    private sealed class Layout(Sale sale, decimal previousBalance, ShopInfo shop, int paperMm)
    {
        // Printable width: 80 mm roll ≈ 72 mm (≈ 2.83 in), 58 mm roll ≈ 48 mm (≈ 1.89 in); a small left inset.
        private readonly float x = paperMm == 58 ? 4 : 6;
        private readonly float width = paperMm == 58 ? 182 : 270;
        private readonly float scale = paperMm == 58 ? 0.8f : 1f;
        private float y = 4;
        private Graphics g = default!;

        private Font F(float size, FontStyle style = FontStyle.Regular) => new("Arial", size * scale, style);

        public void Draw(Graphics graphics)
        {
            g = graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var isWalkIn = sale.CustomerId == Customer.WalkInId;

            using var shopFont = F(15, FontStyle.Bold);
            using var titleFont = F(10, FontStyle.Bold);
            using var normal = F(9);
            using var bold = F(9, FontStyle.Bold);
            using var small = F(7.5f);
            using var contact = F(8.5f);
            using var totalFont = F(13, FontStyle.Bold);

            // ---- shop: logo (Settings), then the name unless the logo already shows it ----
            if (shop.Logo is { } logoBytes) DrawLogo(logoBytes);
            if (shop.PrintName) Center(shop.Name, shopFont);
            if (!string.IsNullOrWhiteSpace(shop.Address)) Center(shop.Address, contact);
            if (!string.IsNullOrWhiteSpace(shop.Phone)) Center("Mobile: " + shop.Phone, contact);
            y += 4;
            Line(1.5f);
            var exchange = ExchangeSummary.Of(sale);
            Center(exchange is null ? "S A L E S   R E C E I P T" : "E X C H A N G E   R E C E I P T", titleFont);
            Line(1.5f);
            y += 2;

            // ---- header ----
            Row("Invoice #", sale.Id.ToString(), normal, bold);
            Row("Date", $"{sale.SaleDate:dd-MMM-yyyy} {sale.CreatedAt:HH:mm}", normal, normal);
            Row("Customer", sale.Customer?.Name ?? "", normal, normal);
            if (!isWalkIn && !string.IsNullOrWhiteSpace(sale.Customer?.Phone))
                Row("Phone", sale.Customer.Phone, normal, normal);
            Dashes();

            // ---- items ----
            for (var i = 0; i < sale.Lines.Count; i++)
            {
                var l = sale.Lines[i];
                var item = l.StockItem;
                Left($"{i + 1}. {item?.Product?.Name}", bold); // owner: product name only (no company / pack)
                var details = string.Join(" · ", new[] { item?.ArticleNo, item?.Barcode, item?.Colour, item?.Size is { Length: > 0 } s ? $"Size {s}" : null }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                if (details.Length > 0) Left(details, small);
                Row($"{Fmt.Qty(l.Qty)} x {Fmt.Money(l.Rate)}", Fmt.Money(l.Amount), normal, bold);
                y += 2;
            }
            Dashes();

            // ---- totals ----
            Row("Items", $"{sale.Lines.Count} (qty {Fmt.Qty(sale.Lines.Sum(l => l.Qty))})", normal, normal);
            Row("Sub total", Fmt.Money(sale.SubTotal), normal, normal);
            if (sale.Discount > 0) Row("Discount", "- " + Fmt.Money(sale.Discount), normal, normal);
            y += 2;
            if (exchange is { } ex)
            {
                // Exchange: the returned items (what was paid for them) come off the new items.
                Row("New items total", Fmt.Money(sale.Total), bold, bold);
                Dashes();
                Center("RETURNED ITEMS", bold);
                foreach (var r in sale.ExchangeReturns)
                    foreach (var l in r.Lines)
                    {
                        var item = l.StockItem;
                        Left(item?.Product?.Name ?? "", bold);
                        var details = string.Join(" · ", new[] { item?.ArticleNo, item?.Barcode, item?.Colour, item?.Size is { Length: > 0 } s ? $"Size {s}" : null, $"inv #{r.SaleId}" }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                        Left(details, small);
                        var less = l.Amount < Math.Round(l.Qty * l.Rate, 2) ? " less disc." : "";
                        Row($"{Fmt.Qty(l.Qty)} x {Fmt.Money(l.Rate)}{less}", "- " + Fmt.Money(l.Amount), normal, bold);
                        y += 2;
                    }
                Row("Returned total", "- " + Fmt.Money(ex.Returned), bold, bold);
                y += 2;
                Line(2);
                y += 1;
                Row(ex.Net >= 0 ? "TO PAY" : "TO GIVE BACK", Fmt.Money(Math.Abs(ex.Net)), totalFont, totalFont);
                Line(2);
                y += 2;
                if (ex.Net >= 0)
                {
                    Row("Paid now", Fmt.Money(ex.PaidNow), normal, normal);
                    if (sale.Due > 0) Row("Due on this invoice", Fmt.Money(sale.Due), bold, bold);
                }
                else
                {
                    Row("Cash given back", Fmt.Money(ex.CashBack), normal, normal);
                    if (ex.Credit > 0) Row("Kept as credit", Fmt.Money(ex.Credit), bold, bold);
                }
            }
            else
            {
                Line(2);
                y += 1;
                Row("TOTAL", Fmt.Money(sale.Total), totalFont, totalFont);
                Line(2);
                y += 2;
                Row("Paid", Fmt.Money(sale.Paid), normal, normal);
                if (sale.Due > 0) Row("Due on this invoice", Fmt.Money(sale.Due), bold, bold);
            }
            if (sale.Discount > 0) Center("You saved " + Fmt.Money(sale.Discount), bold);
            if (!isWalkIn)
            {
                Dashes();
                Row("Previous balance", Fmt.Money(previousBalance), normal, normal);
                Row("New balance", Fmt.Money(previousBalance + sale.Due - (exchange?.Credit ?? 0)), bold, bold);
            }
            if (!string.IsNullOrWhiteSpace(sale.Notes))
            {
                Dashes();
                Left("Note: " + sale.Notes, small);
            }

            // ---- footer ----
            Dashes();
            if (!string.IsNullOrWhiteSpace(shop.InvoiceFooter))
                Center(shop.InvoiceFooter, normal);
            else
            {
                Center("Thank you for shopping with us!", bold);
                Center("Goods once sold are not returnable without this receipt.", small);
            }
            y += 4;
            Center($"*** {sale.Id} ***", small);
        }

        /// <summary>The logo centred, ~46 mm wide on an 80 mm roll (33 mm on 58 mm), at most ~28 mm high.</summary>
        private void DrawLogo(byte[] png)
        {
            try
            {
                using var stream = new System.IO.MemoryStream(png);
                using var logo = Image.FromStream(stream);
                float maxW = paperMm == 58 ? 130 : 180, maxH = 110; // hundredths of an inch
                var k = Math.Min(maxW / logo.Width, maxH / logo.Height);
                float w = logo.Width * k, h = logo.Height * k;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(logo, x + (width - w) / 2, y, w, h);
                y += h + 4;
            }
            catch (Exception ex)
            {
                AppLog.Warn("The shop logo could not be drawn on the receipt", ex); // print the rest anyway
            }
        }

        // ---- drawing helpers (each moves y down by what it drew) ----

        private void Center(string text, Font font) => Text(text, font, StringAlignment.Center);

        private void Left(string text, Font font) => Text(text, font, StringAlignment.Near);

        private void Text(string text, Font font, StringAlignment align)
        {
            using var format = new StringFormat { Alignment = align };
            var size = g.MeasureString(text, font, (int)width, format); // wraps long text to the paper width
            g.DrawString(text, font, Brushes.Black, new RectangleF(x, y, width, size.Height), format);
            y += size.Height;
        }

        /// <summary>Label on the left, value on the right of the same line.</summary>
        private void Row(string left, string right, Font leftFont, Font rightFont)
        {
            using var near = new StringFormat { Alignment = StringAlignment.Near };
            using var far = new StringFormat { Alignment = StringAlignment.Far };
            var rightSize = g.MeasureString(right, rightFont);
            var leftWidth = Math.Max(width - rightSize.Width - 4, width * 0.4f);
            var leftSize = g.MeasureString(left, leftFont, (int)leftWidth, near);
            g.DrawString(left, leftFont, Brushes.Black, new RectangleF(x, y, leftWidth, leftSize.Height), near);
            g.DrawString(right, rightFont, Brushes.Black, new RectangleF(x, y, width, rightSize.Height), far);
            y += Math.Max(leftSize.Height, rightSize.Height);
        }

        private void Line(float thickness)
        {
            using var pen = new Pen(Color.Black, thickness);
            g.DrawLine(pen, x, y, x + width, y);
            y += thickness + 2;
        }

        private void Dashes()
        {
            y += 3;
            using var pen = new Pen(Color.Black, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawLine(pen, x, y, x + width, y);
            y += 5;
        }
    }
}
