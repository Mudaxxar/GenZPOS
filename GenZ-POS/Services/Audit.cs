using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>
/// Audit log (owner 2026-10-24): <see cref="Add"/> puts an <see cref="AuditEntry"/> on the same context as the change, so
/// it is saved (or rolled back) in the same transaction. Read on Users &amp; Roles → Audit log (<c>/audit</c>,
/// <see cref="Permissions.AuditView"/>). Action names are shown and filtered on — keep them stable.
/// </summary>
public static class Audit
{
    public const string SaleDeleted = "Sale deleted";
    public const string SaleReturnDeleted = "Sale return deleted";
    public const string PurchaseEdited = "Purchase edited";
    public const string PurchaseDeleted = "Purchase deleted";
    public const string PurchaseReturnDeleted = "Purchase return deleted";
    public const string PriceChanged = "Price changed";
    public const string BigDiscount = "Big discount";
    public const string RateBelowPrice = "Sold below price";
    public const string StockAdjusted = "Stock adjusted";
    public const string ExpenseAdded = "Expense added";
    public const string ExpenseDeleted = "Expense deleted";
    public const string DayClosed = "Day closed";
    public const string PaymentDeleted = "Payment deleted";

    public static readonly string[] Actions =
    [
        SaleDeleted, SaleReturnDeleted, PurchaseEdited, PurchaseDeleted, PurchaseReturnDeleted, PriceChanged, BigDiscount,
        RateBelowPrice, StockAdjusted, ExpenseAdded, ExpenseDeleted, DayClosed, PaymentDeleted,
    ];

    /// <summary>An invoice discount (rate cuts + invoice discount) at or above this % of the list price is logged.</summary>
    public const decimal BigDiscountPercent = 10;

    public static void Add(AppDbContext db, CurrentUser user, string action, string details, int? refId = null) =>
        db.AuditLog.Add(new AuditEntry
        {
            At = DateTime.Now,
            UserName = user.Session?.UserName ?? "system",
            Action = action,
            RefId = refId,
            Details = details.Length > 1000 ? details[..997] + "…" : details,
        });
}
