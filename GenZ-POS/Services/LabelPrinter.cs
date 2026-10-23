using System.Drawing;
using System.Drawing.Printing;
using GenZPOS.Components;

namespace GenZPOS.Services;

/// <summary>What goes on one barcode label (one piece = one stock item).</summary>
public sealed record LabelData(string Barcode, string? ArticleNo, string ProductName, string? Colour, string? Size, decimal Price);

/// <summary>
/// Prints barcode labels straight to a label printer with GDI (<see cref="PrintDocument"/>, no dialog) — like BarTender,
/// without it. Layout = the shop's <see cref="LabelDesign"/> (Settings → Barcode label, drag and drop): one page per
/// label of the design's size, zero margins, every element drawn in its own box (text shrinks to fit; Code 128 barcode
/// from <see cref="Code128"/>). The designer / Labels screen preview (Shared/LabelCanvas.razor) uses the same numbers.
/// </summary>
public static class LabelPrinter
{
    /// <summary>
    /// Prints every label in <paramref name="labels"/> (repeat an item for more copies) on <paramref name="printerName"/>
    /// (null = Windows default). Returns null when sent, else a message for the user.
    /// </summary>
    /// <param name="toFile">Print into this file instead (e.g. with "Microsoft Print to PDF"; for checks).</param>
    public static string? Print(IReadOnlyList<LabelData> labels, LabelDesign design, ShopInfo shop, string? printerName, string? toFile = null)
    {
        if (labels.Count == 0) return "Tick at least one label to print.";
        try
        {
            using var doc = new PrintDocument();
            doc.DocumentName = $"Labels ({labels.Count})";
            doc.PrintController = new StandardPrintController(); // no "Printing…" pop-up
            if (!string.IsNullOrWhiteSpace(printerName))
                doc.PrinterSettings.PrinterName = printerName;
            if (toFile is not null)
            {
                doc.PrinterSettings.PrintToFile = true;
                doc.PrinterSettings.PrintFileName = toFile;
            }
            if (!doc.PrinterSettings.IsValid)
                return $"Printer \"{doc.PrinterSettings.PrinterName}\" was not found. Choose the label printer again.";

            doc.DefaultPageSettings.PaperSize = new PaperSize($"GenZ label {design.WidthMm:0.#}x{design.HeightMm:0.#} mm",
                (int)Math.Round(Units(design.WidthMm)), (int)Math.Round(Units(design.HeightMm)));
            doc.DefaultPageSettings.Landscape = false;
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            doc.OriginAtMargins = false;

            var next = 0;
            doc.PrintPage += (_, e) =>
            {
                Draw(e.Graphics!, design, labels[next], shop);
                next++;
                e.HasMorePages = next < labels.Count;
            };
            doc.Print();
            AppLog.Info($"{labels.Count} label(s) printed on \"{doc.PrinterSettings.PrinterName}\" ({design.WidthMm:0.#} × {design.HeightMm:0.#} mm)");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Printing labels failed", ex);
            return $"The labels could not be printed: {ex.Message}";
        }
    }

    /// <summary>"Rs 4,250" (no ".00" on a whole price, easier to read on a tag); "Rs 4,250.50" otherwise.</summary>
    public static string PriceText(decimal price) =>
        price == Math.Floor(price) ? $"Rs {price:N0}" : Fmt.Money(price);

    /// <summary>Millimetres → GDI print units (hundredths of an inch).</summary>
    private static float Units(double mm) => (float)(mm / 25.4 * 100);

    private static void Draw(Graphics g, LabelDesign design, LabelData label, ShopInfo shop)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        foreach (var e in design.Elements)
        {
            var box = new RectangleF(Units(e.X), Units(e.Y), Units(e.W), Units(e.H));
            switch (e.Field)
            {
                case LabelField.Logo when shop.Logo is { } png:
                    DrawLogo(g, png, box);
                    break;
                case LabelField.Line:
                    g.FillRectangle(Brushes.Black, box.X, box.Y, box.Width, Math.Max(box.Height, 1));
                    break;
                case LabelField.Barcode:
                    DrawBarcode(g, label.Barcode, box);
                    break;
                default:
                    if (LabelDesign.TextFor(e, label, shop) is { } text)
                        DrawText(g, text, e, box);
                    break;
            }
        }
    }

    /// <summary>One line of text in its box, aligned, vertically centred; shrinks until it fits the box both ways.</summary>
    private static void DrawText(Graphics g, string text, LabelElement e, RectangleF box)
    {
        using var format = new StringFormat
        {
            Alignment = e.Align switch { LabelAlign.Left => StringAlignment.Near, LabelAlign.Right => StringAlignment.Far, _ => StringAlignment.Center },
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        var style = e.Bold ? FontStyle.Bold : FontStyle.Regular;
        var size = (float)Math.Max(4, e.FontPt);
        var font = new Font("Arial", size, style);
        try
        {
            while (size > 4)
            {
                var measured = g.MeasureString(text, font);
                if (measured.Width <= box.Width && measured.Height <= box.Height * 1.15f) break;
                size *= 0.93f;
                font.Dispose();
                font = new Font("Arial", size, style);
            }
            g.DrawString(text, font, Brushes.Black, box, format);
        }
        finally
        {
            font.Dispose();
        }
    }

    private static void DrawLogo(Graphics g, byte[] png, RectangleF box)
    {
        try
        {
            using var stream = new System.IO.MemoryStream(png);
            using var logo = Image.FromStream(stream);
            var s = Math.Min(box.Width / logo.Width, box.Height / logo.Height);
            float w = logo.Width * s, h = logo.Height * s;
            g.DrawImage(logo, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The shop logo could not be drawn on a label", ex);
        }
    }

    /// <summary>
    /// Code 128 centred in its box. The module (narrowest bar) is a whole number of printer dots, so every bar prints
    /// exactly and scanners read it reliably; a 10-module quiet zone stays free on each side where the box allows.
    /// </summary>
    private static void DrawBarcode(Graphics g, string text, RectangleF box)
    {
        var widths = Code128.Widths(text);
        var dotsPerUnit = g.DpiX / 100f; // printer dots per 1/100 inch
        var moduleDots = Math.Max(1, (int)Math.Floor(box.Width * dotsPerUnit / (widths.Sum() + 20)));
        var module = moduleDots / dotsPerUnit;
        var x = box.X + (box.Width - widths.Sum() * module) / 2;

        var smoothing = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None; // crisp bar edges
        for (var i = 0; i < widths.Length; i++)
        {
            var bw = widths[i] * module;
            if (i % 2 == 0) g.FillRectangle(Brushes.Black, x, box.Y, bw, box.Height);
            x += bw;
        }
        g.SmoothingMode = smoothing;
    }
}
