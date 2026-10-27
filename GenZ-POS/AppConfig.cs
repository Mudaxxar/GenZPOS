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
    /// <summary>The developer's appsettings.json as shipped in a release; copied to <see cref="FileName"/> on a PC's first start.</summary>
    public const string DefaultFileName = "appsettings.default.json";
    public const string DefaultBackupFolder = @"C:\GenZ-POS-Backups";
    public const int DefaultKeepBackups = 30;

    public string ConnectionString { get; set; } = AppDbContext.DefaultConnectionString;
    public string BackupFolder { get; set; } = DefaultBackupFolder;
    public int KeepBackups { get; set; } = DefaultKeepBackups;
    public bool AutoBackupDaily { get; set; } = true;

    /// <summary>
    /// Second place every backup is copied to (owner 2026-10-24): a USB drive or a cloud-synced folder (Google Drive /
    /// OneDrive), so a dead disk doesn't take the backups with it. Null = no copy. Per PC (drive letters differ).
    /// </summary>
    public string? BackupCopyFolder { get; set; }

    /// <summary>
    /// This PC's receipt (thermal) printer, e.g. "POS-80". Receipts print straight to it (no dialog); null = not chosen
    /// yet (the receipt screen asks). Per PC, so it lives here, not in the shared database.
    /// </summary>
    public string? ReceiptPrinter { get; set; }

    /// <summary>This PC's barcode label printer, e.g. "4BARCODE 4B-2054F" (chosen once on the labels screen).</summary>
    public string? LabelPrinter { get; set; }

    /// <summary>Writes the settings back to <c>appsettings.json</c> (e.g. after choosing the receipt printer).</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Why the file couldn't be used (null when fine or simply missing).</summary>
    [JsonIgnore] public string? LoadError { get; private set; }

    /// <summary>
    /// Where this PC's settings live: <c>appsettings.json</c> next to GenZ-POS.exe — or, when that folder can't be written
    /// (owner 2026-10-26: installed under C:\Program Files, the file was never created), in
    /// <c>C:\ProgramData\GenZ-POS\appsettings.json</c>. A file that already exists in either place wins (next to the exe first).
    /// </summary>
    [JsonIgnore] public static string FilePath { get; } = ResolveFilePath();

    private static string ResolveFilePath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, FileName);
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GenZ-POS", FileName);
        if (File.Exists(local)) return local;
        if (File.Exists(shared)) return shared;
        return CanWrite(AppContext.BaseDirectory) ? local : shared;
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

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
        {
            // First start on this PC (releases don't ship appsettings.json, so an update can never overwrite it): start
            // from the developer's appsettings.json shipped as appsettings.default.json (owner 2026-10-26: the client
            // gets the connection string set before publishing), else from the built-in defaults.
            var template = Path.Combine(AppContext.BaseDirectory, DefaultFileName);
            if (File.Exists(template))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Copy(template, path);
                    AppLog.Info($"Created {path} from {DefaultFileName}");
                    return Load();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Could not copy {template} to {path}", ex);
                    if (TryRead(template) is { } fromTemplate) return fromTemplate; // works for now, from the template
                }
            }
            var fresh = new AppConfig();
            try
            {
                fresh.Save();
                AppLog.Info($"Created {path} with default settings");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Could not create {path}", ex);
            }
            return fresh;
        }

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

    /// <summary>Settings from a JSON file, or null when it can't be read.</summary>
    private static AppConfig? TryRead(string file)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), JsonOptions);
            cfg?.Normalize();
            return cfg;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not read {file}", ex);
            return null;
        }
    }

    /// <summary>
    /// The connection string for tools that run outside the app (dotnet ef, Package Manager Console): the
    /// <c>appsettings.json</c> of the GenZ-POS project (searched from <paramref name="startFolder"/> and its parents, also
    /// in a GenZ-POS sub-folder), else the built-in default.
    /// </summary>
    public static string ConnectionStringForTools(string startFolder)
    {
        for (var dir = new DirectoryInfo(startFolder); dir is not null; dir = dir.Parent)
            foreach (var file in new[] { Path.Combine(dir.FullName, FileName), Path.Combine(dir.FullName, "GenZ-POS", FileName) })
                if (File.Exists(file) && TryRead(file) is { } cfg)
                    return cfg.ConnectionString;
        return AppDbContext.DefaultConnectionString;
    }

    private SqlConnectionStringBuilder? TryBuilder()
    {
        try { return new SqlConnectionStringBuilder(ConnectionString); }
        catch { return null; }
    }
}
