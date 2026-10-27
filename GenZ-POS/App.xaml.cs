using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using GenZPOS.Components;
using GenZPOS.Data;
using GenZPOS.Services;
using Radzen;

namespace GenZPOS;

public partial class App : Application
{
    private const string AppTitle = "GenZ-POS";
    private const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const int SqlConnectTries = 3;
    private static readonly TimeSpan SqlRetryDelay = TimeSpan.FromSeconds(5);

    // Static so the GC never collects it while the app runs (that would release the single-instance lock).
    private static Mutex? singleInstance;

    private DateTime? backupErrorShownOn;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Splash / message boxes must not end the app; switched to OnMainWindowClose once the main window is up.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        RegisterCrashHandlers();
        SetCulture();

        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        AppLog.Cleanup();
        AppLog.Info($"GenZ-POS v{AppConfig.Version} starting");

        if (!CheckWebView2Runtime())
        {
            Shutdown();
            return;
        }

        var cfg = AppConfig.Load();
        if (cfg.LoadError is not null)
            MessageBox.Show(cfg.LoadError, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);

        LogPrinterSettings(cfg);
        var provider = BuildServices(cfg);
        var (splash, status) = CreateSplash();
        splash.Show();

        // Owner 2026-10-26: when the database can't be reached, open the window on the Database connection screen
        // (Components/Auth/DatabaseSetup.razor) instead of an error box, so the right one can be picked there.
        string? setupProblem;
        try
        {
            setupProblem = await ReachSqlServerAsync(cfg, status);
            if (setupProblem is null)
            {
                if (!await CheckDatabaseChoiceAsync(cfg, status) || !await UpgradeDatabaseAsync(provider, status))
                {
                    splash.Close();
                    Shutdown();
                    return;
                }
                status.Text = "Opening GenZ-POS…";
                await provider.GetRequiredService<ShopInfo>().LoadAsync();
                await provider.GetRequiredService<BackupService>().LoadAsync();
                await provider.GetRequiredService<AuthService>().EnsureAdminAsync();
            }
        }
        catch (Exception ex) when ((ex as SqlException ?? ex.GetBaseException() as SqlException) is { Number: 18456 or 4060 or 4064 } sql)
        {
            // The server answered but refused this login / database: pick another connection on the screen.
            AppLog.Error("Startup: SQL Server refused the connection", ex);
            setupProblem = sql.Number == 18456
                ? $"SQL Server \"{cfg.ServerName}\" refused this login. Choose a login that has access, or another server."
                : $"This login can't open the database \"{cfg.DatabaseName}\" on \"{cfg.ServerName}\". Choose the database (or a login that may open it).";
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed", ex);
            splash.Close();
            MessageBox.Show(StartupErrorMessage(ex, cfg), AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (setupProblem is not null)
        {
            var state = provider.GetRequiredService<StartupState>();
            state.SetupMode = true;
            state.Problem = setupProblem;
        }

        var window = new MainWindow(provider);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        splash.Close();

        if (setupProblem is null)
            StartBackgroundWork(provider);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            singleInstance?.ReleaseMutex();
            singleInstance?.Dispose();
        }
        catch
        {
            // Exiting anyway; Windows releases the mutex with the process.
        }
        base.OnExit(e);
    }

    // ---------- startup steps ----------

    private void RegisterCrashHandlers()
    {
        // Last-resort handlers: log, and tell the user instead of silently crashing.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled UI error", args.Exception);
            MessageBox.Show(PageBase.FriendlyMessage(args.Exception), AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved background task error", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Error($"Fatal unhandled error (terminating: {args.IsTerminating})", ex);
        };
    }

    /// <summary>Fixed culture so dates/numbers look the same on every PC whatever its Windows region: dd/MM/yyyy, "Rs".</summary>
    private static void SetCulture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
        culture.NumberFormat.CurrencySymbol = "Rs";
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>Only one GenZ-POS per Windows user session. A second start brings the running window to the front.</summary>
    private static bool AcquireSingleInstance()
    {
        singleInstance = new Mutex(initiallyOwned: true, @"Local\GenZ-POS.SingleInstance", out var createdNew);
        if (createdNew)
            return true;

        singleInstance.Dispose();
        singleInstance = null;
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(current.ProcessName))
            {
                using (p)
                {
                    if (p.Id == current.Id) continue;
                    var handle = p.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    if (IsIconic(handle)) ShowWindow(handle, SwRestore);
                    SetForegroundWindow(handle);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not bring the running GenZ-POS window to the front", ex);
        }
        return false;
    }

