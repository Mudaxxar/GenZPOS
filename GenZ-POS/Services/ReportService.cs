using Dapper;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>
/// Heavy read-only queries written in SQL with Dapper. Each call borrows the connection of a
/// short-lived EF context (no separate connection string). Everything else uses EF Core.
/// All balances / ledgers / reports follow the "Money rules" in CLAUDE.md.
/// </summary>
public class ReportService(IDbContextFactory<AppDbContext> dbFactory)
{
    // On-hand qty per product; reused by several queries.
    // On-hand per variant (colour + size / barcode): the stock table StockMovements, summed.
    private const string StockSql = "SELECT StockItemId, SUM(QtyIn - QtyOut) AS Qty FROM StockMovements GROUP BY StockItemId";

    /// <summary>
    /// Customer balance (they owe us) = opening + Σ sales (Total − Paid) − Σ payments − Σ returns (Total − Refunded).
    /// Negative = advance. LastPaymentDate = latest payment received or invoice with money paid at the counter.
    /// </summary>
    private const string CustomerBalanceSql = """
        SELECT c.Id, c.Name, c.Phone, c.Address, c.Type, c.IsActive, c.OpeningBalance,
               c.OpeningBalance + ISNULL(s.Due, 0) - ISNULL(p.Received, 0) - ISNULL(r.Credit, 0) AS Balance,
               s.LastSaleDate,
               CASE WHEN p.LastPaymentDate IS NULL OR s.LastPaidSaleDate > p.LastPaymentDate
                    THEN s.LastPaidSaleDate ELSE p.LastPaymentDate END AS LastPaymentDate
        FROM Customers c
        LEFT JOIN (SELECT CustomerId, SUM(Total - Paid) AS Due, MAX(SaleDate) AS LastSaleDate,
                          MAX(CASE WHEN Paid > 0 THEN SaleDate END) AS LastPaidSaleDate
                   FROM Sales GROUP BY CustomerId) s ON s.CustomerId = c.Id
        LEFT JOIN (SELECT CustomerId, SUM(Amount) AS Received, MAX(PaymentDate) AS LastPaymentDate
                   FROM CustomerPayments GROUP BY CustomerId) p ON p.CustomerId = c.Id
        LEFT JOIN (SELECT CustomerId, SUM(Total - Refunded) AS Credit
                   FROM SaleReturns GROUP BY CustomerId) r ON r.CustomerId = c.Id
        """;

    /// <summary>
    /// Supplier balance (we owe them) = opening + Σ purchases (Total − Paid) − Σ payments − Σ returns (Total − Received).
    /// LastPaymentDate = latest supplier payment or bill with money paid on it.
    /// </summary>
    private const string SupplierBalanceSql = """
        SELECT s.Id, s.Name, s.Phone, s.Address, s.IsActive, s.OpeningBalance,
               s.OpeningBalance + ISNULL(pu.Due, 0) - ISNULL(pa.Paid, 0) - ISNULL(r.Credit, 0) AS Balance,
               pu.LastPurchaseDate,
               CASE WHEN pa.LastPaymentDate IS NULL OR pu.LastPaidPurchaseDate > pa.LastPaymentDate
                    THEN pu.LastPaidPurchaseDate ELSE pa.LastPaymentDate END AS LastPaymentDate
        FROM Suppliers s
        LEFT JOIN (SELECT SupplierId, SUM(Total - Paid) AS Due, MAX(PurchaseDate) AS LastPurchaseDate,
                          MAX(CASE WHEN Paid > 0 THEN PurchaseDate END) AS LastPaidPurchaseDate
                   FROM Purchases GROUP BY SupplierId) pu ON pu.SupplierId = s.Id
        LEFT JOIN (SELECT SupplierId, SUM(Amount) AS Paid, MAX(PaymentDate) AS LastPaymentDate
                   FROM SupplierPayments GROUP BY SupplierId) pa ON pa.SupplierId = s.Id
        LEFT JOIN (SELECT SupplierId, SUM(Total - Received) AS Credit
                   FROM PurchaseReturns GROUP BY SupplierId) r ON r.SupplierId = s.Id
        """;

    /// <summary>
    /// Exchange set-off per return (SaleService.CreateExchangeAsync): the part of a return's value that paid for the new
    /// invoice's items. It sits in both the invoice's Paid and the return's Refunded but no cash moved, so cash figures
    /// (Day Book, dashboard) take it off both. Per exchange invoice: min(Σ its returns, its Total), given to its returns
    /// in Id order. Columns: ReturnId, ExchangeSaleId, ReturnDate, SetOff.
    /// </summary>
    internal const string ExchangeSetOffSql = """
        SELECT r.Id AS ReturnId, r.ExchangeSaleId, r.ReturnDate,
               CASE WHEN x.SetOff <= r.Before THEN 0
                    WHEN x.SetOff - r.Before < r.Total THEN x.SetOff - r.Before
                    ELSE r.Total END AS SetOff
        FROM (SELECT Id, ExchangeSaleId, ReturnDate, Total,
                     SUM(Total) OVER (PARTITION BY ExchangeSaleId ORDER BY Id ROWS UNBOUNDED PRECEDING) - Total AS Before
              FROM SaleReturns WHERE ExchangeSaleId IS NOT NULL) r
        JOIN (SELECT s.Id, CASE WHEN rr.Returned < s.Total THEN rr.Returned ELSE s.Total END AS SetOff
              FROM Sales s
              JOIN (SELECT ExchangeSaleId, SUM(Total) AS Returned FROM SaleReturns
                    WHERE ExchangeSaleId IS NOT NULL GROUP BY ExchangeSaleId) rr ON rr.ExchangeSaleId = s.Id) x
          ON x.Id = r.ExchangeSaleId
        """;

