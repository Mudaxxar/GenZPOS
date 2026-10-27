using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;
using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>
/// Startup safety check (owner 2026-10-26): GenZ-POS databases on the server are found with their data, so a wrong
/// appsettings.json can't make the app quietly start a new, empty database.
/// </summary>
public class DatabaseGuardTests
{
    private static readonly string Server =
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? @"Server=np:\\.\pipe\sql\query;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

    private static string Db(string name) => new SqlConnectionStringBuilder(Server) { InitialCatalog = name }.ConnectionString;

    [Fact]
    public async Task Finds_shop_databases_with_and_without_data()
    {
        // One GenZ-POS database with a product, one migrated but empty.
        foreach (var (name, withData) in new[] { ("GenZPOS_GuardData", true), ("GenZPOS_GuardEmpty", false) })
        {
            var b = new DbContextOptionsBuilder<AppDbContext>();
            AppDbContext.Configure(b, Db(name));
            await using var db = new AppDbContext(b.Options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            if (withData)
            {
                db.Products.Add(new Product
                {
                    Company = new Company { Name = "Zee" }, SubCategoryId = 1, Name = "Shoes",
                    StockItems = [new() { ArticleNo = "A1", Barcode = "B1" }],
                });
                await db.SaveChangesAsync();
            }
        }

        var found = await DatabaseGuard.FindShopDatabasesAsync(Db("GenZPOS_GuardEmpty"));

        var data = Assert.Single(found, d => d.Name == "GenZPOS_GuardData");
        Assert.True(data.HasData);
        Assert.Equal(1, data.Products);
        var empty = Assert.Single(found, d => d.Name == "GenZPOS_GuardEmpty");
        Assert.False(empty.HasData);
        Assert.DoesNotContain(found, d => d.Name is "master" or "tempdb" or "model" or "msdb");

        // A wrong server in appsettings.json: SQL Server on this PC is found under one of the usual names, with data.
        if (new SqlConnectionStringBuilder(Server).DataSource.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
        {
            var wrong = new SqlConnectionStringBuilder(Db("GenZPOS_GuardData")) { DataSource = "NoSuchServer_GenZ" }.ConnectionString;
            var local = await DatabaseGuard.FindLocalServerAsync(wrong);
            Assert.NotNull(local);
            Assert.NotNull(local!.Value.Db);
            Assert.True(local.Value.Db!.HasData);
            Assert.Equal("NoSuchServer_GenZ", new SqlConnectionStringBuilder(wrong).DataSource);
            Assert.Equal(local.Value.Server, new SqlConnectionStringBuilder(DatabaseGuard.WithServer(wrong, local.Value.Server)).DataSource);
        }

        // Database connection screen (ConnectionSetup): lists this server's GenZ-POS databases and the others.
        var serverName = new SqlConnectionStringBuilder(Server).DataSource;
        var (shopDbs, others) = await ConnectionSetup.ListDatabasesAsync(serverName, windowsLogin: true, null, null);
        Assert.Contains(shopDbs, d => d.Name == "GenZPOS_GuardData" && d.HasData);
        Assert.DoesNotContain(others, n => n == "GenZPOS_GuardData");
        if (serverName.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
            Assert.Contains(await ConnectionSetup.FindServersAsync(), s => s.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ConnectionSetup.ListDatabasesAsync("NoSuchServer_GenZ", true, null, null));
        Assert.Contains("can't be reached", ex.Message);

        var windows = new SqlConnectionStringBuilder(ConnectionSetup.Build(@".\SQLEXPRESS", "Shop", windowsLogin: true, null, null));
        Assert.True(windows.IntegratedSecurity);
        Assert.Equal((@".\SQLEXPRESS", "Shop", true, true), (windows.DataSource, windows.InitialCatalog, windows.TrustServerCertificate, windows.MultipleActiveResultSets));
        var sqlLogin = new SqlConnectionStringBuilder(ConnectionSetup.Build("MAINPC", "Shop", windowsLogin: false, "pos", "secret"));
        Assert.False(sqlLogin.IntegratedSecurity);
        Assert.Equal(("pos", "secret"), (sqlLogin.UserID, sqlLogin.Password));

        // Switching keeps the server and everything else, only the database changes.
        var switched = new SqlConnectionStringBuilder(DatabaseGuard.WithDatabase(Db("Wrong"), "GenZPOS_GuardData"));
        Assert.Equal("GenZPOS_GuardData", switched.InitialCatalog);
        Assert.Equal(new SqlConnectionStringBuilder(Server).DataSource, switched.DataSource);
    }
}
