namespace GenZPOS.Services;

/// <summary>
/// Per-product maximum discount (<c>Product.MaxDiscountPercent</c>), one rule for the till screen and SaleService:
/// a line's effective price (rate, then its share of the invoice discount) may not fall below
/// sale price × (1 − max %). So the rate can't be cut below <see cref="MinRate"/>, and the invoice discount can't be
/// more than the sum of every line's <see cref="Room"/>. Items without a limit can take any discount.
/// </summary>
public static class DiscountRules
{
    /// <summary>Lowest rate allowed, or null when the item has no limit (or no sale price to measure from).</summary>
    public static decimal? MinRate(decimal salePrice, decimal? maxDiscountPercent) =>
        maxDiscountPercent is { } max && salePrice > 0
            ? Math.Round(salePrice * (1 - Math.Clamp(max, 0, 100) / 100), 2)
            : null;

    /// <summary>How much of the invoice discount this line can still carry (its whole amount when there is no limit).</summary>
    public static decimal Room(decimal qty, decimal rate, decimal salePrice, decimal? maxDiscountPercent)
    {
        var amount = Math.Round(qty * rate, 2);
        return MinRate(salePrice, maxDiscountPercent) is { } min
            ? Math.Max(0, amount - Math.Round(qty * min, 2))
            : amount;
    }

    // ---------- invoice / bill discount typed as Rs or % (DiscountInput); always saved as Rs ----------

    /// <summary>The discount in rupees: <paramref name="value"/> as typed, or that % of <paramref name="subTotal"/>.</summary>
    public static decimal Amount(decimal subTotal, decimal value, bool isPercent) =>
        isPercent ? Math.Round(subTotal * Math.Clamp(value, 0, 100) / 100, 2) : value;

    /// <summary>A rupee discount as % of the sub total (for showing "= 5%"), 2 decimals.</summary>
    public static decimal ToPercent(decimal subTotal, decimal amount) =>
        subTotal > 0 ? Math.Round(amount / subTotal * 100, 2) : 0;

    /// <summary>
    /// Highest % (2 decimals, rounded down) whose amount stays within <paramref name="maxAmount"/>, so "use max"
    /// in % mode can never land a cent over the limit.
    /// </summary>
    public static decimal MaxPercent(decimal subTotal, decimal maxAmount) =>
        subTotal > 0 ? Math.Floor(Math.Min(maxAmount, subTotal) / subTotal * 10000) / 100 : 0;
}
