using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using GenZPOS.Data;

namespace GenZPOS;

/// <summary>
/// Per-installation settings from <c>appsettings.json</c> next to GenZ-POS.exe.
/// Missing file → defaults. Broken file → defaults + <see cref="LoadError"/> (logged and shown once at startup).
/// Shop details (name, phone…) are NOT here; they live in the database (Settings table) so they're in backups.
/// </summary>
public sealed class AppConfig
{
    public const string FileName = "appsettings.json";
    public const string DefaultBackupFolder = @"C:\GenZ-POS-Backups";
    public const int DefaultKeepBackups = 30;

    public string ConnectionString { get; set; } = AppDbContext.DefaultConnectionString;
    public string BackupFolder { get; set; } = DefaultBackupFolder;
    public int KeepBackups { get; set; } = DefaultKeepBackups;
    public bool AutoBackupDaily { get; set; } = true;

    /// <summary>
    /// This PC's receipt (thermal) printer, e.g. "POS-80". Receipts print straight to it (no dialog); null = not chosen
    /// yet (the receipt screen asks). Per PC, so it lives here, not in the shared database.
    /// </summary>
    public string? ReceiptPrinter { get; set; }

    /// <summary>This PC's barcode label printer, e.g. "4BARCODE 4B-2054F" (chosen once on the labels screen).</summary>
    public string? LabelPrinter { get; set; }

    /// <summary>Writes the settings back to <c>appsettings.json</c> (e.g. after choosing the receipt printer).</summary>
    public void Save() =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Why the file couldn't be used (null when fine or simply missing).</summary>
    [JsonIgnore] public string? LoadError { get; private set; }

    [JsonIgnore] public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>App version, e.g. "1.0.0" (from &lt;Version&gt; in the csproj).</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(AppConfig).Assembly).GetName().Version is { } v
            ? v.ToString(3) : "1.0.0";

    /// <summary>SQL Server name from the connection string (no password / user shown).</summary>
    [JsonIgnore] public string ServerName => TryBuilder()?.DataSource is { Length: > 0 } s ? s : "?";

    /// <summary>Database name from the connection string (falls back to "Blazor").</summary>
    [JsonIgnore] public string DatabaseName => TryBuilder()?.InitialCatalog is { Length: > 0 } d ? d : "Blazor";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
            return new AppConfig();

        AppConfig cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not read {path}; using default settings.", ex);
            return new AppConfig
            {
                LoadError = $"The settings file ({path}) is damaged, so default settings are being used.\n\n{ex.Message}",
            };
        }

        cfg.Normalize();
        return cfg;
    }

    private void Normalize()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(ConnectionString))
            ConnectionString = AppDbContext.DefaultConnectionString;
        else if (TryBuilder() is null)
        {
            problems.Add("ConnectionString is not valid");
            ConnectionString = AppDbContext.DefaultConnectionString;
        }

        if (string.IsNullOrWhiteSpace(BackupFolder))
            BackupFolder = DefaultBackupFolder;
        else
        {
            BackupFolder = BackupFolder.Trim();
            if (!Path.IsPathFullyQualified(BackupFolder))
            {
                problems.Add($"BackupFolder \"{BackupFolder}\" must be a full path like C:\\GenZ-POS-Backups");
                BackupFolder = DefaultBackupFolder;
            }
        }

        if (KeepBackups < 1)
        {
            problems.Add("KeepBackups must be 1 or more");
            KeepBackups = DefaultKeepBackups;
        }

        if (problems.Count > 0)
        {
            LoadError = $"Some settings in {FilePath} were wrong, defaults are used for them:\n- " + string.Join("\n- ", problems);
            AppLog.Error(LoadError);
        }
    }

    private SqlConnectionStringBuilder? TryBuilder()
    {
        try { return new SqlConnectionStringBuilder(ConnectionString); }
        catch { return null; }
    }
}
