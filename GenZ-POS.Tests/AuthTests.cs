using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>
/// Login, lockout, password change and rights enforcement against a throw-away database (GenZPOS_AuthTest,
/// separate from the scenario test's database so both can run in parallel).
/// </summary>
public class AuthTests
{
    private static readonly string ConnectionString = new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")
    {
        InitialCatalog = "GenZPOS_AuthTest",
    }.ConnectionString;

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Login_lockout_password_change_and_rights_work()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, ConnectionString);
        var factory = new Factory(builder.Options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }

        var me = new CurrentUser();
        var auth = new AuthService(factory, me);
        var admin = new UserService(factory, me, auth);

        // ---------- first run: admin / admin, must change password ----------
        Assert.True(await auth.EnsureAdminAsync());
        Assert.False(await auth.EnsureAdminAsync());
        Assert.True(await auth.IsFirstRunAsync());

        await Assert.ThrowsAsync<BusinessException>(() => auth.LoginAsync("admin", "wrong"));
        Assert.False(me.IsSignedIn);
        await Assert.ThrowsAsync<BusinessException>(() => auth.LoginAsync("nobody", "admin"));

        await auth.LoginAsync("admin", "admin");
        Assert.True(me.Session!.IsAdmin);
        Assert.True(me.Session.MustChangePassword);
        Assert.False(await auth.IsFirstRunAsync());

        await Assert.ThrowsAsync<BusinessException>(() => auth.ChangePasswordAsync("admin", "short", "short"));
        await Assert.ThrowsAsync<BusinessException>(() => auth.ChangePasswordAsync("bad", "owner123", "owner123"));
        await auth.ChangePasswordAsync("admin", "owner123", "owner123");
        Assert.False(me.Session!.MustChangePassword);

        // ---------- the owner adds a cashier ----------
        var cashierId = await admin.SaveUserAsync(
            new UserRow { UserName = "sana", FullName = "Sana", RoleId = AppRole.CashierId, IsActive = true }, "cash123", mustChangePassword: false);
        await Assert.ThrowsAsync<BusinessException>(() => admin.SaveUserAsync(
            new UserRow { UserName = "SANA", RoleId = AppRole.CashierId, IsActive = true }, "x123456", false)); // name taken

        // Admin can't lose the last admin.
        var adminRow = (await admin.GetUsersAsync()).Single(u => u.UserName == "admin");
        adminRow.RoleId = AppRole.CashierId;
        await Assert.ThrowsAsync<BusinessException>(() => admin.SaveUserAsync(adminRow, null, false));
        await Assert.ThrowsAsync<BusinessException>(() => admin.DeleteUserAsync(adminRow.Id)); // own user
        await Assert.ThrowsAsync<BusinessException>(() => admin.DeleteRoleAsync(AppRole.AdminId));
        await Assert.ThrowsAsync<BusinessException>(() => admin.DeleteRoleAsync(AppRole.CashierId)); // has a user

        // ---------- cashier: rights from the seeded Cashier role ----------
        auth.Logout();
        Assert.False(me.IsSignedIn);
        await auth.LoginAsync("sana", "cash123");
        Assert.True(me.Has(Permissions.SalesCreate));
        Assert.False(me.Has(Permissions.SalesDelete));
        Assert.False(me.Has(Permissions.UsersManage));

        var sales = new SaleService(factory, me);
        var stock = new StockService(factory, me);
        var noRight = await Assert.ThrowsAsync<BusinessException>(() => sales.DeleteAsync(1));
        Assert.Contains("Delete sale invoices", noRight.Message);
        await Assert.ThrowsAsync<BusinessException>(() => stock.AdjustAsync(1, DateTime.Today, StockMovementType.AdjustIn, 1, null));
        await Assert.ThrowsAsync<BusinessException>(() => admin.GetUsersAsync());

        // ---------- lockout: 5 wrong passwords lock, even the right one is refused ----------
        auth.Logout();
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<BusinessException>(() => auth.LoginAsync("sana", "nope"));
        var locked = await Assert.ThrowsAsync<BusinessException>(() => auth.LoginAsync("sana", "cash123"));
        Assert.Contains("Too many", locked.Message);

        // Owner unlocks and switches the cashier off.
        await auth.LoginAsync("admin", "owner123");
        Assert.True((await admin.GetUsersAsync()).Single(u => u.Id == cashierId).IsLocked);
        await admin.UnlockAsync(cashierId);
        var cashierRow = (await admin.GetUsersAsync()).Single(u => u.Id == cashierId);
        Assert.False(cashierRow.IsLocked);
        cashierRow.IsActive = false;
        await admin.SaveUserAsync(cashierRow, null, false);
        auth.Logout();
        var off = await Assert.ThrowsAsync<BusinessException>(() => auth.LoginAsync("sana", "cash123"));
        Assert.Contains("switched off", off.Message);

        // ---------- a role edit applies to the next check ----------
        await auth.LoginAsync("admin", "owner123");
        var cashierRole = (await admin.GetRolesAsync()).Single(r => r.Id == AppRole.CashierId);
        cashierRole.Permissions.Add(Permissions.SalesDelete);
        cashierRole.Permissions.Remove(Permissions.StockView);
        await admin.SaveRoleAsync(cashierRole);
        var saved = (await admin.GetRolesAsync()).Single(r => r.Id == AppRole.CashierId);
        Assert.Contains(Permissions.SalesDelete, saved.Permissions);
        Assert.DoesNotContain(Permissions.StockView, saved.Permissions);
    }
}