    public async Task<DashboardStats> GetDashboardAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = DateTime.Today;
        // Stock is aggregated once (CTE) and only over active products, with the same rules as the stock report.
        return await db.Database.GetDbConnection().QuerySingleAsync<DashboardStats>($"""
            WITH s AS ({StockSql}),
            ps AS (
              SELECT v.ReorderLevel, v.PurchasePrice, ISNULL(s.Qty, 0) AS Qty
              FROM StockItems v JOIN Products p ON p.Id = v.ProductId LEFT JOIN s ON s.StockItemId = v.Id
              WHERE p.IsActive = 1 AND v.IsActive = 1
            ),
            st AS (
              SELECT ISNULL(SUM(CASE WHEN {LowRule("ReorderLevel", "Qty")} THEN 1 ELSE 0 END), 0) AS LowStockCount,
                     ISNULL(SUM(CASE WHEN Qty <= 0 THEN 1 ELSE 0 END), 0) AS OutOfStockCount,
                     ISNULL(SUM(Qty * PurchasePrice), 0) AS StockValue
              FROM ps
            ),
            cb AS ({CustomerBalanceSql}),
            sb AS ({SupplierBalanceSql}),
            xo AS ({ExchangeSetOffSql})
            SELECT
              (SELECT ISNULL(SUM(Total), 0) FROM Sales WHERE SaleDate >= @Today AND SaleDate < @Tomorrow)
                - (SELECT ISNULL(SUM(Total), 0) FROM SaleReturns WHERE ReturnDate >= @Today AND ReturnDate < @Tomorrow) AS SalesToday,
              (SELECT COUNT(*) FROM Sales WHERE SaleDate >= @Today AND SaleDate < @Tomorrow) AS InvoicesToday,
              (SELECT ISNULL(SUM(Total), 0) FROM SaleReturns WHERE ReturnDate >= @Today AND ReturnDate < @Tomorrow) AS ReturnsToday,
              (SELECT COUNT(*) FROM SaleReturns WHERE ReturnDate >= @Today AND ReturnDate < @Tomorrow) AS ReturnsTodayCount,
              (SELECT ISNULL(SUM(Total), 0) FROM SaleReturns WHERE ReturnDate >= @MonthStart AND ReturnDate < @Tomorrow) AS ReturnsThisMonth,
              (SELECT COUNT(*) FROM SaleReturns WHERE ReturnDate >= @MonthStart AND ReturnDate < @Tomorrow) AS ReturnsThisMonthCount,
              (SELECT ISNULL(SUM(Refunded), 0) FROM SaleReturns WHERE ReturnDate >= @MonthStart AND ReturnDate < @Tomorrow)
                - (SELECT ISNULL(SUM(SetOff), 0) FROM xo WHERE ReturnDate >= @MonthStart AND ReturnDate < @Tomorrow) AS RefundedThisMonth,
              (SELECT ISNULL(SUM(Total), 0) FROM Sales WHERE SaleDate >= @MonthStart AND SaleDate < @Tomorrow)
                - (SELECT ISNULL(SUM(Total), 0) FROM SaleReturns WHERE ReturnDate >= @MonthStart AND ReturnDate < @Tomorrow) AS SalesThisMonth,
              (SELECT ISNULL(SUM(l.Qty * l.Cost), 0) FROM SaleLines l JOIN Sales x ON x.Id = l.SaleId
                 WHERE x.SaleDate >= @MonthStart AND x.SaleDate < @Tomorrow)
                - (SELECT ISNULL(SUM(l.Qty * l.Cost), 0) FROM SaleReturnLines l JOIN SaleReturns x ON x.Id = l.SaleReturnId
                 WHERE x.ReturnDate >= @MonthStart AND x.ReturnDate < @Tomorrow) AS CostThisMonth,
              (SELECT ISNULL(SUM(Paid), 0) FROM Sales WHERE SaleDate >= @Today AND SaleDate < @Tomorrow)
                + (SELECT ISNULL(SUM(Amount), 0) FROM CustomerPayments WHERE Method = 0 AND PaymentDate >= @Today AND PaymentDate < @Tomorrow)
                + (SELECT ISNULL(SUM(Received), 0) FROM PurchaseReturns WHERE ReturnDate >= @Today AND ReturnDate < @Tomorrow)
                - (SELECT ISNULL(SUM(SetOff), 0) FROM xo WHERE ReturnDate >= @Today AND ReturnDate < @Tomorrow) AS CashInToday,
              (SELECT ISNULL(SUM(Paid), 0) FROM Purchases WHERE PurchaseDate >= @Today AND PurchaseDate < @Tomorrow)
                + (SELECT ISNULL(SUM(Amount), 0) FROM SupplierPayments WHERE Method = 0 AND PaymentDate >= @Today AND PaymentDate < @Tomorrow)
                + (SELECT ISNULL(SUM(Refunded), 0) FROM SaleReturns WHERE ReturnDate >= @Today AND ReturnDate < @Tomorrow)
                - (SELECT ISNULL(SUM(SetOff), 0) FROM xo WHERE ReturnDate >= @Today AND ReturnDate < @Tomorrow)
                + (SELECT ISNULL(SUM(Amount), 0) FROM Expenses WHERE Method = 0 AND ExpenseDate >= @Today AND ExpenseDate < @Tomorrow) AS CashOutToday,
              (SELECT ISNULL(SUM(Amount), 0) FROM Expenses WHERE ExpenseDate >= @MonthStart AND ExpenseDate < @Tomorrow) AS ExpensesThisMonth,
              (SELECT ISNULL(SUM(Amount), 0) FROM Expenses WHERE ExpenseDate >= @Today AND ExpenseDate < @Tomorrow) AS ExpensesToday,
              (SELECT COUNT(*) FROM Expenses WHERE ExpenseDate >= @MonthStart AND ExpenseDate < @Tomorrow) AS ExpensesThisMonthCount,
              (SELECT ISNULL(SUM(Amount), 0) FROM CustomerPayments WHERE Method <> 0 AND PaymentDate >= @Today AND PaymentDate < @Tomorrow) AS NonCashInToday,
              (SELECT ISNULL(SUM(Balance), 0) FROM cb WHERE Balance > 0) AS Receivable,
              (SELECT COUNT(*) FROM cb WHERE Balance > 0) AS ReceivableCount,
              (SELECT ISNULL(-SUM(Balance), 0) FROM cb WHERE Balance < 0) AS CustomerAdvances,
              (SELECT ISNULL(SUM(Balance), 0) FROM sb WHERE Balance > 0) AS Payable,
              (SELECT COUNT(*) FROM sb WHERE Balance > 0) AS PayableCount,
              (SELECT ISNULL(-SUM(Balance), 0) FROM sb WHERE Balance < 0) AS SupplierAdvances,
              (SELECT ISNULL(SUM(Paid), 0) FROM Purchases WHERE PurchaseDate >= @Today AND PurchaseDate < @Tomorrow)
                + (SELECT ISNULL(SUM(Amount), 0) FROM SupplierPayments WHERE PaymentDate >= @Today AND PaymentDate < @Tomorrow) AS PaidToSuppliersToday,
              st.LowStockCount, st.OutOfStockCount, st.StockValue
            FROM st
            """,
            new { Today = today, Tomorrow = today.AddDays(1), MonthStart = new DateTime(today.Year, today.Month, 1) });
    }

    /// <summary>Current balance for every customer (Money rules: opening + invoices due − payments − returns credited).</summary>
    public async Task<List<CustomerBalance>> GetCustomerBalancesAsync(string? search = null, int? customerId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Database.GetDbConnection().QueryAsync<CustomerBalance>($"""
            SELECT * FROM ({CustomerBalanceSql}) b
            WHERE (@Search IS NULL OR b.Name LIKE @Search OR b.Phone LIKE @Search)
              AND (@CustomerId IS NULL OR b.Id = @CustomerId)
            ORDER BY b.Name
            """, new { Search = Like(search), CustomerId = customerId });
        return rows.ToList();
    }

    public async Task<decimal> GetCustomerBalanceAsync(int customerId) =>
        (await GetCustomerBalancesAsync(customerId: customerId)).FirstOrDefault()?.Balance ?? 0;

    /// <summary>What we owe each supplier (Money rules: opening + unpaid part of bills − payments − returns credited).</summary>
    public async Task<List<SupplierBalance>> GetSupplierBalancesAsync(string? search = null, int? supplierId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Database.GetDbConnection().QueryAsync<SupplierBalance>($"""
            SELECT * FROM ({SupplierBalanceSql}) b
            WHERE (@Search IS NULL OR b.Name LIKE @Search OR b.Phone LIKE @Search)
              AND (@SupplierId IS NULL OR b.Id = @SupplierId)
            ORDER BY b.Name
            """, new { Search = Like(search), SupplierId = supplierId });
        return rows.ToList();
    }

    public async Task<decimal> GetSupplierBalanceAsync(int supplierId) =>
        (await GetSupplierBalancesAsync(supplierId: supplierId)).FirstOrDefault()?.Balance ?? 0;

    /// <summary>
    /// Supplier statement: balance brought forward + entries in range with running balance.
    /// Debit = we owe more (bill, cash received back on a return); Credit = we owe less (paid on bill, payment, return).
    /// Kinds: Purchase, Paid on bill, Payment, Return, Cash received back (RefId = purchase / payment / purchase return id).
    /// </summary>
    public async Task<CustomerLedger> GetSupplierLedgerAsync(int supplierId, DateTime from, DateTime to)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        var p = new { SupplierId = supplierId, From = from.Date, ToExclusive = to.Date.AddDays(1) };

        string entries = $"""
            SELECT pu.PurchaseDate AS [Date], 1 AS Seq, 'Purchase' AS Kind, pu.Id AS RefId,
                   CONCAT('Purchase #', pu.Id, CASE WHEN pu.BillNo IS NULL THEN '' ELSE CONCAT(' (bill ', pu.BillNo, ')') END) AS Description,
                   pu.Total AS Debit, CAST(0 AS decimal(18,2)) AS Credit, pu.CreatedAt
            FROM Purchases pu WHERE pu.SupplierId = @SupplierId
            UNION ALL
            SELECT pu.PurchaseDate, 2, 'Paid on bill', pu.Id, CONCAT('Paid on purchase #', pu.Id), 0, pu.Paid, pu.CreatedAt
            FROM Purchases pu WHERE pu.SupplierId = @SupplierId AND pu.Paid > 0
            UNION ALL
            SELECT sp.PaymentDate, 3, 'Payment', sp.Id,
                   CONCAT('Payment made (', {MethodSql("sp.Method")}, ')',
                          CASE WHEN sp.Notes IS NULL THEN '' ELSE CONCAT(' - ', sp.Notes) END),
                   0, sp.Amount, sp.CreatedAt
            FROM SupplierPayments sp WHERE sp.SupplierId = @SupplierId
            UNION ALL
            SELECT pr.ReturnDate, 4, 'Return', pr.Id,
                   CONCAT('Purchase return #', pr.Id, CASE WHEN pr.PurchaseId IS NULL THEN '' ELSE CONCAT(' (purchase #', pr.PurchaseId, ')') END),
                   0, pr.Total, pr.CreatedAt
            FROM PurchaseReturns pr WHERE pr.SupplierId = @SupplierId
            UNION ALL
            SELECT pr.ReturnDate, 5, 'Cash received back', pr.Id, CONCAT('Cash received back on return #', pr.Id), pr.Received, 0, pr.CreatedAt
            FROM PurchaseReturns pr WHERE pr.SupplierId = @SupplierId AND pr.Received > 0
            """;

        var opening = await conn.QuerySingleAsync<decimal>($"""
            SELECT ISNULL((SELECT OpeningBalance FROM Suppliers WHERE Id = @SupplierId), 0)
                 + ISNULL((SELECT SUM(Debit - Credit) FROM ({entries}) e WHERE e.[Date] < @From), 0)
            """, p);

        var rows = (await conn.QueryAsync<LedgerRow>($"""
            SELECT * FROM ({entries}) e
            WHERE e.[Date] >= @From AND e.[Date] < @ToExclusive
            ORDER BY e.[Date], e.CreatedAt, e.Seq, e.RefId
            """, p)).ToList();

        var running = opening;
        foreach (var r in rows)
        {
            running += r.Debit - r.Credit;
            r.Balance = running;
        }
        return new CustomerLedger(opening, rows, running);
    }

    /// <summary>
    /// Customer statement: balance brought forward + entries in range with running balance.
    /// Debit = owes more (invoice, refund); Credit = owes less (paid at counter, payment, return).
    /// Kinds: Invoice, Paid at counter, Payment, Return, Refund (RefId = sale / payment / sale return id).
    /// </summary>
    public async Task<CustomerLedger> GetCustomerLedgerAsync(int customerId, DateTime from, DateTime to)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        var p = new { CustomerId = customerId, From = from.Date, ToExclusive = to.Date.AddDays(1) };

        string entries = $"""
            SELECT s.SaleDate AS [Date], 1 AS Seq, 'Invoice' AS Kind, s.Id AS RefId,
                   CONCAT('Invoice #', s.Id) AS Description, s.Total AS Debit, CAST(0 AS decimal(18,2)) AS Credit, s.CreatedAt
            FROM Sales s WHERE s.CustomerId = @CustomerId
            UNION ALL
            SELECT s.SaleDate, 2, 'Paid at counter', s.Id, CONCAT('Paid on invoice #', s.Id), 0, s.Paid, s.CreatedAt
            FROM Sales s WHERE s.CustomerId = @CustomerId AND s.Paid > 0
            UNION ALL
            SELECT cp.PaymentDate, 3, 'Payment', cp.Id,
                   CONCAT('Payment received (', {MethodSql("cp.Method")}, ')',
                          CASE WHEN cp.Notes IS NULL THEN '' ELSE CONCAT(' - ', cp.Notes) END),
                   0, cp.Amount, cp.CreatedAt
            FROM CustomerPayments cp WHERE cp.CustomerId = @CustomerId
            UNION ALL
            SELECT r.ReturnDate, 4, 'Return', r.Id,
                   CONCAT('Sales return #', r.Id, CASE WHEN r.SaleId IS NULL THEN '' ELSE CONCAT(' (invoice #', r.SaleId, ')') END),
                   0, r.Total, r.CreatedAt
            FROM SaleReturns r WHERE r.CustomerId = @CustomerId
            UNION ALL
            SELECT r.ReturnDate, 5, 'Refund', r.Id, CONCAT('Cash refunded on return #', r.Id), r.Refunded, 0, r.CreatedAt
            FROM SaleReturns r WHERE r.CustomerId = @CustomerId AND r.Refunded > 0
            """;

        var opening = await conn.QuerySingleAsync<decimal>($"""
            SELECT ISNULL((SELECT OpeningBalance FROM Customers WHERE Id = @CustomerId), 0)
                 + ISNULL((SELECT SUM(Debit - Credit) FROM ({entries}) e WHERE e.[Date] < @From), 0)
            """, p);

        var rows = (await conn.QueryAsync<LedgerRow>($"""
            SELECT * FROM ({entries}) e
            WHERE e.[Date] >= @From AND e.[Date] < @ToExclusive
            ORDER BY e.[Date], e.CreatedAt, e.Seq, e.RefId
            """, p)).ToList();

        var running = opening;
        foreach (var r in rows)
        {
            running += r.Debit - r.Credit;
            r.Balance = running;
        }

        return new CustomerLedger(opening, rows, running);
    }

    /// <summary>
    /// Day book: every money movement in the period (Money rules "Cash (Day Book)"), plus the period's document totals.
    /// </summary>
    public async Task<DayBookReport> GetDayBookAsync(DateTime from, DateTime to)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        var p = new { From = from.Date, ToExclusive = to.Date.AddDays(1) };

        // Exchanges: only the cash that changed hands (the set-off is taken off the invoice's Paid and the return's Refunded).
        var rows = (await conn.QueryAsync<DayBookRow>($"""
            WITH xo AS ({ExchangeSetOffSql}),
            xs AS (SELECT ExchangeSaleId, SUM(SetOff) AS SetOff FROM xo GROUP BY ExchangeSaleId)
            SELECT * FROM (
              SELECT s.SaleDate AS [Date], s.CreatedAt, 'Sale' AS Kind,
                     CASE WHEN xs.ExchangeSaleId IS NULL THEN 'Sale (paid at counter)' ELSE 'Exchange (difference paid)' END AS [Type], s.Id AS RefId,
                     s.CustomerId AS PartyId, c.Name AS Party, 'Cash' AS Method, s.Notes,
                     s.Paid - ISNULL(xs.SetOff, 0) AS CashIn, CAST(0 AS decimal(18,2)) AS CashOut,
                     CAST(0 AS decimal(18,2)) AS NonCashIn, CAST(0 AS decimal(18,2)) AS NonCashOut
              FROM Sales s JOIN Customers c ON c.Id = s.CustomerId
              LEFT JOIN xs ON xs.ExchangeSaleId = s.Id
              WHERE s.Paid - ISNULL(xs.SetOff, 0) > 0 AND s.SaleDate >= @From AND s.SaleDate < @ToExclusive
              UNION ALL
              SELECT cp.PaymentDate, cp.CreatedAt, 'CustomerPayment', 'Payment received', cp.Id,
                     cp.CustomerId, c.Name, {MethodSql("cp.Method")}, cp.Notes,
                     CASE WHEN cp.Method = 0 THEN cp.Amount ELSE 0 END, 0,
                     CASE WHEN cp.Method <> 0 THEN cp.Amount ELSE 0 END, 0
              FROM CustomerPayments cp JOIN Customers c ON c.Id = cp.CustomerId
              WHERE cp.PaymentDate >= @From AND cp.PaymentDate < @ToExclusive
              UNION ALL
              SELECT r.ReturnDate, r.CreatedAt, 'SaleReturn',
                     CASE WHEN r.ExchangeSaleId IS NULL THEN 'Refund on sales return' ELSE 'Exchange (difference given back)' END, r.Id,
                     r.CustomerId, c.Name, 'Cash', r.Notes, 0, r.Refunded - ISNULL(xo.SetOff, 0), 0, 0
              FROM SaleReturns r JOIN Customers c ON c.Id = r.CustomerId
              LEFT JOIN xo ON xo.ReturnId = r.Id
              WHERE r.Refunded - ISNULL(xo.SetOff, 0) > 0 AND r.ReturnDate >= @From AND r.ReturnDate < @ToExclusive
              UNION ALL
              SELECT pu.PurchaseDate, pu.CreatedAt, 'Purchase', 'Paid on purchase bill', pu.Id,
                     pu.SupplierId, su.Name, 'Cash', pu.Notes, 0, pu.Paid, 0, 0
              FROM Purchases pu JOIN Suppliers su ON su.Id = pu.SupplierId
              WHERE pu.Paid > 0 AND pu.PurchaseDate >= @From AND pu.PurchaseDate < @ToExclusive
              UNION ALL
              SELECT sp.PaymentDate, sp.CreatedAt, 'SupplierPayment', 'Payment to supplier', sp.Id,
                     sp.SupplierId, su.Name, {MethodSql("sp.Method")}, sp.Notes,
                     0, CASE WHEN sp.Method = 0 THEN sp.Amount ELSE 0 END,
                     0, CASE WHEN sp.Method <> 0 THEN sp.Amount ELSE 0 END
              FROM SupplierPayments sp JOIN Suppliers su ON su.Id = sp.SupplierId
              WHERE sp.PaymentDate >= @From AND sp.PaymentDate < @ToExclusive
              UNION ALL
              SELECT pr.ReturnDate, pr.CreatedAt, 'PurchaseReturn', 'Cash back on purchase return', pr.Id,
                     pr.SupplierId, su.Name, 'Cash', pr.Notes, pr.Received, 0, 0, 0
              FROM PurchaseReturns pr JOIN Suppliers su ON su.Id = pr.SupplierId
              WHERE pr.Received > 0 AND pr.ReturnDate >= @From AND pr.ReturnDate < @ToExclusive
              UNION ALL
              SELECT e.ExpenseDate, e.CreatedAt, 'Expense', CONCAT('Expense: ', ec.Name), e.Id,
                     0, ISNULL(e.PaidTo, ''), {MethodSql("e.Method")}, e.Notes,
                     0, CASE WHEN e.Method = 0 THEN e.Amount ELSE 0 END,
                     0, CASE WHEN e.Method <> 0 THEN e.Amount ELSE 0 END
              FROM Expenses e JOIN ExpenseCategories ec ON ec.Id = e.CategoryId
              WHERE e.ExpenseDate >= @From AND e.ExpenseDate < @ToExclusive
            ) d
            ORDER BY d.[Date], d.CreatedAt, d.Kind, d.RefId
            """, p)).ToList();

        var totals = await conn.QuerySingleAsync<DayBookTotals>("""
            SELECT
              (SELECT COUNT(*) FROM Sales WHERE SaleDate >= @From AND SaleDate < @ToExclusive) AS Invoices,
              (SELECT ISNULL(SUM(Total), 0) FROM Sales WHERE SaleDate >= @From AND SaleDate < @ToExclusive) AS Sales,
              (SELECT ISNULL(SUM(CASE WHEN Total > Paid THEN Total - Paid ELSE 0 END), 0) FROM Sales
                 WHERE SaleDate >= @From AND SaleDate < @ToExclusive) AS CreditSales,
              (SELECT ISNULL(SUM(Total), 0) FROM SaleReturns WHERE ReturnDate >= @From AND ReturnDate < @ToExclusive) AS SaleReturns,
              (SELECT ISNULL(SUM(Total), 0) FROM Purchases WHERE PurchaseDate >= @From AND PurchaseDate < @ToExclusive) AS Purchases,
              (SELECT ISNULL(SUM(Total), 0) FROM PurchaseReturns WHERE ReturnDate >= @From AND ReturnDate < @ToExclusive) AS PurchaseReturns,
              (SELECT ISNULL(SUM(Amount), 0) FROM Expenses WHERE ExpenseDate >= @From AND ExpenseDate < @ToExclusive) AS Expenses
            """, p);

        return new DayBookReport(rows, totals);
    }

    /// <summary>Sales, returns, cost and gross profit per day or month (Money rules "Cost/profit").</summary>
    public async Task<List<SalesProfitRow>> GetSalesProfitAsync(DateTime from, DateTime to, bool byMonth)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Database.GetDbConnection().QueryAsync<SalesProfitRow>($"""
            SELECT x.Period, SUM(x.Invoices) AS Invoices, SUM(x.Gross) AS GrossSales, SUM(x.Discount) AS Discount,
                   SUM(x.SalesTotal) AS SalesTotal, SUM(x.Returns) AS Returns,
                   SUM(x.SaleCost) AS SaleCost, SUM(x.ReturnCost) AS ReturnCost, SUM(x.Expenses) AS Expenses
            FROM (
              SELECT {PeriodSql("s.SaleDate")} AS Period, 1 AS Invoices, s.SubTotal AS Gross, s.Discount, s.Total AS SalesTotal,
                     CAST(0 AS decimal(18,2)) AS Returns,
                     (SELECT ISNULL(SUM(l.Qty * l.Cost), 0) FROM SaleLines l WHERE l.SaleId = s.Id) AS SaleCost,
                     CAST(0 AS decimal(18,2)) AS ReturnCost, CAST(0 AS decimal(18,2)) AS Expenses
              FROM Sales s
              WHERE s.SaleDate >= @From AND s.SaleDate < @ToExclusive
              UNION ALL
              SELECT {PeriodSql("r.ReturnDate")}, 0, 0, 0, 0, r.Total, 0,
                     (SELECT ISNULL(SUM(l.Qty * l.Cost), 0) FROM SaleReturnLines l WHERE l.SaleReturnId = r.Id), 0
              FROM SaleReturns r
              WHERE r.ReturnDate >= @From AND r.ReturnDate < @ToExclusive
              UNION ALL
              SELECT {PeriodSql("e.ExpenseDate")}, 0, 0, 0, 0, 0, 0, 0, e.Amount
              FROM Expenses e
              WHERE e.ExpenseDate >= @From AND e.ExpenseDate < @ToExclusive
            ) x
            GROUP BY x.Period
            ORDER BY x.Period
            """, new { From = from.Date, ToExclusive = to.Date.AddDays(1), ByMonth = byMonth });
        return rows.ToList();
    }

    /// <summary>
    /// Sales by product / company / category. Invoice discount is spread over its lines pro-rata
    /// (line Amount × Total / SubTotal); returns count at their own Amount and Cost.
    /// </summary>
    public async Task<List<ItemSalesRow>> GetItemSalesAsync(DateTime from, DateTime to, ItemSalesGroup group, int? companyId, int? categoryId)
    {
        var (key, name, groupBy) = group switch
        {
            ItemSalesGroup.Company => ("co.Id", "co.Name", "co.Id, co.Name"),
            ItemSalesGroup.Category => ("c.Id", "c.Name", "c.Id, c.Name"),
            // "Product" = the product master (all its stock items together).
            _ => ("p.Id",
                  "CONCAT(co.Name, ' · ', p.Name, CASE WHEN p.PackSize IS NULL OR p.PackSize = '' THEN '' ELSE CONCAT(' · ', p.PackSize) END)",
                  "p.Id, co.Name, p.Name, p.PackSize, p.Unit"),
        };
        var unit = group == ItemSalesGroup.Product ? "p.Unit" : "CAST(NULL AS nvarchar(20))";

        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Database.GetDbConnection().QueryAsync<ItemSalesRow>($"""
            WITH x AS (
              SELECT l.StockItemId, l.Qty AS QtySold, CAST(0 AS decimal(18,2)) AS QtyReturned,
                     ISNULL(l.Amount * s.Total / NULLIF(s.SubTotal, 0), 0) AS Sales, l.Qty * l.Cost AS Cost
              FROM SaleLines l JOIN Sales s ON s.Id = l.SaleId
              WHERE s.SaleDate >= @From AND s.SaleDate < @ToExclusive
              UNION ALL
              SELECT l.StockItemId, 0, l.Qty, -l.Amount, -(l.Qty * l.Cost)
              FROM SaleReturnLines l JOIN SaleReturns r ON r.Id = l.SaleReturnId
              WHERE r.ReturnDate >= @From AND r.ReturnDate < @ToExclusive
            )
            SELECT {key} AS [Key], {name} AS Name, {unit} AS Unit,
                   SUM(x.QtySold) AS QtySold, SUM(x.QtyReturned) AS QtyReturned,
                   CAST(SUM(x.Sales) AS decimal(18,2)) AS NetSales, CAST(SUM(x.Cost) AS decimal(18,2)) AS Cost
            FROM x
            JOIN StockItems v ON v.Id = x.StockItemId
            JOIN Products p ON p.Id = v.ProductId
            JOIN Companies co ON co.Id = p.CompanyId
            JOIN SubCategories sc ON sc.Id = p.SubCategoryId
            JOIN Categories c ON c.Id = sc.CategoryId
            WHERE (@CompanyId IS NULL OR p.CompanyId = @CompanyId)
              AND (@CategoryId IS NULL OR sc.CategoryId = @CategoryId)
            GROUP BY {groupBy}
            ORDER BY NetSales DESC, Name
            """, new { From = from.Date, ToExclusive = to.Date.AddDays(1), CompanyId = companyId, CategoryId = categoryId });
        return rows.ToList();
    }

    /// <summary>Stock card for one variant (colour + size): opening qty before From, movements in range with running balance.</summary>
    public async Task<StockCard> GetStockCardAsync(int variantId, DateTime from, DateTime to)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        var p = new { StockItemId = variantId, From = from.Date, ToExclusive = to.Date.AddDays(1) };

        var product = await conn.QuerySingleOrDefaultAsync<StockCardProduct>($"""
            SELECT v.Id, {VariantNameSql} AS DisplayName, v.Barcode, v.ArticleNo,
                   p.Unit, v.PurchasePrice, v.SalePrice, v.ReorderLevel, CAST(CASE WHEN p.IsActive = 1 AND v.IsActive = 1 THEN 1 ELSE 0 END AS bit) AS IsActive,
                   ISNULL((SELECT SUM(QtyIn - QtyOut) FROM StockMovements WHERE StockItemId = v.Id), 0) AS OnHand
            FROM StockItems v JOIN Products p ON p.Id = v.ProductId JOIN Companies co ON co.Id = p.CompanyId
            WHERE v.Id = @StockItemId
            """, p);

        var opening = await conn.QuerySingleAsync<decimal>(
            "SELECT ISNULL(SUM(QtyIn - QtyOut), 0) FROM StockMovements WHERE StockItemId = @StockItemId AND MovementDate < @From", p);

        var rows = (await conn.QueryAsync<StockCardRow>("""
            SELECT m.Id, m.MovementDate AS [Date], m.CreatedAt, m.Type, m.RefId, m.Notes, m.QtyIn, m.QtyOut,
                   CASE m.Type WHEN 1 THEN su.Name WHEN 2 THEN cu.Name WHEN 5 THEN rc.Name WHEN 6 THEN rs.Name END AS Party
            FROM StockMovements m
            LEFT JOIN Purchases pu ON m.Type = 1 AND pu.Id = m.RefId
            LEFT JOIN Suppliers su ON su.Id = pu.SupplierId
            LEFT JOIN Sales s ON m.Type = 2 AND s.Id = m.RefId
            LEFT JOIN Customers cu ON cu.Id = s.CustomerId
            LEFT JOIN SaleReturns sr ON m.Type = 5 AND sr.Id = m.RefId
            LEFT JOIN Customers rc ON rc.Id = sr.CustomerId
            LEFT JOIN PurchaseReturns pr ON m.Type = 6 AND pr.Id = m.RefId
            LEFT JOIN Suppliers rs ON rs.Id = pr.SupplierId
            WHERE m.StockItemId = @StockItemId AND m.MovementDate >= @From AND m.MovementDate < @ToExclusive
            ORDER BY m.MovementDate, m.CreatedAt, m.Id
            """, p)).ToList();

        var running = opening;
        foreach (var r in rows)
        {
            running += r.QtyIn - r.QtyOut;
            r.Balance = running;
        }
        return new StockCard(product, opening, rows, running);
    }

    /// <summary>
    /// Stock items with on-hand qty and value, grouped by article no.: <paramref name="skip"/> / <paramref name="take"/>
    /// count ARTICLES (all stock items of an article are on one page). TotalCount = matching articles; TotalValue and
    /// TotalPieces cover everything that matches.
    /// </summary>
    public async Task<StockReportPage> GetStockReportAsync(StockReportFilter f, int skip, int take)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = (await db.Database.GetDbConnection().QueryAsync<StockReportRow>($"""
            WITH r0 AS (
              SELECT v.Id, p.Id AS ProductId, co.Name AS Company, c.Name AS Category, sc.Name AS SubCategory,
                     p.Name, v.ArticleNo, v.Colour, v.Size, v.Barcode, p.PackSize, p.Unit, p.StockMode, v.PurchasePrice, v.SalePrice, v.ReorderLevel,
                     ISNULL(s.Qty, 0) AS OnHand,
                     ISNULL(s.Qty, 0) * v.PurchasePrice AS StockValue
              FROM StockItems v
              JOIN Products p ON p.Id = v.ProductId
              JOIN Companies co ON co.Id = p.CompanyId
              JOIN SubCategories sc ON sc.Id = p.SubCategoryId
              JOIN Categories c ON c.Id = sc.CategoryId
              LEFT JOIN ({StockSql}) s ON s.StockItemId = v.Id
              WHERE p.IsActive = 1 AND v.IsActive = 1
                AND (@Search IS NULL OR p.Name LIKE @Search OR v.Colour LIKE @Search OR co.Name LIKE @Search
                     OR v.Barcode = @Raw OR v.ArticleNo LIKE @Search)
                AND (@CompanyId IS NULL OR p.CompanyId = @CompanyId)
                AND (@CategoryId IS NULL OR sc.CategoryId = @CategoryId)
                AND (@Stock = 0
                     OR (@Stock = 1 AND {LowRule("v.ReorderLevel", "ISNULL(s.Qty, 0)")})
                     OR (@Stock = 2 AND ISNULL(s.Qty, 0) <= 0)
                     OR (@Stock = 3 AND ISNULL(s.Qty, 0) > 0))
            ),
            -- totals over everything that matches
            r AS (
              SELECT *, SUM(StockValue) OVER () AS TotalValue, SUM(CASE WHEN OnHand > 0 THEN OnHand ELSE 0 END) OVER () AS TotalPieces
              FROM r0
            ),
            -- one row per article (an article no. belongs to one product), numbered for paging
            a AS (
              SELECT ArticleNo, ROW_NUMBER() OVER (ORDER BY MIN(Company), MIN(Name), ArticleNo) AS Rn, COUNT(*) OVER () AS TotalCount
              FROM r0
              GROUP BY ArticleNo
            ),
            -- the article's own totals over ALL its active pieces (not only the filtered ones), for the group header
            art AS (
              SELECT v.ArticleNo,
                     SUM(CASE WHEN ISNULL(s.Qty, 0) > 0 THEN s.Qty ELSE 0 END) AS ArticleInStock,
                     SUM(CASE WHEN ISNULL(s.Qty, 0) > 0 THEN 0 ELSE 1 END) AS ArticleSold,
                     SUM(CASE WHEN ISNULL(s.Qty, 0) > 0 THEN s.Qty * v.PurchasePrice ELSE 0 END) AS ArticleValue
              FROM StockItems v
              JOIN Products p ON p.Id = v.ProductId
              LEFT JOIN ({StockSql}) s ON s.StockItemId = v.Id
              WHERE p.IsActive = 1 AND v.IsActive = 1 AND v.ArticleNo IN (SELECT ArticleNo FROM a WHERE Rn > @Skip AND Rn <= CAST(@Skip AS bigint) + @Take)
              GROUP BY v.ArticleNo
            )
            SELECT r.*, a.TotalCount, art.ArticleInStock, art.ArticleSold, art.ArticleValue
            FROM r JOIN a ON a.ArticleNo = r.ArticleNo
            JOIN art ON art.ArticleNo = r.ArticleNo
            WHERE a.Rn > @Skip AND a.Rn <= CAST(@Skip AS bigint) + @Take
            ORDER BY a.Rn, r.Colour, LEN(r.Size), r.Size, r.Barcode, r.Id
            """,
            new { Search = Like(f.Search), Raw = f.Search?.Trim(), f.CompanyId, f.CategoryId, Stock = (int)f.Stock, Skip = skip, Take = take }))
            .ToList();

        var first = rows.FirstOrDefault();
        return new StockReportPage(rows, first?.TotalCount ?? 0, first?.TotalValue ?? 0, first?.TotalPieces ?? 0);
    }

    /// <summary>
    /// Every active variant (colour + size / barcode) with its product and on-hand qty, for the item pickers on the
    /// sale / purchase screens and barcode scanning. <see cref="ProductLookup.Id"/> = the variant id.
    /// </summary>
    public async Task<List<ProductLookup>> GetProductLookupAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Database.GetDbConnection().QueryAsync<ProductLookup>($"""
            SELECT v.Id, p.Id AS ProductId,
                   -- article no. and barcode at the end so the item pickers can be searched by them
                   CONCAT({VariantNameSql}, ' · ', v.ArticleNo, ' · ', v.Barcode) AS DisplayName,
                   co.Name AS Company, p.Name, v.Colour, v.Size,
                   v.Barcode, v.ArticleNo, p.Unit, v.PurchasePrice, v.SalePrice, p.MaxDiscountPercent, ISNULL(s.Qty, 0) AS OnHand,
                   p.StockMode, p.PurchaseUnit, p.PurchaseUnitQty
            FROM StockItems v
            JOIN Products p ON p.Id = v.ProductId
            JOIN Companies co ON co.Id = p.CompanyId
            LEFT JOIN ({StockSql}) s ON s.StockItemId = v.Id
            WHERE p.IsActive = 1 AND v.IsActive = 1
            ORDER BY co.Name, p.Name, v.ArticleNo, v.Colour, v.Size, v.Id
            """);
        return rows.ToList();
    }

    /// <summary>
    /// Active products (the masters) for the purchase item picker, with their total stock and the latest stock item's
    /// prices (to prefill cost / retail).
    /// </summary>
    public async Task<List<ProductPick>> GetProductPicksAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Database.GetDbConnection().QueryAsync<ProductPick>("""
            SELECT p.Id,
                   CONCAT(co.Name, ' · ', p.Name, CASE WHEN p.PackSize IS NULL OR p.PackSize = '' THEN '' ELSE CONCAT(' · ', p.PackSize) END) AS DisplayName,
                   p.Unit, p.StockMode, p.PurchaseUnit, p.PurchaseUnitQty, ISNULL(st.Qty, 0) AS OnHand,
                   (SELECT TOP 1 i.Id FROM StockItems i WHERE i.ProductId = p.Id ORDER BY i.Id) AS FirstStockItemId,
                   ISNULL(last.PurchasePrice, 0) AS PurchasePrice, ISNULL(last.SalePrice, 0) AS SalePrice, ISNULL(last.ReorderLevel, 0) AS ReorderLevel
            FROM Products p
            JOIN Companies co ON co.Id = p.CompanyId
            OUTER APPLY (SELECT TOP 1 i.PurchasePrice, i.SalePrice, i.ReorderLevel FROM StockItems i WHERE i.ProductId = p.Id ORDER BY i.Id DESC) last
            LEFT JOIN (SELECT i.ProductId, SUM(m.QtyIn - m.QtyOut) AS Qty
                       FROM StockMovements m JOIN StockItems i ON i.Id = m.StockItemId GROUP BY i.ProductId) st ON st.ProductId = p.Id
            WHERE p.IsActive = 1
            ORDER BY co.Name, p.Name, p.PackSize, p.Id
            """);
        return rows.ToList();
    }

    /// <summary>"Zee Shoes · Shoes · Black · 42" in SQL (aliases v = StockItems, p = Products, co = Companies).</summary>
    private const string VariantNameSql = """
        CONCAT(co.Name, ' · ', p.Name,
               CASE WHEN p.PackSize IS NULL OR p.PackSize = '' THEN '' ELSE CONCAT(' · ', p.PackSize) END,
               CASE WHEN v.Colour IS NULL OR v.Colour = '' THEN '' ELSE CONCAT(' · ', v.Colour) END,
               CASE WHEN v.Size IS NULL OR v.Size = '' THEN '' ELSE CONCAT(' · ', v.Size) END)
        """;

    /// <summary>Contains-pattern for LIKE; the user's [ % _ are escaped so they match literally.</summary>
    private static string? Like(string? s) => string.IsNullOrWhiteSpace(s)
        ? null
        : "%" + s.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

    /// <summary>Low-stock rule in SQL: a reorder level is set and on-hand is at or below it (mirrors <see cref="StockReportRow.IsLow"/>).</summary>
    private static string LowRule(string reorderLevel, string qty) => $"({reorderLevel} > 0 AND {qty} <= {reorderLevel})";

    /// <summary>PaymentMethod enum value → text in SQL.</summary>
    private static string MethodSql(string column) =>
        $"CASE {column} WHEN 0 THEN 'Cash' WHEN 1 THEN 'Bank' WHEN 2 THEN 'Cheque' ELSE 'Other' END";

    /// <summary>Day (date) or first of the month, depending on @ByMonth.</summary>
    private static string PeriodSql(string column) =>
        $"CASE WHEN @ByMonth = 1 THEN DATEFROMPARTS(YEAR({column}), MONTH({column}), 1) ELSE CAST({column} AS date) END";
}