    /// <summary>The UI runs inside Microsoft Edge WebView2. Windows 11 has it; old Windows 10 PCs may not.</summary>
    private static bool CheckWebView2Runtime()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
            if (!string.IsNullOrEmpty(version))
                return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // handled below
        }
        catch (Exception ex)
        {
            // Unexpected (e.g. loader problem): let the window try anyway; the log has the details.
            AppLog.Error("WebView2 check failed", ex);
            return true;
        }

        AppLog.Error("WebView2 Runtime is not installed");
        MessageBox.Show(
            "GenZ-POS needs the free \"Microsoft Edge WebView2 Runtime\", which is not installed on this computer.\n\n" +
            "The download page will open now. Install it, then start GenZ-POS again.",
            AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        try
        {
            Process.Start(new ProcessStartInfo(WebView2DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not open the WebView2 download link", ex);
        }
        return false;
    }

    private static ServiceProvider BuildServices(AppConfig cfg)
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        // Blazor / EF Core errors → %LocalAppData%\GenZ-POS\logs
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Error);
            b.AddProvider(new FileLoggerProvider());
        });
        services.AddRadzenComponents();

        // Login + rights: one signed-in user per window; each right is an authorization policy.
        services.AddAuthorizationCore(AppAuthStateProvider.AddPolicies);
        services.AddCascadingAuthenticationState();
        services.AddSingleton<CurrentUser>();
        services.AddSingleton<AuthenticationStateProvider, AppAuthStateProvider>();
        services.AddSingleton<AuthService>();
        services.AddScoped<UserService>();

        services.AddSingleton(cfg);
        services.AddDbContextFactory<AppDbContext>(o => AppDbContext.Configure(o, cfg.ConnectionString));

        services.AddSingleton<DesktopService>();
        services.AddScoped<StockService>();
        services.AddScoped<PurchaseService>();
        services.AddScoped<SaleService>();
        services.AddScoped<ReportService>();
        services.AddScoped<SaleReturnService>();
        services.AddScoped<PurchaseReturnService>();
        services.AddScoped<ProductExcelService>();
        services.AddScoped<StockItemService>();
        services.AddScoped<ExpenseService>();
        services.AddScoped<DayClosingService>();
        services.AddScoped<HeldSaleService>();
        services.AddSingleton<ShopInfo>();
        services.AddSingleton<StartupState>(); // Database connection screen (Components/Auth/DatabaseSetup.razor)
        services.AddSingleton<BackupService>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Waits for the configured SQL Server (it may still be starting after Windows). Null = reachable; otherwise why not —
    /// the window then opens on the Database connection screen (owner 2026-10-26), which lists the SQL Servers on this PC.
    /// </summary>
    private static async Task<string?> ReachSqlServerAsync(AppConfig cfg, TextBlock status)
    {
        try
        {
            await WaitForSqlServerAsync(cfg, status);
            return null;
        }
        catch (SqlException ex)
        {
            AppLog.Error($"SQL Server \"{cfg.ServerName}\" not reachable", ex);
            return $"GenZ-POS can't reach SQL Server \"{cfg.ServerName}\" (database \"{cfg.DatabaseName}\"). Choose where this " +
                   "computer's data is and press Save. If that is already right, the main computer may be off or SQL Server " +
                   "still starting: wait a minute and press \"Try the saved connection again\".";
        }
    }

    /// <summary>The printers from appsettings.json (owner 2026-10-26); a saved one that isn't installed here is logged.</summary>
    private static void LogPrinterSettings(AppConfig cfg)
    {
        var installed = Services.ReceiptPrinter.InstalledPrinters();
        AppLog.Info($"Settings from {AppConfig.FilePath}: database {cfg.ServerName} / {cfg.DatabaseName}, receipt printer " +
                    $"\"{cfg.ReceiptPrinter ?? "(Windows default)"}\", label printer \"{cfg.LabelPrinter ?? "(not chosen)"}\"");
        foreach (var printer in new[] { cfg.ReceiptPrinter, cfg.LabelPrinter })
            if (!string.IsNullOrWhiteSpace(printer) && !installed.Contains(printer, StringComparer.OrdinalIgnoreCase))
                AppLog.Warn($"Printer \"{printer}\" from appsettings.json is not installed on this computer");
    }

    /// <summary>
    /// Restarts GenZ-POS (after the database connection changed): releases the single-instance lock first, so the new
    /// process may start, then closes this one.
    /// </summary>
    public static void Restart()
    {
        try
        {
            singleInstance?.ReleaseMutex();
            singleInstance?.Dispose();
            singleInstance = null;
        }
        catch
        {
            // Not held (any more): the new process can start anyway.
        }
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
        Current.Dispatcher.Invoke(() => Current.Shutdown());
    }

    /// <summary>Right after Windows starts, SQL Server may still be starting: try a few times before giving up.</summary>
    private static async Task WaitForSqlServerAsync(AppConfig cfg, TextBlock status)
    {
        // Probe "master": the shop database may not exist yet on a brand-new PC (Migrate creates it).
        var probe = new SqlConnectionStringBuilder(cfg.ConnectionString)
        {
            InitialCatalog = "master",
            ConnectTimeout = 10,
            Pooling = false,
        }.ConnectionString;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(probe);
                await conn.OpenAsync();
                return;
            }
            catch (SqlException ex) when (attempt < SqlConnectTries)
            {
                AppLog.Warn($"SQL Server not reachable (try {attempt} of {SqlConnectTries})", ex);
                status.Text = $"Waiting for the database (SQL Server) to start…\nTry {attempt + 1} of {SqlConnectTries}";
                await Task.Delay(SqlRetryDelay);
            }
        }
    }

    /// <summary>
    /// Never start a new, empty database by accident (owner 2026-10-26: after an update GenZ-POS made a new database
    /// instead of using the shop's — an appsettings.json from another PC had been copied over). When the configured
    /// database is missing or empty but the server has a GenZ-POS database with data, ask which one to use (the answer
    /// is saved in appsettings.json); on a server with no GenZ-POS database at all, ask before creating it.
    /// The DbContext factory reads cfg.ConnectionString lazily, so changing it here is enough. Returns false = close.
    /// </summary>
    private static async Task<bool> CheckDatabaseChoiceAsync(AppConfig cfg, TextBlock status)
    {
        status.Text = "Looking for the shop's data…";
        List<ShopDatabase> found;
        try
        {
            found = await DatabaseGuard.FindShopDatabasesAsync(cfg.ConnectionString);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not list the databases on the server; continuing with the configured one", ex);
            return true;
        }

        var target = cfg.DatabaseName;
        var configured = found.FirstOrDefault(d => string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase));
        if (configured is { HasData: true })
            return true; // the normal case: the shop's database, with its data

        var withData = found.Where(d => d.HasData && !string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.LastSale ?? DateTime.MinValue).ThenByDescending(d => d.Products + d.Sales).ToList();
        if (withData.Count > 0)
        {
            var best = withData[0];
            var others = withData.Count > 1
                ? "\n\nOther databases with data: " + string.Join("; ", withData.Skip(1).Select(d => d.Describe))
                : "";
            var answer = MessageBox.Show(
                $"GenZ-POS is set to use the database \"{target}\" on SQL Server \"{cfg.ServerName}\", but " +
                (configured is null ? "it does not exist." : "it is empty.") +
                $"\n\nThe shop's data was found in:\n   {best.Describe}{others}" +
                $"\n\nYes  = use \"{best.Name}\" (recommended; remembered from now on)" +
                $"\nNo  = use \"{target}\" anyway ({(configured is null ? "a NEW, EMPTY database is created" : "it has no data")})" +
                "\nCancel = close GenZ-POS",
                AppTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel)
                return false;
            if (answer == MessageBoxResult.Yes)
            {
                cfg.ConnectionString = DatabaseGuard.WithDatabase(cfg.ConnectionString, best.Name);
                AppLog.Info($"Database switched from \"{target}\" to \"{best.Name}\" (the configured one was {(configured is null ? "missing" : "empty")})");
                SaveConfig(cfg);
            }
            return true;
        }

        if (configured is null)
        {
            // A brand-new shop: no GenZ-POS database on this server yet.
            var create = MessageBox.Show(
                $"No GenZ-POS data was found on SQL Server \"{cfg.ServerName}\".\n\n" +
                $"Create a new, empty database \"{target}\" now?\n\n" +
                $"Choose No if this PC should use data that is somewhere else; then set the ConnectionString in\n{AppConfig.FilePath}",
                AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (create != MessageBoxResult.Yes)
                return false;
            AppLog.Info($"Creating a new database \"{target}\" on {cfg.ServerName} (none found)");
        }
        return true;
    }

    private static void SaveConfig(AppConfig cfg)
    {
        try { cfg.Save(); }
        catch (Exception ex)
        {
            AppLog.Error("Could not save appsettings.json", ex);
            MessageBox.Show($"GenZ-POS could not save its settings file ({AppConfig.FilePath}): {ex.Message}\n\n" +
                            "It works for now, but will ask again next time. Ask a technician to make that file writable.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Applies pending EF migrations. On an existing database (on this PC) a "_pre-upgrade" backup is taken first.
    /// Returns false if the user chose not to continue.
    /// </summary>
    private static async Task<bool> UpgradeDatabaseAsync(IServiceProvider provider, TextBlock status)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
            return true;

        var isExistingDatabase = (await db.Database.GetAppliedMigrationsAsync()).Any();
        var backup = provider.GetRequiredService<BackupService>();
        if (isExistingDatabase && backup.IsLocalServer)
        {
            status.Text = "Saving a safety backup before updating the database…";
            try
            {
                await backup.BackupAsync(BackupService.PreUpgradeSuffix);
            }
            catch (Exception ex)
            {
                var answer = MessageBox.Show(
                    "This version of GenZ-POS needs to update the database, but the safety backup could not be made:\n\n" +
                    PageBase.FriendlyMessage(ex) + "\n\nUpdate the database anyway?",
                    AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                    return false;
            }
        }

        status.Text = isExistingDatabase ? "Updating the database…" : "Creating the database…";
        AppLog.Info($"Applying migrations: {string.Join(", ", pending)}");
        db.Database.SetCommandTimeout(300);
        await db.Database.MigrateAsync();
        return true;
    }

    private static string StartupErrorMessage(Exception ex, AppConfig cfg)
    {
        var sql = ex as SqlException ?? ex.GetBaseException() as SqlException;
        if (sql is not null && sql.Number is -1 or 2 or 53 or 40 or -2)
            return $"GenZ-POS can't reach the database server (SQL Server \"{cfg.ServerName}\").\n\n" +
                   "• If the computer was just switched on, wait one minute and start GenZ-POS again.\n" +
                   "• Otherwise restart the computer that holds the data.\n\n" +
                   $"Details were saved in: {AppLog.Folder}";
        if (sql is not null && sql.Number is 18456 or 4060)
            return "SQL Server refused this Windows user.\n\n" +
                   "Ask whoever set up the PC to give this Windows user access to SQL Server (see README-INSTALL).\n\n" +
                   $"Details were saved in: {AppLog.Folder}";
        return $"Could not open or update the database:\n\n{ex.GetBaseException().Message}\n\n" +
               $"Details were saved in: {AppLog.Folder}";
    }

    private void StartBackgroundWork(IServiceProvider provider)
    {
        // Warm up EF query compilation + the SQL connection pool, so the first screen doesn't pay that cost.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
                await db.Products.AsNoTracking().Take(1).ToListAsync();
                await db.Sales.AsNoTracking().Take(1).ToListAsync();
                await db.Purchases.AsNoTracking().Take(1).ToListAsync();
            }
            catch (Exception ex)
            {
                // Warm-up only; real errors surface on the screen that needs the data.
                AppLog.Warn("Warm-up query failed", ex);
            }
        });

        // Daily automatic backup: now (if none today) and re-checked every hour for PCs left on overnight.
        var backup = provider.GetRequiredService<BackupService>();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20)); // let the first screen load first
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                await backup.RunDailyIfDueAsync();
                if (backup.LastError is { } error)
                    await Dispatcher.InvokeAsync(() => ShowBackupProblem(error));
            }
            while (await timer.WaitForNextTickAsync());
        });
    }

    /// <summary>A failing daily backup must not stay silent; tell the user once per day.</summary>
    private void ShowBackupProblem(string error)
    {
        if (backupErrorShownOn == DateTime.Today) return;
        backupErrorShownOn = DateTime.Today;
        var text = "Today's automatic backup did not work:\n\n" + error +
                   "\n\nOpen \"Settings & Backup\" and press \"Backup now\" to try again.";
        if (MainWindow is { IsLoaded: true } owner)
            MessageBox.Show(owner, text, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(text, AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static (Window Window, TextBlock Status) CreateSplash()
    {
        var status = new TextBlock
        {
            Text = "Starting GenZ-POS…",
            FontSize = 16,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
        };
        var window = new Window
        {
            Title = AppTitle,
            Width = 420,
            Height = 150,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = Brushes.SteelBlue,
                BorderThickness = new Thickness(2),
                Child = status,
            },
        };
        return (window, status);
    }

    // ---------- Win32 (bring the already-running window to the front) ----------

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
