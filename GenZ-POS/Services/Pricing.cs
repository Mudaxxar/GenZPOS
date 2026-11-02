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

    /// <summary>Price of one piece from the price of a pack: Rs 950 for 6 → 158.33.</summary>
    public static decimal UnitPrice(decimal packPrice, decimal perPack) =>
        perPack == 1 ? packPrice : Math.Round(packPrice / perPack, 2);

    /// <summary>
    /// The pack price back from a stored piece price: 158.33 for 6 → 950, not 949.98. A whole rupee amount is used when
    /// it gives that same piece price.
    /// </summary>
    public static decimal PackPrice(decimal unitPrice, decimal perPack)
    {
        if (perPack == 1) return unitPrice;
        var exact = Math.Round(unitPrice * perPack, 2);
        var whole = Math.Round(exact);
        return UnitPrice(whole, perPack) == unitPrice ? whole : exact;
    }
}