public class DashboardStats
{
    /// <summary>Today's invoices − today's sales returns.</summary>
    public decimal SalesToday { get; set; }
    public int InvoicesToday { get; set; }
    /// <summary>Sales returns dated today (already taken off <see cref="SalesToday"/>).</summary>
    public decimal ReturnsToday { get; set; }
    public int ReturnsTodayCount { get; set; }
    /// <summary>Sales returns dated this month (already taken off <see cref="SalesThisMonth"/>).</summary>
    public decimal ReturnsThisMonth { get; set; }
    public int ReturnsThisMonthCount { get; set; }
    /// <summary>Cash paid back on this month's returns (the rest went off customer balances).</summary>
    public decimal RefundedThisMonth { get; set; }
    /// <summary>This month's returns as a share of this month's invoices (before returns), in %.</summary>
    public decimal ReturnRateThisMonth => SalesThisMonth + ReturnsThisMonth is var gross and > 0
        ? Math.Round(ReturnsThisMonth / gross * 100, 1) : 0;
    /// <summary>This month's invoices − sales returns.</summary>
    public decimal SalesThisMonth { get; set; }
    /// <summary>Cost of goods sold this month (sale lines − returned lines, at recorded cost).</summary>
    public decimal CostThisMonth { get; set; }
    public decimal GrossProfitThisMonth => SalesThisMonth - CostThisMonth;
    /// <summary>Shop expenses this month (any method).</summary>
    public decimal ExpensesThisMonth { get; set; }
    public int ExpensesThisMonthCount { get; set; }
    /// <summary>Shop expenses dated today (any method).</summary>
    public decimal ExpensesToday { get; set; }
    /// <summary>Net profit = gross profit − expenses.</summary>
    public decimal NetProfitThisMonth => GrossProfitThisMonth - ExpensesThisMonth;
    /// <summary>Real cash in today: paid at counter + cash payments received + cash back on purchase returns.</summary>
    public decimal CashInToday { get; set; }
    /// <summary>Real cash out today: paid on bills + cash supplier payments + refunds on sales returns.</summary>
    public decimal CashOutToday { get; set; }
    /// <summary>Net cash today (in − out), same as the Day Book.</summary>
    public decimal CashToday => CashInToday - CashOutToday;
    /// <summary>Customer payments by bank / cheque / other today.</summary>
    public decimal NonCashInToday { get; set; }
    /// <summary>Σ positive customer balances (what customers owe us).</summary>
    public decimal Receivable { get; set; }
    public int ReceivableCount { get; set; }
    /// <summary>Σ customer advances (negative balances, shown positive).</summary>
    public decimal CustomerAdvances { get; set; }
    /// <summary>Σ positive supplier balances (what we owe suppliers).</summary>
    public decimal Payable { get; set; }
    public int PayableCount { get; set; }
    /// <summary>Advances paid to suppliers (negative balances, shown positive).</summary>
    public decimal SupplierAdvances { get; set; }
    /// <summary>Paid on today's bills + supplier payments dated today (any method).</summary>
    public decimal PaidToSuppliersToday { get; set; }
    /// <summary>Active products with a reorder level set and on-hand at or below it.</summary>
    public int LowStockCount { get; set; }
    /// <summary>Active products with nothing (or less) on hand.</summary>
    public int OutOfStockCount { get; set; }
    /// <summary>Active products' on-hand × purchase price (same as the stock report total).</summary>
    public decimal StockValue { get; set; }
}

