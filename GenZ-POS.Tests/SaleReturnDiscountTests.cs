using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>Owner 2026-10-23: a return gives back what the customer paid — the invoice rate less its share of the invoice discount.</summary>
public class SaleReturnDiscountTests
{
    private static SaleLine Line(int id, decimal qty, decimal rate) => new() { Id = id, Qty = qty, Rate = rate, Amount = qty * rate };

    private static SaleReturnableLine Returnable(SaleLine l, Dictionary<int, decimal> net, decimal returnedQty = 0, decimal returnedAmount = 0) =>
        new() { Rate = l.Rate, Sold = l.Qty, NetTotal = net[l.Id], Returned = returnedQty, ReturnedAmount = returnedAmount };

    [Fact]
    public void Owner_case_4250_with_20_percent_off_returns_3400()
    {
        var line = Line(1, 1, 4250);
        var sale = new Sale { SubTotal = 4250, Discount = 850, Total = 3400 };
        var net = SaleReturnService.NetShares(sale, [line]);
        var r = Returnable(line, net);
        Assert.Equal(3400m, r.NetRate);
        Assert.Equal(3400m, r.AmountFor(1));
    }

    [Fact]
    public void No_discount_returns_the_invoice_rate()
    {
        var line = Line(1, 2, 1300);
        var net = SaleReturnService.NetShares(new Sale { SubTotal = 2600, Total = 2600 }, [line]);
        Assert.Equal(1300m, Returnable(line, net).AmountFor(1));
    }

    [Fact]
    public void Shares_add_up_to_the_invoice_total_and_a_full_return_gives_it_all_back()
    {
        // 3 lines, an awkward discount: 1,300 + 3 × 280 + 999 = 3,139 − 100 = 3,039
        SaleLine[] lines = [Line(1, 1, 1300), Line(2, 3, 280), Line(3, 1, 999)];
        var sale = new Sale { SubTotal = 3139, Discount = 100, Total = 3039 };
        var net = SaleReturnService.NetShares(sale, [.. lines]);
        Assert.Equal(3039m, net.Values.Sum());
        Assert.All(net.Values, v => Assert.Equal(v, Math.Round(v, 2)));

        // Primer returned 1 at a time: the last piece gets what is left, so the three add up to its share exactly.
        var primer = lines[1];
        var first = Returnable(primer, net).AmountFor(1);
        var second = Returnable(primer, net, 1, first).AmountFor(1);
        var last = Returnable(primer, net, 2, first + second).AmountFor(1);
        Assert.Equal(net[2], first + second + last);

        // Everything at once = the invoice total.
        Assert.Equal(3039m, lines.Sum(l => Returnable(l, net).AmountFor(l.Qty)));
    }
}
