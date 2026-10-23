using System.ComponentModel.DataAnnotations;

namespace GenZPOS.Data.Entities;

/// <summary>Supplier bill. Saving it adds stock.</summary>
public class Purchase
{
    public int Id { get; set; }
    public DateTime PurchaseDate { get; set; } = DateTime.Today;

    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>Supplier's own bill / invoice number.</summary>
    [MaxLength(50)]
    public string? BillNo { get; set; }

    public decimal SubTotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Total { get; set; }
    public decimal Paid { get; set; }

    [MaxLength(250)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<PurchaseLine> Lines { get; set; } = [];
}

public class PurchaseLine
{
    public int Id { get; set; }
    public int PurchaseId { get; set; }

    /// <summary>The colour / size (barcode) bought, sold or moved.</summary>
    public int StockItemId { get; set; }
    public StockItem? StockItem { get; set; }

    public decimal Qty { get; set; }
    public decimal Cost { get; set; }
    public decimal Amount { get; set; }

    /// <summary>
    /// Retail price set on this purchase (cost + profit %). Saving the purchase makes it the product's sale price.
    /// Null on older lines = the sale price wasn't changed.
    /// </summary>
    public decimal? SalePrice { get; set; }
}

/// <summary>Customer invoice. Saving it removes stock; unpaid amount goes on the customer's ledger.</summary>
public class Sale
{
    public int Id { get; set; }
    public DateTime SaleDate { get; set; } = DateTime.Today;

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public decimal SubTotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Total { get; set; }

    /// <summary>Paid at the counter when the invoice was made.</summary>
    public decimal Paid { get; set; }

    [MaxLength(250)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<SaleLine> Lines { get; set; } = [];

    /// <summary>
    /// Exchange (owner 2026-10-23): returns saved together with this invoice on the New Sale screen. Their value was set
    /// against this invoice: <see cref="Paid"/> includes the part of it used to pay (see SaleService.CreateExchangeAsync).
    /// </summary>
    public List<SaleReturn> ExchangeReturns { get; set; } = [];

    public decimal Due => Total - Paid;
}

public class SaleLine
{
    public int Id { get; set; }
    public int SaleId { get; set; }

    /// <summary>The colour / size (barcode) bought, sold or moved.</summary>
    public int StockItemId { get; set; }
    public StockItem? StockItem { get; set; }

    /// <summary>Unused: tint/shade per line was removed from the UI (owner found it confusing). Kept so old data and the schema stay intact.</summary>
    [MaxLength(60)]
    public string? ShadeCode { get; set; }

    public decimal Qty { get; set; }
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }

    /// <summary>Product's purchase price at the moment of sale (set by SaleService, never from the UI). Used for profit.</summary>
    public decimal Cost { get; set; }
}

/// <summary>
/// Customer brings items back. Stock comes back in; the customer owes <see cref="Total"/> less,
/// and any cash handed back (<see cref="Refunded"/>) is added back to what they owe.
/// </summary>
public class SaleReturn
{
    public int Id { get; set; }
    public DateTime ReturnDate { get; set; } = DateTime.Today;

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>The invoice the items came from (optional, but the normal case).</summary>
    public int? SaleId { get; set; }
    public Sale? Sale { get; set; }

    /// <summary>Exchange: the NEW invoice this return was saved with (its value went towards that invoice).</summary>
    public int? ExchangeSaleId { get; set; }
    public Sale? ExchangeSale { get; set; }

    public decimal Total { get; set; }

    /// <summary>
    /// Cash handed back to the customer now. The rest of the Total is credited to their balance. On an exchange it also
    /// includes the part set against the new invoice (that invoice's Paid includes the same amount).
    /// </summary>
    public decimal Refunded { get; set; }

    [MaxLength(250)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<SaleReturnLine> Lines { get; set; } = [];
}

public class SaleReturnLine
{
    public int Id { get; set; }
    public int SaleReturnId { get; set; }

    /// <summary>The colour / size (barcode) bought, sold or moved.</summary>
    public int StockItemId { get; set; }
    public StockItem? StockItem { get; set; }

    public decimal Qty { get; set; }
    /// <summary>Price refunded per unit (normally the invoice rate).</summary>
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Cost per unit as recorded on the original sale line (reverses that line's cost in profit).</summary>
    public decimal Cost { get; set; }
}

/// <summary>
/// Items sent back to a supplier (damaged / wrong). Stock goes out; we owe the supplier <see cref="Total"/> less,
/// and any cash the supplier gives back (<see cref="Received"/>) is added back to what we owe.
/// </summary>
public class PurchaseReturn
{
    public int Id { get; set; }
    public DateTime ReturnDate { get; set; } = DateTime.Today;

    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>The bill the items came from (optional).</summary>
    public int? PurchaseId { get; set; }
    public Purchase? Purchase { get; set; }

    public decimal Total { get; set; }

    /// <summary>Cash the supplier paid back now. The rest of the Total reduces what we owe them.</summary>
    public decimal Received { get; set; }

    [MaxLength(250)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<PurchaseReturnLine> Lines { get; set; } = [];
}

public class PurchaseReturnLine
{
    public int Id { get; set; }
    public int PurchaseReturnId { get; set; }

    /// <summary>The colour / size (barcode) bought, sold or moved.</summary>
    public int StockItemId { get; set; }
    public StockItem? StockItem { get; set; }

    public decimal Qty { get; set; }
    public decimal Cost { get; set; }
    public decimal Amount { get; set; }
}

public enum PaymentMethod
{
    Cash = 0,
    Bank = 1,
    Cheque = 2,
    Other = 3,
}

/// <summary>Money paid to a supplier against what we owe them (purchases not paid in full).</summary>
public class SupplierPayment
{
    public int Id { get; set; }
    public DateTime PaymentDate { get; set; } = DateTime.Today;

    [Range(1, int.MaxValue, ErrorMessage = "Select a supplier")]
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Amount must be more than zero")]
    public decimal Amount { get; set; }

    public PaymentMethod Method { get; set; }

    [MaxLength(250)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Money received from a customer against their balance.</summary>
public class CustomerPayment
{
    public int Id { get; set; }
    public DateTime PaymentDate { get; set; } = DateTime.Today;

    [Range(1, int.MaxValue, ErrorMessage = "Select a customer")]
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Amount must be more than zero")]
    public decimal Amount { get; set; }

    public PaymentMethod Method { get; set; }

    [MaxLength(250)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