public class CustomerBalance
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public CustomerType Type { get; set; }
    public bool IsActive { get; set; }
    public decimal OpeningBalance { get; set; }
    /// <summary>They owe us (negative = advance).</summary>
    public decimal Balance { get; set; }
    public DateTime? LastSaleDate { get; set; }
    /// <summary>Latest payment received or invoice with money paid at the counter.</summary>
    public DateTime? LastPaymentDate { get; set; }
    public int? DaysSinceLastPayment => LastPaymentDate is DateTime d ? (int)(DateTime.Today - d.Date).TotalDays : null;
}

public class SupplierBalance
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; }
    public decimal OpeningBalance { get; set; }
    /// <summary>We owe them (negative = advance paid).</summary>
    public decimal Balance { get; set; }
    public DateTime? LastPurchaseDate { get; set; }
    /// <summary>Latest supplier payment or bill with money paid on it.</summary>
    public DateTime? LastPaymentDate { get; set; }
    public int? DaysSinceLastPayment => LastPaymentDate is DateTime d ? (int)(DateTime.Today - d.Date).TotalDays : null;
}

public class LedgerRow
{
    public DateTime Date { get; set; }
    public int Seq { get; set; }
    /// <summary>Customer: Invoice, Paid at counter, Payment, Return, Refund. Supplier: Purchase, Paid on bill, Payment, Return, Cash received back.</summary>
    public string Kind { get; set; } = "";
    public int RefId { get; set; }
    public string Description { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance { get; set; }
    /// <summary>When the entry was keyed in; orders entries within the same day.</summary>
    public DateTime CreatedAt { get; set; }
}

public record CustomerLedger(decimal Opening, List<LedgerRow> Rows, decimal Closing);

public class DayBookRow
{
    public DateTime Date { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Sale, CustomerPayment, SaleReturn, Purchase, SupplierPayment, PurchaseReturn.</summary>
    public string Kind { get; set; } = "";
    public string Type { get; set; } = "";
    public int RefId { get; set; }
    public int PartyId { get; set; }
    public string Party { get; set; } = "";
    public string Method { get; set; } = "";
    public string? Notes { get; set; }
    public decimal CashIn { get; set; }
    public decimal CashOut { get; set; }
    public decimal NonCashIn { get; set; }
    public decimal NonCashOut { get; set; }

    public bool IsCustomer => Kind is "Sale" or "CustomerPayment" or "SaleReturn";

    public string RefText => Kind switch
    {
        "Sale" => $"Invoice #{RefId}",
        "CustomerPayment" => $"Receipt #{RefId}",
        "SaleReturn" => $"Sales return #{RefId}",
        "Purchase" => $"Purchase #{RefId}",
        "SupplierPayment" => $"Payment #{RefId}",
        "PurchaseReturn" => $"Purchase return #{RefId}",
        "Expense" => $"Expense #{RefId}",
        _ => $"#{RefId}",
    };

    /// <summary>Page that shows the document (payments open the party's ledger).</summary>
    public string Link => Kind switch
    {
        "Sale" => $"sales/{RefId}",
        "CustomerPayment" => $"ledger/{PartyId}",
        "SaleReturn" => $"sale-returns/{RefId}",
        "Purchase" => $"purchases/{RefId}/edit",
        "SupplierPayment" => $"supplier-ledger/{PartyId}",
        "PurchaseReturn" => $"purchase-returns/{RefId}",
        "Expense" => "expenses",
        _ => "",
    };

    public string PartyLink => Kind == "Expense" ? "" : IsCustomer ? $"ledger/{PartyId}" : $"supplier-ledger/{PartyId}";
}

public class DayBookTotals
{
    public int Invoices { get; set; }
    /// <summary>Σ invoice totals in the period.</summary>
    public decimal Sales { get; set; }
    /// <summary>Part of those invoices not paid at the counter.</summary>
    public decimal CreditSales { get; set; }
    public decimal SaleReturns { get; set; }
    public decimal Purchases { get; set; }
    public decimal PurchaseReturns { get; set; }
    /// <summary>Shop expenses in the period (cash and non-cash).</summary>
    public decimal Expenses { get; set; }
}

public record DayBookReport(List<DayBookRow> Rows, DayBookTotals Totals)
{
    public decimal CashIn => Rows.Sum(r => r.CashIn);
    public decimal CashOut => Rows.Sum(r => r.CashOut);
    public decimal NetCash => CashIn - CashOut;
    public decimal NonCashIn => Rows.Sum(r => r.NonCashIn);
    public decimal NonCashOut => Rows.Sum(r => r.NonCashOut);
}

public class SalesProfitRow
{
    /// <summary>The day, or the first day of the month.</summary>
    public DateTime Period { get; set; }
    public int Invoices { get; set; }
    /// <summary>Σ invoice SubTotal (before discount).</summary>
    public decimal GrossSales { get; set; }
    public decimal Discount { get; set; }
    /// <summary>Σ invoice Total.</summary>
    public decimal SalesTotal { get; set; }
    /// <summary>Σ sales return Total.</summary>
    public decimal Returns { get; set; }
    public decimal SaleCost { get; set; }
    public decimal ReturnCost { get; set; }

