using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>What a held till cart remembers.</summary>
public sealed record HeldCart(int CustomerId, IReadOnlyList<HeldLine> Lines, decimal DiscountValue, bool DiscountIsPercent, string? Notes);

public sealed record HeldLine(int StockItemId, decimal Qty, decimal Rate);

/// <summary>A held cart in the list (without its lines).</summary>
public sealed record HeldSaleInfo(int Id, string? Label, string Customer, int ItemCount, decimal Total, string? HeldBy, DateTime HeldAt);

/// <summary>
/// Hold / park a sale (owner 2026-10-24): the till puts a cart aside to serve the next customer and brings it back later,
/// on any counter. Kept in the database (survives a restart); stock is not reserved — the sale checks it when saved.
/// </summary>
public class HeldSaleService(IDbContextFactory<AppDbContext> dbFactory, CurrentUser currentUser)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> HoldAsync(HeldCart cart, string? label, decimal total)
    {
        currentUser.Demand(Permissions.SalesCreate);
        if (cart.Lines.Count == 0)
            throw new BusinessException("The cart is empty: nothing to hold.");
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label is { Length: > 80 }) label = label[..80];

        await using var db = await dbFactory.CreateDbContextAsync();
        var held = new HeldSale
        {
            CustomerId = cart.CustomerId, Label = label, Total = total, ItemCount = cart.Lines.Count,
            Data = JsonSerializer.Serialize(cart, Json), HeldBy = currentUser.Session?.UserName, HeldAt = DateTime.Now,
        };
        db.HeldSales.Add(held);
        await db.SaveChangesAsync();
        return held.Id;
    }

    public async Task<List<HeldSaleInfo>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await (from h in db.HeldSales.AsNoTracking()
                      join c in db.Customers on h.CustomerId equals c.Id into cs
                      from c in cs.DefaultIfEmpty()
                      orderby h.HeldAt
                      select new HeldSaleInfo(h.Id, h.Label, c != null ? c.Name : "?", h.ItemCount, h.Total, h.HeldBy, h.HeldAt))
            .ToListAsync();
    }

    public Task<int> CountAsync() => CountInAsync();

    private async Task<int> CountInAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.HeldSales.CountAsync();
    }

    /// <summary>Takes a held cart back (and removes it from the list). Null when another counter already took it.</summary>
    public async Task<HeldCart?> ResumeAsync(int id)
    {
        currentUser.Demand(Permissions.SalesCreate);
        await using var db = await dbFactory.CreateDbContextAsync();
        var data = await db.HeldSales.Where(h => h.Id == id).Select(h => h.Data).FirstOrDefaultAsync();
        if (data is null || await db.HeldSales.Where(h => h.Id == id).ExecuteDeleteAsync() == 0)
            return null;
        return JsonSerializer.Deserialize<HeldCart>(data, Json);
    }

    public async Task DeleteAsync(int id)
    {
        currentUser.Demand(Permissions.SalesCreate);
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.HeldSales.Where(h => h.Id == id).ExecuteDeleteAsync();
    }
}
