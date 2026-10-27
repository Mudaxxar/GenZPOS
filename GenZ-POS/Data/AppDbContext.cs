using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const string DefaultConnectionString =
        "Server=.;Database=Blazor;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

    /// <summary>Shared options setup for the app and for design-time (migrations).</summary>
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        // SQL Server 2016 = compatibility level 130; stops EF emitting newer T-SQL.
        options.UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(130));

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<SubCategory> SubCategories => Set<SubCategory>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<PurchaseLine> PurchaseLines => Set<PurchaseLine>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<CustomerPayment> CustomerPayments => Set<CustomerPayment>();
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();
    public DbSet<SaleReturnLine> SaleReturnLines => Set<SaleReturnLine>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<PurchaseReturnLine> PurchaseReturnLines => Set<PurchaseReturnLine>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AppRole> Roles => Set<AppRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<CashClosing> CashClosings => Set<CashClosing>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<HeldSale> HeldSales => Set<HeldSale>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Masters can't be deleted while documents reference them (pages show a friendly message).
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;

        b.Entity<Company>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Category>().HasIndex(x => x.Name).IsUnique();
        b.Entity<SubCategory>().HasIndex(x => new { x.CategoryId, x.Name }).IsUnique();
        // Product = master; its stock items = article no. + colour + size, each with a unique barcode.
        b.Entity<Product>().HasIndex(x => x.Name);
        b.Entity<StockItem>().HasIndex(x => x.Barcode).IsUnique();
        b.Entity<StockItem>().HasIndex(x => x.ArticleNo);
        b.Entity<StockItem>().HasIndex(x => new { x.ProductId, x.ArticleNo, x.Colour, x.Size });
        // Auto article no. / barcode numbers (ProductCodes).
        b.HasSequence<long>(ProductCodes.ArticleNoSequence).StartsAt(1);
        b.HasSequence<long>(ProductCodes.BarcodeSequence).StartsAt(1);

        b.Entity<Purchase>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Purchase>().HasIndex(x => x.PurchaseDate);

        b.Entity<Sale>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Sale>().HasIndex(x => new { x.CustomerId, x.SaleDate });
        b.Entity<Sale>().HasIndex(x => x.SaleDate);

        b.Entity<CustomerPayment>().HasIndex(x => new { x.CustomerId, x.PaymentDate });
        b.Entity<SupplierPayment>().HasIndex(x => new { x.SupplierId, x.PaymentDate });
        b.Entity<Purchase>().HasIndex(x => x.SupplierId);

        b.Entity<StockMovement>().HasIndex(x => x.StockItemId);
        b.Entity<StockMovement>().HasIndex(x => new { x.Type, x.RefId });

        b.Entity<SaleReturn>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SaleReturnId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SaleReturn>().HasIndex(x => new { x.CustomerId, x.ReturnDate });
        b.Entity<SaleReturn>().HasIndex(x => x.SaleId);
        // Two links to Sales: the invoice the items came from, and (exchange) the new invoice saved with the return.
        b.Entity<SaleReturn>().HasOne(x => x.Sale).WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SaleReturn>().HasOne(x => x.ExchangeSale).WithMany(x => x.ExchangeReturns).HasForeignKey(x => x.ExchangeSaleId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<SaleReturn>().HasIndex(x => x.ReturnDate);

        b.Entity<PurchaseReturn>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseReturnId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PurchaseReturn>().HasIndex(x => new { x.SupplierId, x.ReturnDate });
        b.Entity<PurchaseReturn>().HasIndex(x => x.PurchaseId);
        b.Entity<PurchaseReturn>().HasIndex(x => x.ReturnDate);

        // Quantities have 3 decimals (owner 2026-10-24: 0.125 kg, 50 g = 0.05 kg); money stays (18,2).
        b.Entity<StockMovement>().Property(x => x.QtyIn).HasPrecision(18, 3);
        b.Entity<StockMovement>().Property(x => x.QtyOut).HasPrecision(18, 3);
        b.Entity<SaleLine>().Property(x => x.Qty).HasPrecision(18, 3);
        b.Entity<PurchaseLine>().Property(x => x.Qty).HasPrecision(18, 3);
        b.Entity<SaleReturnLine>().Property(x => x.Qty).HasPrecision(18, 3);
        b.Entity<PurchaseReturnLine>().Property(x => x.Qty).HasPrecision(18, 3);
        b.Entity<StockItem>().Property(x => x.ReorderLevel).HasPrecision(18, 3);
        b.Entity<Product>().Property(x => x.PurchaseUnitQty).HasPrecision(18, 3);

        // Expenses, day closing, audit log, held carts (owner 2026-10-24).
        b.Entity<ExpenseCategory>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Expense>().HasIndex(x => x.ExpenseDate);
        b.Entity<CashClosing>().HasIndex(x => x.ClosingDate).IsUnique();
        b.Entity<AuditEntry>().HasIndex(x => x.At);
        b.Entity<AuditEntry>().HasIndex(x => x.Action);

        b.Entity<AppUser>().HasIndex(x => x.UserName).IsUnique();
        b.Entity<AppRole>().HasIndex(x => x.Name).IsUnique();
        b.Entity<AppRole>().HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RolePermission>().HasKey(x => new { x.RoleId, x.Permission });

        Seed(b);
    }

    private static void Seed(ModelBuilder b)
    {
        b.Entity<Customer>().HasData(new Customer
        {
            Id = Customer.WalkInId, Name = "Walk-in Customer", Type = CustomerType.WalkIn, IsActive = true,
        });

        string[][] tree =
        [
            ["Emulsion", "Interior Emulsion", "Exterior Emulsion"],
            ["Enamel", "Gloss Enamel", "Matt Enamel"],
            ["Distemper", "Distemper"],
            ["Primer", "Wall Primer", "Wood Primer", "Metal Primer"],
            ["Putty & Fillers", "Wall Putty", "Wood Filler"],
            ["Wood Finishes", "Lacquer", "Varnish", "Polyurethane"],
            ["Thinners & Solvents", "Thinner", "Turpentine"],
            ["Tools & Accessories", "Brushes", "Rollers", "Sandpaper", "Masking Tape"],
        ];

        var subId = 1;
        for (var i = 0; i < tree.Length; i++)
        {
            var catId = i + 1;
            b.Entity<Category>().HasData(new Category { Id = catId, Name = tree[i][0] });
            foreach (var sub in tree[i].Skip(1))
                b.Entity<SubCategory>().HasData(new SubCategory { Id = subId++, CategoryId = catId, Name = sub });
        }

        string[] expenseCategories = ["Rent", "Salaries & wages", "Electricity", "Gas & water", "Tea & food", "Transport",
            "Repairs & maintenance", "Phone & internet", "Shop supplies", "Other"];
        for (var i = 0; i < expenseCategories.Length; i++)
            b.Entity<ExpenseCategory>().HasData(new ExpenseCategory { Id = i + 1, Name = expenseCategories[i] });

        // Starting roles; the owner can change Manager / Cashier rights or add roles on the Users & Roles screen.
        // The first admin *user* is created at startup (AuthService.EnsureAdminAsync): a password hash is salted,
        // so it can't be static seed data.
        b.Entity<AppRole>().HasData(
            new AppRole { Id = AppRole.AdminId, Name = "Admin", Description = "Owner: every right", IsAdmin = true },
            new AppRole { Id = AppRole.ManagerId, Name = "Manager", Description = "Day-to-day running, no users or settings" },
            new AppRole { Id = AppRole.CashierId, Name = "Cashier", Description = "Counter sales and payments" });

        string[] cashier =
        [
            Permissions.SalesCreate, Permissions.SalesView, Permissions.CustomersManage,
            Permissions.PaymentsReceive, Permissions.StockView,
        ];
        var manager = Permissions.All.Except([Permissions.SettingsManage, Permissions.UsersManage, Permissions.AuditView]);
        b.Entity<RolePermission>().HasData(
            manager.Select(p => new RolePermission { RoleId = AppRole.ManagerId, Permission = p })
                .Concat(cashier.Select(p => new RolePermission { RoleId = AppRole.CashierId, Permission = p })));
    }
}

/// <summary>Used by `dotnet ef` (the WPF app has no generic host to discover the context from).</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        // Owner 2026-10-26: dotnet ef / Package Manager Console use the project's appsettings.json too (else the default).
        AppDbContext.Configure(options, AppConfig.ConnectionStringForTools(System.IO.Directory.GetCurrentDirectory()));
        return new AppDbContext(options.Options);
    }
}