    /// <summary>Shop expenses dated in the period (any payment method).</summary>
    public decimal Expenses { get; set; }

    public decimal NetSales => SalesTotal - Returns;
    public decimal Cost => SaleCost - ReturnCost;
    /// <summary>Gross profit = net sales − cost of goods.</summary>
    public decimal Profit => NetSales - Cost;
    public decimal? Margin => NetSales == 0 ? null : Math.Round(Profit / NetSales * 100, 1);
    /// <summary>Net profit = gross profit − expenses (owner 2026-10-24).</summary>
    public decimal NetProfit => Profit - Expenses;
}

public enum ItemSalesGroup
{
    Product = 0,
    Company = 1,
    Category = 2,
}

public class ItemSalesRow
{
    /// <summary>Product / company / category id.</summary>
    public int Key { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Only when grouped by product.</summary>
    public string? Unit { get; set; }
    public decimal QtySold { get; set; }
    public decimal QtyReturned { get; set; }
    public decimal NetSales { get; set; }
    public decimal Cost { get; set; }

    public decimal NetQty => QtySold - QtyReturned;
    public decimal Profit => NetSales - Cost;
    public decimal? Margin => NetSales == 0 ? null : Math.Round(Profit / NetSales * 100, 1);
}

public class StockCardProduct
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string? Barcode { get; set; }
    public string? ArticleNo { get; set; }
    public string Unit { get; set; } = "";
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal ReorderLevel { get; set; }
    public bool IsActive { get; set; }
    public decimal OnHand { get; set; }
}

public class StockCardRow
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public DateTime CreatedAt { get; set; }
    public StockMovementType Type { get; set; }
    public int? RefId { get; set; }
    public string? Notes { get; set; }
    /// <summary>Supplier / customer of the document, if any.</summary>
    public string? Party { get; set; }
    public decimal QtyIn { get; set; }
    public decimal QtyOut { get; set; }
    public decimal Balance { get; set; }

