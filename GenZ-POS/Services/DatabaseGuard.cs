using Microsoft.Data.SqlClient;

namespace GenZPOS.Services;

/// <summary>A GenZ-POS database found on the SQL Server, with how much shop data it holds.</summary>
public sealed record ShopDatabase(string Name, int Products, int Sales, DateTime? LastSale)
{
    public bool HasData => Products + Sales > 0;
    public string Describe => $"\"{Name}\": {Products} product(s), {Sales} invoice(s)" + (LastSale is { } d ? $", last sale {d:dd-MMM-yyyy}" : "");
}

/// <summary>
/// Startup safety check (owner 2026-10-26: "after an update it didn't pick up the existing database, it created a new
/// one"). Before EF creates a database, look at what the server already has: a GenZ-POS database (one with
/// __EFMigrationsHistory, Products and Sales tables) holding shop data under another name means the settings point at
/// the wrong database — e.g. a copied appsettings.json. App.xaml.cs then asks before creating anything.
/// </summary>
public static class DatabaseGuard
{
    /// <summary>Every database on the server (that this Windows user may open) that looks like GenZ-POS's.</summary>
    public static async Task<List<ShopDatabase>> FindShopDatabasesAsync(string connectionString)
    {
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", ConnectTimeout = 15, Pooling = false };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();

        var names = new List<string>();
        await using (var cmd = new SqlCommand(
            "SELECT name FROM sys.databases WHERE database_id > 4 AND state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1 ORDER BY name", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));

        var found = new List<ShopDatabase>();
        foreach (var name in names)
        {
            var db = "[" + name.Replace("]", "]]") + "]";
            try
            {
                await using var check = new SqlCommand(
                    $"SELECT CASE WHEN OBJECT_ID(N'{db.Replace("'", "''")}.dbo.__EFMigrationsHistory') IS NOT NULL " +
                    $"AND OBJECT_ID(N'{db.Replace("'", "''")}.dbo.Products') IS NOT NULL " +
                    $"AND OBJECT_ID(N'{db.Replace("'", "''")}.dbo.Sales') IS NOT NULL THEN 1 ELSE 0 END", conn);
                if ((int)(await check.ExecuteScalarAsync() ?? 0) != 1) continue;

                await using var counts = new SqlCommand(
                    $"SELECT (SELECT COUNT(*) FROM {db}.dbo.Products), (SELECT COUNT(*) FROM {db}.dbo.Sales), (SELECT MAX(SaleDate) FROM {db}.dbo.Sales)", conn);
                await using var r = await counts.ExecuteReaderAsync();
                if (await r.ReadAsync())
                    found.Add(new ShopDatabase(name, r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetDateTime(2)));
            }
            catch (SqlException ex)
            {
                AppLog.Warn($"Could not look into database {name}", ex);
            }
        }
        return found;
    }

    /// <summary>
    /// The configured SQL Server can't be reached (owner 2026-10-26: a fresh appsettings.json says "Server=." but this PC's
    /// SQL Server is ".\SQLEXPRESS"): try the usual names on this PC. Returns the first one with GenZ-POS data (the
    /// configured database name preferred), else the first one that answers (Db = null), else null.
    /// </summary>
    public static async Task<(string Server, ShopDatabase? Db)?> FindLocalServerAsync(string connectionString)
    {
        var configured = new SqlConnectionStringBuilder(connectionString);
        var machine = Environment.MachineName;
        string[] names = [".", @".\SQLEXPRESS", @"(localdb)\MSSQLLocalDB", machine, $@"{machine}\SQLEXPRESS"];
        (string Server, ShopDatabase? Db)? firstReachable = null;
        foreach (var server in names.Where(n => !string.Equals(n, configured.DataSource, StringComparison.OrdinalIgnoreCase)))
        {
            var candidate = new SqlConnectionStringBuilder(connectionString) { DataSource = server, ConnectTimeout = 4 }.ConnectionString;
            List<ShopDatabase> found;
            try
            {
                found = await FindShopDatabasesAsync(candidate);
            }
            catch (SqlException)
            {
                continue; // not on this PC
            }
            var best = found.Where(d => d.HasData)
                .OrderByDescending(d => string.Equals(d.Name, configured.InitialCatalog, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(d => d.LastSale ?? DateTime.MinValue).FirstOrDefault();
            if (best is not null) return (server, best);
            firstReachable ??= (server, null);
        }
        return firstReachable;
    }

    public static string WithServer(string connectionString, string server, string? database = null)
    {
        var b = new SqlConnectionStringBuilder(connectionString) { DataSource = server };
        if (database is not null) b.InitialCatalog = database;
        return b.ConnectionString;
    }

    /// <summary>The connection string with another database name.</summary>
    public static string WithDatabase(string connectionString, string database) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;
}
