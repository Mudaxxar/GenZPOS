using System.ComponentModel.DataAnnotations;

namespace GenZPOS.Data.Entities;

public enum StockMovementType
{
    Opening = 0,
    Purchase = 1,
    Sale = 2,
    AdjustIn = 3,
    AdjustOut = 4,
    /// <summary>Customer returned items (stock in). RefId = SaleReturn.Id.</summary>
    SaleReturn = 5,
    /// <summary>Items sent back to supplier (stock out). RefId = PurchaseReturn.Id.</summary>
    PurchaseReturn = 6,
}

/// <summary>
/// The single source of truth for stock. On-hand qty = Σ QtyIn − Σ QtyOut.
/// Purchase / Sale movements carry the document id in <see cref="RefId"/>.
/// </summary>
public class StockMovement
{
    public int Id { get; set; }

    /// <summary>The colour / size (barcode) bought, sold or moved.</summary>
    public int StockItemId { get; set; }
    public StockItem? StockItem { get; set; }

    public DateTime MovementDate { get; set; } = DateTime.Today;
    public StockMovementType Type { get; set; }

    public decimal QtyIn { get; set; }
    public decimal QtyOut { get; set; }

    /// <summary>Purchase.Id or Sale.Id for document movements; null for manual adjustments.</summary>
    public int? RefId { get; set; }

    [MaxLength(250)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
