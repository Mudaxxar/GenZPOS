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

        var provider = BuildServices(cfg);
        var (splash, status) = CreateSplash();
        splash.Show();

        try
        {
            await WaitForSqlServerAsync(cfg, status);
            if (!await UpgradeDatabaseAsync(provider, status))
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
        catch (Exception ex)
        {
            AppLog.Error("Startup failed", ex);
            splash.Close();
            MessageBox.Show(StartupErrorMessage(ex, cfg), AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var window = new MainWindow(provider);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        splash.Close();

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
        services.AddSingleton<BackupService>();

        return services.BuildServiceProvider();
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
