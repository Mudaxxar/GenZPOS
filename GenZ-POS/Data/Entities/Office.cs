using System.ComponentModel.DataAnnotations;

namespace GenZPOS.Data.Entities;

/// <summary>What money was spent on: Rent, Salaries, Electricity, Tea &amp; food … (owner 2026-10-24).</summary>
public class ExpenseCategory
{
    public int Id { get; set; }

    [Required, MaxLength(80)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Money the shop spent (not on stock). Cash expenses come out of the drawer (Day Book, day closing); net profit =
/// gross profit − expenses.
/// </summary>
public class Expense
{
    public int Id { get; set; }
    public DateTime ExpenseDate { get; set; } = DateTime.Today;

    [Range(1, int.MaxValue, ErrorMessage = "Select a category")]
    public int CategoryId { get; set; }
    public ExpenseCategory? Category { get; set; }

    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;

    /// <summary>Who was paid (landlord, electricity company, staff name …).</summary>
    [MaxLength(120)]
    public string? PaidTo { get; set; }

    [MaxLength(250)]
    public string? Notes { get; set; }

    [MaxLength(50)]
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Day closing (Z-report): the drawer counted at the end of a day. Expected = opening + cash in − cash out (Day Book);
/// difference = counted − expected. Cash left in the drawer becomes the next day's opening. One per date (closing again
/// replaces it).
/// </summary>
public class CashClosing
{
    public int Id { get; set; }

    /// <summary>The business day closed (date only, unique).</summary>
    public DateTime ClosingDate { get; set; }

    public decimal OpeningCash { get; set; }
    public decimal CashIn { get; set; }
    public decimal CashOut { get; set; }
    public decimal Expected { get; set; }
    public decimal Counted { get; set; }
    public decimal Difference { get; set; }

    /// <summary>Taken out of the drawer at closing (to the owner / bank); the rest stays for tomorrow.</summary>
    public decimal TakenOut { get; set; }
    public decimal LeftInDrawer { get; set; }

    [MaxLength(250)]
    public string? Notes { get; set; }

    [MaxLength(50)]
    public string? ClosedBy { get; set; }

    public DateTime ClosedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Who did what (owner 2026-10-24): deletes, price changes, big discounts, stock adjustments, expenses, day closings,
/// settings. Written in the same transaction as the change (Services/Audit.cs). Never edited or deleted by the app.
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.Now;

    [MaxLength(50)]
    public string? UserName { get; set; }

    /// <summary>Short kind, e.g. "Sale deleted", "Price changed" (see <c>Audit</c> constants).</summary>
    [Required, MaxLength(60)]
    public string Action { get; set; } = "";

    /// <summary>The document / item's number (invoice #, stock item id …), when there is one.</summary>
    public int? RefId { get; set; }

    [MaxLength(1000)]
    public string? Details { get; set; }
}

/// <summary>
/// A till cart put aside (Hold) to serve the next customer, brought back later (Resume) on any counter. Lines are kept as
/// JSON; stock is not reserved (it is checked again when the sale is saved).
/// </summary>
public class HeldSale
{
    public int Id { get; set; }
    public int CustomerId { get; set; }

    /// <summary>Name the cashier gave it ("lady in red", "Ali"), shown in the held list.</summary>
    [MaxLength(80)]
    public string? Label { get; set; }

    public decimal Total { get; set; }
    public int ItemCount { get; set; }

    /// <summary>{ lines: [{ stockItemId, qty, rate }], discount, discountIsPercent, notes }.</summary>
    public string Data { get; set; } = "";

    [MaxLength(50)]
    public string? HeldBy { get; set; }

    public DateTime HeldAt { get; set; } = DateTime.Now;
}
