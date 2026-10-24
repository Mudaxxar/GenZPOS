using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>
/// Shop expenses (owner 2026-10-24): rent, salaries, electricity, tea … Cash ones come out of the drawer (Day Book, day
/// closing); all of them come off the profit (net profit = gross profit − expenses). Adds and deletes are audited.
/// </summary>
public class ExpenseService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser)
{
    public async Task<int> AddAsync(Expense expense)
    {
        currentUser.Demand(Permissions.ExpensesManage);
        if (expense.CategoryId <= 0)
            throw new BusinessException("Select what the money was spent on.");
        if (expense.Amount <= 0)
            throw new BusinessException("Amount must be more than zero.");
        var paidTo = Clean(expense.PaidTo);
        var notes = Clean(expense.Notes);
        if (paidTo is { Length: > 120 } || notes is { Length: > 250 })
            throw new BusinessException("Paid to can be at most 120 and notes 250 characters.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var category = await db.ExpenseCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == expense.CategoryId)
                       ?? throw new BusinessException("That expense category no longer exists.");
        var entity = new Expense
        {
            ExpenseDate = expense.ExpenseDate.Date, CategoryId = expense.CategoryId, Amount = Math.Round(expense.Amount, 2),
            Method = expense.Method, PaidTo = paidTo, Notes = notes,
            CreatedBy = currentUser.Session?.UserName, CreatedAt = DateTime.Now,
        };
        db.Expenses.Add(entity);
        await db.SaveChangesAsync();
        Audit.Add(db, currentUser, Audit.ExpenseAdded,
            $"Expense #{entity.Id} {entity.ExpenseDate:dd-MMM-yyyy}: {category.Name} {entity.Amount:N2} ({entity.Method})" +
            (paidTo is null ? "" : $" to {paidTo}"), entity.Id);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    public async Task DeleteAsync(int id)
    {
        currentUser.Demand(Permissions.ExpensesManage);
        await using var db = await dbFactory.CreateDbContextAsync();
        var e = await db.Expenses.Include(x => x.Category).FirstOrDefaultAsync(x => x.Id == id)
                ?? throw new BusinessException("Expense not found. It may already have been deleted.");
        db.Expenses.Remove(e);
        Audit.Add(db, currentUser, Audit.ExpenseDeleted,
            $"Expense #{id} {e.ExpenseDate:dd-MMM-yyyy}: {e.Category?.Name} {e.Amount:N2} ({e.Method})" + (e.PaidTo is null ? "" : $" to {e.PaidTo}"), id);
        await db.SaveChangesAsync();
    }

    /// <summary>Adds a category (or returns the existing one with that name).</summary>
    public async Task<int> AddCategoryAsync(string name)
    {
        currentUser.Demand(Permissions.ExpensesManage);
        name = Clean(name) ?? throw new BusinessException("Enter a name.");
        if (name.Length > 80)
            throw new BusinessException("Name can be at most 80 characters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Name == name) is { } existing)
        {
            existing.IsActive = true;
            await db.SaveChangesAsync();
            return existing.Id;
        }
        var category = new ExpenseCategory { Name = name };
        db.ExpenseCategories.Add(category);
        await db.SaveChangesAsync();
        return category.Id;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
