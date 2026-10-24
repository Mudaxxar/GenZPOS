using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>Everything the Z-report shows for one day (figures from the Day Book, so they always agree with it).</summary>
/// <param name="Opening">Cash in the drawer at the start: what the previous closing left in it (or typed, the first time).</param>
/// <param name="OpeningFrom">Date of that previous closing; null = no earlier closing (opening is typed).</param>
/// <param name="Closing">This day's closing when it was already done (closing again replaces it).</param>
public sealed record ZReport(
    DateTime Date, decimal Opening, DateTime? OpeningFrom, DayBookReport Book,
    IReadOnlyList<(string Category, decimal Amount, decimal Cash)> Expenses, decimal InvoiceDiscounts, CashClosing? Closing)
{
    /// <summary>Cash that should be in the drawer: opening + cash in − cash out.</summary>
    public decimal Expected => Opening + Book.CashIn - Book.CashOut;

    /// <summary>Cash entries of a closed day changed afterwards (e.g. an invoice deleted): the saved count no longer matches.</summary>
    public bool ChangedSinceClosing => Closing is { } c && (c.CashIn != Book.CashIn || c.CashOut != Book.CashOut);

    /// <summary>Cash in / out by kind (sales at the counter, payments received, refunds, expenses …).</summary>
    public IEnumerable<(string Type, decimal In, decimal Out)> CashByType =>
        Book.Rows.Where(r => r.CashIn != 0 || r.CashOut != 0)
            .GroupBy(r => r.Kind == "Expense" ? "Expenses" : r.Type)
            .Select(g => (g.Key, g.Sum(r => r.CashIn), g.Sum(r => r.CashOut)));
}

/// <summary>
/// Day closing (owner 2026-10-24): count the drawer at the end of the day against what the Day Book says should be
/// there, record the difference, take money out, and leave the rest as tomorrow's opening. One closing per date;
/// closing a day again replaces it. Audited.
/// </summary>
public class DayClosingService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser, ReportService reports)
{
    public async Task<ZReport> GetZReportAsync(DateTime date)
    {
        currentUser.Demand(Permissions.DayClosing);
        date = date.Date;
        var book = await reports.GetDayBookAsync(date, date);

        await using var db = await dbFactory.CreateDbContextAsync();
        var previous = await db.CashClosings.AsNoTracking().Where(c => c.ClosingDate < date)
            .OrderByDescending(c => c.ClosingDate).FirstOrDefaultAsync();
        var closing = await db.CashClosings.AsNoTracking().FirstOrDefaultAsync(c => c.ClosingDate == date);
        var expenses = (await db.Expenses.AsNoTracking().Where(e => e.ExpenseDate == date)
                .GroupBy(e => e.Category!.Name)
                .Select(g => new { g.Key, Amount = g.Sum(e => e.Amount), Cash = g.Sum(e => e.Method == PaymentMethod.Cash ? e.Amount : 0) })
                .ToListAsync())
            .OrderByDescending(x => x.Amount).Select(x => (x.Key, x.Amount, x.Cash)).ToList();
        var discounts = await db.Sales.Where(s => s.SaleDate == date).SumAsync(s => (decimal?)s.Discount) ?? 0;

        // Opening = what the last closing left in the drawer; with no earlier closing, the one typed when this day was
        // closed (or 0).
        var opening = previous?.LeftInDrawer ?? closing?.OpeningCash ?? 0;
        return new ZReport(date, opening, previous?.ClosingDate, book, expenses, discounts, closing);
    }

    /// <summary>
    /// Closes <paramref name="date"/>: saves the counted cash against the expected cash, what was taken out and what is
    /// left for tomorrow. <paramref name="opening"/> is only used when there is no earlier closing. Returns the closing.
    /// </summary>
    public async Task<CashClosing> CloseAsync(DateTime date, decimal opening, decimal counted, decimal takenOut, string? notes)
    {
        currentUser.Demand(Permissions.DayClosing);
        date = date.Date;
        if (date > DateTime.Today)
            throw new BusinessException("A day can't be closed before it has happened.");
        if (counted < 0 || takenOut < 0 || opening < 0)
            throw new BusinessException("Amounts can't be negative.");
        if (takenOut > counted)
            throw new BusinessException("You can't take out more than the cash counted.");
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (notes is { Length: > 250 })
            throw new BusinessException("Notes can be at most 250 characters.");

        var z = await GetZReportAsync(date);
        if (z.OpeningFrom is null) z = z with { Opening = opening };

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var later = await db.CashClosings.Where(c => c.ClosingDate > date).OrderBy(c => c.ClosingDate).Select(c => c.ClosingDate).FirstOrDefaultAsync();

        var entity = await db.CashClosings.FirstOrDefaultAsync(c => c.ClosingDate == date);
        var again = entity is not null;
        if (entity is null)
        {
            entity = new CashClosing { ClosingDate = date };
            db.CashClosings.Add(entity);
        }
        entity.OpeningCash = z.Opening;
        entity.CashIn = z.Book.CashIn;
        entity.CashOut = z.Book.CashOut;
        entity.Expected = z.Expected;
        entity.Counted = Math.Round(counted, 2);
        entity.Difference = entity.Counted - entity.Expected;
        entity.TakenOut = Math.Round(takenOut, 2);
        entity.LeftInDrawer = entity.Counted - entity.TakenOut;
        entity.Notes = notes;
        entity.ClosedBy = currentUser.Session?.UserName;
        entity.ClosedAt = DateTime.Now;

        Audit.Add(db, currentUser, Audit.DayClosed,
            $"{(again ? "Closed again" : "Closed")} {date:dd-MMM-yyyy}: expected {entity.Expected:N2}, counted {entity.Counted:N2}, " +
            $"difference {entity.Difference:+#,0.00;-#,0.00;0.00}, taken out {entity.TakenOut:N2}, left {entity.LeftInDrawer:N2}" +
            (later != default ? $" (a later day, {later:dd-MMM-yyyy}, was already closed)" : ""));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        AppLog.Info($"Day {date:yyyy-MM-dd} closed by \"{currentUser.Session?.UserName}\": difference {entity.Difference:N2}");
        return entity;
    }
}