    public string Description
    {
        get
        {
            var text = Type switch
            {
                StockMovementType.Opening => "Opening stock",
                StockMovementType.Purchase => $"Purchase #{RefId}{PartySuffix}",
                StockMovementType.Sale => $"Sale #{RefId}{PartySuffix}",
                StockMovementType.SaleReturn => $"Sale return #{RefId}{PartySuffix}",
                StockMovementType.PurchaseReturn => $"Purchase return #{RefId}{PartySuffix}",
                StockMovementType.AdjustIn => "Stock in",
                StockMovementType.AdjustOut => "Stock out",
                _ => Type.ToString(),
            };
            return string.IsNullOrWhiteSpace(Notes) ? text : $"{text} – {Notes}";
        }
    }

    private string PartySuffix => string.IsNullOrEmpty(Party) ? "" : $" – {Party}";

    /// <summary>Page that shows the source document; null for manual movements.</summary>
    public string? Link => RefId is not int id ? null : Type switch
    {
        StockMovementType.Purchase => $"purchases/{id}/edit",
        StockMovementType.Sale => $"sales/{id}",
        StockMovementType.SaleReturn => $"sale-returns/{id}",
        StockMovementType.PurchaseReturn => $"purchase-returns/{id}",
        _ => null,
    };
}

public record StockCard(StockCardProduct? Product, decimal Opening, List<StockCardRow> Rows, decimal Closing);

public enum StockLevelFilter
{
    All = 0,
    /// <summary>Reorder level set and on-hand at or below it.</summary>
    Low = 1,
    /// <summary>Nothing (or less) on hand: sold pieces.</summary>
    Out = 2,
    /// <summary>Something on hand.</summary>
    InStock = 3,
}

public class StockReportFilter
{
    public string? Search { get; set; }
    public int? CompanyId { get; set; }
    public int? CategoryId { get; set; }
    public StockLevelFilter Stock { get; set; } = StockLevelFilter.All;

