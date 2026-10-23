using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>
/// The money of an exchange invoice (new items + returned items, SaleService.CreateExchangeAsync) as the receipt, the
/// receipt preview and the A4 invoice show it. Worked out from what is stored: the returned value first pays for the
/// new items (<see cref="SetOff"/> is in the invoice's Paid and in the returns' Refunded); the rest is cash.
/// </summary>
public sealed record ExchangeSummary(decimal NewItems, decimal Returned, decimal SetOff, decimal PaidNow, decimal CashBack, decimal Credit)
{
    /// <summary>New items − returned: &gt; 0 the customer pays, &lt; 0 money back to them.</summary>
    public decimal Net => NewItems - Returned;

    /// <summary>Null when <paramref name="sale"/> is not an exchange (ExchangeReturns must be loaded).</summary>
    public static ExchangeSummary? Of(Sale sale)
    {
        if (sale.ExchangeReturns.Count == 0) return null;
        var returned = sale.ExchangeReturns.Sum(r => r.Total);
        var setOff = Math.Max(0, Math.Min(returned, sale.Total));
        var refunded = sale.ExchangeReturns.Sum(r => r.Refunded);
        return new ExchangeSummary(sale.Total, returned, setOff,
            PaidNow: Math.Max(0, sale.Paid - setOff),
            CashBack: Math.Max(0, refunded - setOff),
            Credit: sale.ExchangeReturns.Sum(r => r.Total - r.Refunded)); // returned value kept on the customer's account
    }
}
