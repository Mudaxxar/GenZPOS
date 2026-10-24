using System.Drawing;
using System.Drawing.Printing;
using GenZPOS.Components;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>
/// Prints the day closing (Z-report) on the receipt printer with GDI, no dialog — the same way as
/// <see cref="ReceiptPrinter"/> (owner's proven approach for the POS-80): every line drawn at a known position, the page
/// ends after the last line. Units: 1/100 inch.
/// </summary>
public static class ZReportPrinter
{
    /// <summary>Returns null when sent, else a message for the user.</summary>
    /// <param name="toFile">Print into this file instead (e.g. "Microsoft Print to PDF"; for checks).</param>
    public static string? Print(ZReport z, ShopInfo shop, string? printerName, int paperMm, string? printedBy, string? toFile = null)
    {
        try
        {
            using var doc = new PrintDocument();
            doc.DocumentName = $"Z-report {z.Date:yyyy-MM-dd}";
            doc.PrintController = new StandardPrintController();
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
            doc.PrintPage += (_, e) =>
            {
                new Layout(e.Graphics!, paperMm).Draw(z, shop, printedBy);
                e.HasMorePages = false;
            };
            doc.Print();
            AppLog.Info($"Z-report {z.Date:yyyy-MM-dd} printed on \"{doc.PrinterSettings.PrinterName}\"");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Printing the Z-report failed", ex);
            return $"The Z-report could not be printed: {ex.Message}";
        }
    }

    private sealed class Layout(Graphics g, int paperMm)
    {
        private readonly float x = paperMm == 58 ? 4 : 6;
        private readonly float width = paperMm == 58 ? 182 : 270;
        private readonly float scale = paperMm == 58 ? 0.8f : 1f;
        private float y = 4;

        private Font F(float size, FontStyle style = FontStyle.Regular) => new("Arial", size * scale, style);

        public void Draw(ZReport z, ShopInfo shop, string? printedBy)
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var shopFont = F(13, FontStyle.Bold);
            using var title = F(10, FontStyle.Bold);
            using var normal = F(8.5f);
            using var bold = F(8.5f, FontStyle.Bold);
            using var big = F(12, FontStyle.Bold);
            using var small = F(7.5f);
            var c = z.Closing;
            var book = z.Book;
            var t = book.Totals;

            Center(shop.Name, shopFont);
            Line(1.5f);
            Center("Z - REPORT  ·  DAY CLOSING", title);
            Line(1.5f);
            Row("Day", z.Date.ToString("ddd dd-MMM-yyyy"), normal, bold);
            Row("Printed", $"{DateTime.Now:dd-MMM-yyyy HH:mm}" + (printedBy is null ? "" : $" · {printedBy}"), normal, normal);
            if (c is not null) Row("Closed by", $"{c.ClosedBy} · {c.ClosedAt:HH:mm}", normal, normal);
            else Center("(day not closed yet)", bold);

            Section("SALES", bold);
            Row("Invoices", t.Invoices.ToString(), normal, normal);
            Row("Sales total", Fmt.Money(t.Sales), normal, bold);
            if (z.InvoiceDiscounts > 0) Row("  of which discounts", Fmt.Money(z.InvoiceDiscounts), normal, normal);
            if (t.CreditSales > 0) Row("On credit (not paid)", Fmt.Money(t.CreditSales), normal, normal);
            if (t.SaleReturns > 0) Row("Sales returns", "- " + Fmt.Money(t.SaleReturns), normal, normal);
            Row("Net sales", Fmt.Money(t.Sales - t.SaleReturns), bold, bold);

            Section("CASH DRAWER", bold);
            Row("Opening cash", Fmt.Money(z.Opening), normal, normal);
            foreach (var (type, cashIn, _) in z.CashByType.Where(r => r.In != 0))
                Row("+ " + type, Fmt.Money(cashIn), normal, normal);
            foreach (var (type, _, cashOut) in z.CashByType.Where(r => r.Out != 0))
                Row("- " + type, Fmt.Money(cashOut), normal, normal);
            Line(1);
            // A closed day: the figures it was closed with (entries changed later are flagged below).
            Row("EXPECTED", Fmt.Money(c?.Expected ?? z.Expected), big, big);
            if (c is not null)
            {
                Row("Counted", Fmt.Money(c.Counted), bold, bold);
                Row(c.Difference < 0 ? "SHORT" : c.Difference > 0 ? "OVER" : "Difference", Fmt.Money(Math.Abs(c.Difference)), big, big);
                if (c.TakenOut > 0) Row("Taken out", Fmt.Money(c.TakenOut), normal, normal);
                Row("Left in drawer", Fmt.Money(c.LeftInDrawer), bold, bold);
                if (z.ChangedSinceClosing)
                    Left($"! Entries changed after closing: cash in {Fmt.Money(c.CashIn)} → {Fmt.Money(book.CashIn)}, out {Fmt.Money(c.CashOut)} → {Fmt.Money(book.CashOut)}. Expected now {Fmt.Money(z.Expected)}. Close the day again to recount.", small);
            }

            if (book.NonCashIn > 0 || book.NonCashOut > 0)
            {
                Section("BANK / CHEQUE (not in drawer)", bold);
                if (book.NonCashIn > 0) Row("Received", Fmt.Money(book.NonCashIn), normal, normal);
                if (book.NonCashOut > 0) Row("Paid", Fmt.Money(book.NonCashOut), normal, normal);
            }

            if (z.Expenses.Count > 0)
            {
                Section("EXPENSES", bold);
                foreach (var (category, amount, cash) in z.Expenses)
                    Row(category + (cash == amount ? "" : " (part bank)"), Fmt.Money(amount), normal, normal);
                Row("Total expenses", Fmt.Money(z.Expenses.Sum(e => e.Amount)), bold, bold);
            }

            if (t.Purchases > 0 || t.PurchaseReturns > 0)
            {
                Section("PURCHASES", bold);
                Row("Bills", Fmt.Money(t.Purchases), normal, normal);
                if (t.PurchaseReturns > 0) Row("Returned to suppliers", Fmt.Money(t.PurchaseReturns), normal, normal);
            }

            if (!string.IsNullOrWhiteSpace(c?.Notes))
            {
                Dashes();
                Left("Note: " + c.Notes, small);
            }
            Dashes();
            y += 18;
            Row("Signature", "______________", normal, normal);
            y += 6;
        }

        private void Section(string text, Font font)
        {
            Dashes();
            Center(text, font);
        }

        private void Center(string text, Font font) => Text(text, font, StringAlignment.Center);

        private void Left(string text, Font font) => Text(text, font, StringAlignment.Near);

        private void Text(string text, Font font, StringAlignment align)
        {
            using var format = new StringFormat { Alignment = align };
            var size = g.MeasureString(text, font, (int)width, format);
            g.DrawString(text, font, Brushes.Black, new RectangleF(x, y, width, size.Height), format);
            y += size.Height;
        }

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