    /// <summary>Shortcut kept for older callers: true = <see cref="StockLevelFilter.Low"/>.</summary>
    public bool LowOnly
    {
        get => Stock == StockLevelFilter.Low;
        set => Stock = value ? StockLevelFilter.Low : StockLevelFilter.All;
    }
}

public class StockReportRow
{
    /// <summary>Variant id (colour + size).</summary>
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Company { get; set; } = "";
    public string Category { get; set; } = "";
    public string SubCategory { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ArticleNo { get; set; }
    public string? Colour { get; set; }
    public string? Size { get; set; }
    public string? Barcode { get; set; }
    public string? PackSize { get; set; }
    public string Unit { get; set; } = "";
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal ReorderLevel { get; set; }
    public decimal OnHand { get; set; }
    public decimal StockValue { get; set; }
    public StockMode StockMode { get; set; }
    /// <summary>Quantity item (rice per kg): one row with its qty on hand, instead of one row per piece.</summary>
    public bool IsQuantity => StockMode == StockMode.Quantity;
    /// <summary>Matching articles (the report pages by article).</summary>
    public int TotalCount { get; set; }
    public decimal TotalValue { get; set; }
    public decimal TotalPieces { get; set; }

    /// <summary>The whole article's pieces in stock / sold and stock value (all its pieces, whatever the filter).</summary>
    public decimal ArticleInStock { get; set; }
    public int ArticleSold { get; set; }
    public decimal ArticleValue { get; set; }
    /// <summary>Nothing (or less) on hand.</summary>
    public bool IsOut => OnHand <= 0;
    /// <summary>Same as <see cref="IsOut"/>.</summary>
    public bool OutOfStock => IsOut;
    /// <summary>Reorder level set and on-hand at or below it (an out-of-stock item can also be low).</summary>
    public bool IsLow => ReorderLevel > 0 && OnHand <= ReorderLevel;
}

/// <param name="TotalCount">Matching articles.</param>
/// <param name="TotalPieces">Pieces on hand over all matching stock items.</param>
public record StockReportPage(List<StockReportRow> Rows, int TotalCount, decimal TotalValue, decimal TotalPieces);

/// <summary>A product master in the purchase item picker.</summary>
public class ProductPick
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal OnHand { get; set; }
    /// <summary>Prices of its latest stock item (0 when it has none yet).</summary>
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal ReorderLevel { get; set; }
    public StockMode StockMode { get; set; }
    public string? PurchaseUnit { get; set; }
    public decimal? PurchaseUnitQty { get; set; }
    /// <summary>Quantity products: their stock item (the one barcode bought / sold again and again); null if none yet.</summary>
    public int? FirstStockItemId { get; set; }
    public bool IsQuantity => StockMode == StockMode.Quantity;
    public bool HasPurchaseUnit => IsQuantity && !string.IsNullOrWhiteSpace(PurchaseUnit) && PurchaseUnitQty > 0;
}

public class ProductLookup
{
    /// <summary>Variant id (colour + size): what sale / purchase lines and stock movements point at.</summary>
    public int Id { get; set; }
    /// <summary>The product (article) the variant belongs to.</summary>
    public int ProductId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Company { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Colour { get; set; }
    public string? Size { get; set; }
    public string? Barcode { get; set; }
    public string? ArticleNo { get; set; }
    public string Unit { get; set; } = "";
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal? MaxDiscountPercent { get; set; }
    public decimal OnHand { get; set; }
    public StockMode StockMode { get; set; }
    public string? PurchaseUnit { get; set; }
    public decimal? PurchaseUnitQty { get; set; }
    /// <summary>Quantity item (rice per kg): any qty, bought / sold again and again; else one piece per barcode.</summary>
    public bool IsQuantity => StockMode == StockMode.Quantity;

    public decimal? MinRate => DiscountRules.MinRate(SalePrice, MaxDiscountPercent);

    /// <summary>
    /// Products for a scanned / typed code: barcode matches first (barcodes are unique), else article no. matches
    /// (several products may share one). Case-insensitive.
    /// </summary>
    public static List<ProductLookup> MatchCode(IEnumerable<ProductLookup> products, string code)
    {
        code = code.Trim();
        var list = products as IReadOnlyCollection<ProductLookup> ?? products.ToList();
        var byBarcode = list.Where(p => string.Equals(p.Barcode, code, StringComparison.OrdinalIgnoreCase)).ToList();
        return byBarcode.Count > 0
            ? byBarcode
            : list.Where(p => string.Equals(p.ArticleNo, code, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
