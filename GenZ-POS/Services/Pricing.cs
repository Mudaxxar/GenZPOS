namespace GenZPOS.Services;

/// <summary>
/// Cost price ↔ profit % ↔ retail (sale) price on the purchase screen. Profit % is on cost (mark-up):
/// retail = cost × (1 + profit / 100); profit = (retail − cost) / cost × 100. Money to 2 decimals.
/// </summary>
public static class Pricing
{
    public static decimal RetailFromProfit(decimal cost, decimal profitPercent) =>
        Math.Round(cost * (1 + profitPercent / 100m), 2);

    /// <summary>Null when there is no cost to measure from.</summary>
    public static decimal? ProfitPercent(decimal cost, decimal retail) =>
        cost > 0 ? Math.Round((retail - cost) / cost * 100m, 2) : null;
}
