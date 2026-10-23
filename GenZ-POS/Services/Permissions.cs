namespace GenZPOS.Services;

/// <summary>
/// Every right a role can be given. The string is what's stored in RolePermissions and used as the
/// authorization policy name (<c>[Authorize(Policy = Permissions.SalesCreate)]</c>), so never rename one —
/// add a new one instead. The Admin role has all of them implicitly.
/// </summary>
public static class Permissions
{
    public const string SalesCreate = "Sales.Create";
    public const string SalesView = "Sales.View";
    public const string SalesDelete = "Sales.Delete";
    public const string SaleReturnsManage = "SaleReturns.Manage";
    public const string CustomersManage = "Customers.Manage";
    public const string PaymentsReceive = "Payments.Receive";

    public const string PurchasesManage = "Purchases.Manage";
    public const string PurchasesDelete = "Purchases.Delete";
    public const string PurchaseReturnsManage = "PurchaseReturns.Manage";
    public const string SuppliersManage = "Suppliers.Manage";
    public const string SuppliersPay = "Suppliers.Pay";

    public const string StockView = "Stock.View";
    public const string StockAdjust = "Stock.Adjust";
    public const string CatalogManage = "Catalog.Manage";

    public const string DashboardView = "Dashboard.View";
    public const string ReportsDayBook = "Reports.DayBook";
    public const string ReportsProfit = "Reports.Profit";
    public const string ReportsItemSales = "Reports.ItemSales";
    public const string ReportsBalances = "Reports.Balances";

    public const string SettingsManage = "Settings.Manage";
    public const string UsersManage = "Users.Manage";

    public sealed record Item(string Name, string Description);
    public sealed record Group(string Title, IReadOnlyList<Item> Items);

    /// <summary>All rights, grouped the way the Roles screen shows them.</summary>
    public static IReadOnlyList<Group> Groups { get; } =
    [
        new("Sales",
        [
            new(SalesCreate, "Make new sales (till screen)"),
            new(SalesView, "View sales list and invoices"),
            new(SalesDelete, "Delete sale invoices"),
            new(SaleReturnsManage, "Make, view and delete sale returns"),
            new(CustomersManage, "Add / edit customers, customer ledger"),
            new(PaymentsReceive, "Receive customer payments"),
        ]),
        new("Purchases",
        [
            new(PurchasesManage, "Enter, edit and view purchases"),
            new(PurchasesDelete, "Delete purchases"),
            new(PurchaseReturnsManage, "Make, view and delete purchase returns"),
            new(SuppliersManage, "Add / edit suppliers, supplier ledger"),
            new(SuppliersPay, "Pay suppliers"),
        ]),
        new("Stock & catalog",
        [
            new(StockView, "Stock report and item history"),
            new(StockAdjust, "Stock in / out and stock count"),
            new(CatalogManage, "Products, companies and categories"),
        ]),
        new("Reports",
        [
            new(DashboardView, "Dashboard figures (sales, cash, profit)"),
            new(ReportsDayBook, "Day Book (cash)"),
            new(ReportsProfit, "Sales & profit"),
            new(ReportsItemSales, "Item / brand sales"),
            new(ReportsBalances, "Receivables and payables"),
        ]),
        new("Administration",
        [
            new(SettingsManage, "Shop settings, backup and restore"),
            new(UsersManage, "Users, roles and rights"),
        ]),
    ];

    public static IReadOnlyList<string> All { get; } = Groups.SelectMany(g => g.Items).Select(i => i.Name).ToList();

    public static string Describe(string permission) =>
        Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Name == permission)?.Description ?? permission;
}
