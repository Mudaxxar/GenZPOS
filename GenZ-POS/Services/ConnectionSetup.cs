using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace GenZPOS.Services;

/// <summary>
/// Whether the database connection screen (Components/Auth/DatabaseSetup.razor) is shown instead of the app: always when
/// GenZ-POS couldn't reach its database at startup (<see cref="SetupMode"/>), or when an admin opens it from Settings.
/// Singleton; Routes.razor re-renders on <see cref="Changed"/>.
/// </summary>
public sealed class StartupState
{
    /// <summary>Started without a database (it couldn't be reached): only the connection screen can be shown.</summary>
    public bool SetupMode { get; set; }

    /// <summary>Why the database couldn't be reached (shown on the screen).</summary>
    public string? Problem { get; set; }

    public bool ShowSetup => SetupMode || setupOpen;
    private bool setupOpen;

    public event Action? Changed;

    public void OpenSetup() { setupOpen = true; Changed?.Invoke(); }
    public void CloseSetup() { setupOpen = false; Changed?.Invoke(); }
}

/// <summary>
/// Choosing the database from inside GenZ-POS (owner 2026-10-26: "select the database connection like the first-time
/// login page" instead of editing appsettings.json in Notepad): find SQL Servers on this PC, try a server with Windows
/// or SQL login, list its databases (GenZ-POS ones with their data), build and save the connection string.
/// </summary>
public static class ConnectionSetup
{
    /// <summary>
    /// SQL Servers that answer on this PC: the instances installed here (registry) plus LocalDB and the usual names,
    /// and <paramref name="alsoTry"/> (e.g. the server in appsettings.json). Each is tried for a few seconds, in parallel.
    /// </summary>
    public static async Task<List<string>> FindServersAsync(string? alsoTry = null)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(alsoTry)) names.Add(alsoTry.Trim());
        names.AddRange(InstalledInstances());
        names.AddRange([".", @".\SQLEXPRESS", @"(localdb)\MSSQLLocalDB"]);
        var candidates = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var results = await Task.WhenAll(candidates.Select(async server =>
        {
            try
            {
                await using var conn = new SqlConnection(Build(server, "master", windowsLogin: true, null, null, timeoutSeconds: 4));
                await conn.OpenAsync();
                return server;
            }
            catch
            {
                return null;
            }
        }));
        return results.Where(s => s is not null).Select(s => s!).ToList();
    }

    /// <summary>SQL Server instances installed on this PC (".", ".\SQLEXPRESS" …), from the registry.</summary>
    private static IEnumerable<string> InstalledInstances()
    {
        var found = new List<string>();
        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
                if (key is null) continue;
                found.AddRange(key.GetValueNames().Select(n => n.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "." : $@".\{n}"));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not read the SQL Server instances from the registry", ex);
        }
        return found;
    }

    /// <summary>
    /// Connects to <paramref name="server"/> and returns its databases: the GenZ-POS ones (with products / invoices) and the
    /// names of all others. Throws a friendly <see cref="BusinessException"/> when the server or the login fails.
    /// </summary>
    public static async Task<(List<ShopDatabase> Shop, List<string> Others)> ListDatabasesAsync(
        string server, bool windowsLogin, string? user, string? password)
    {
        var master = Build(server, "master", windowsLogin, user, password, timeoutSeconds: 8);
        try
        {
            var shop = await DatabaseGuard.FindShopDatabasesAsync(master);
            var all = new List<string>();
            await using (var conn = new SqlConnection(master))
            {
                await conn.OpenAsync();
                await using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name", conn);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) all.Add(reader.GetString(0));
            }
            return (shop, all.Where(n => !shop.Any(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList());
        }
        catch (SqlException ex)
        {
            throw new BusinessException(ex.Number switch
            {
                18456 => windowsLogin
                    ? $"SQL Server \"{server}\" doesn't let this Windows user in. Use a SQL login, or give this Windows user access on that server."
                    : "The SQL login or password is wrong.",
                -1 or 2 or 53 or 26 or -2 or 40 =>
                    $"SQL Server \"{server}\" can't be reached. Check the name (e.g. MAINPC\\SQLEXPRESS), that the computer is on, " +
                    "and (for another computer) that SQL Server allows network connections (TCP/IP, firewall).",
                _ => $"Could not connect to \"{server}\": {ex.Message}",
            });
        }
    }

    /// <summary>A connection string the way GenZ-POS needs it.</summary>
    public static string Build(string server, string database, bool windowsLogin, string? user, string? password, int timeoutSeconds = 15)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server.Trim(),
            InitialCatalog = database.Trim(),
            TrustServerCertificate = true,
            MultipleActiveResultSets = true,
            ConnectTimeout = timeoutSeconds,
        };
        if (windowsLogin)
            b.IntegratedSecurity = true;
        else
        {
            b.UserID = user?.Trim() ?? "";
            b.Password = password ?? "";
        }
        return b.ConnectionString;
    }
}
