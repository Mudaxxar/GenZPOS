using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Data.Entities;

namespace GenZPOS.Services;

/// <summary>One .bak file in the backup folder.</summary>
public record BackupFile(string Name, string FullPath, DateTime CreatedAt, long SizeBytes)
{
    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024d / 1024d:N1} MB"
        : $"{Math.Max(1, SizeBytes / 1024)} KB";
}

/// <summary>
/// Full database backups with SQL Server's BACKUP DATABASE (works on Express; no compression there).
/// The .bak file is written by the SQL Server service (account NT Service\MSSQLSERVER), so that account needs
/// write access to the backup folder — deploy\setup.cmd grants it.
/// Backups only make sense on the PC that runs SQL Server (<see cref="IsLocalServer"/>): the path is on the server's disk.
/// Singleton.
/// </summary>
public class BackupService(IDbContextFactory<AppDbContext> dbFactory, AppConfig cfg)
{
    public const string PreUpgradeSuffix = "_pre-upgrade";
    private const int BackupTimeoutSeconds = 600;

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>When the last successful backup finished (from the Settings table / this session).</summary>
    public DateTime? LastBackupAt { get; private set; }

    /// <summary>Message of the last failed backup (null after a success). Pages can show it as a warning.</summary>
    public string? LastError { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>Raised (on any thread) when a backup starts/finishes or fails.</summary>
    public event Action? Changed;

    public string Folder => cfg.BackupFolder;

    public bool IsLocalServer => IsLocal(cfg.ConnectionString);

    private string DbName => cfg.DatabaseName;

    /// <summary>Reads Backup.LastAt from the Settings table (call after migrations).</summary>
    public async Task LoadAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var value = await db.Settings.AsNoTracking()
            .Where(s => s.Key == AppSetting.LastBackupAt).Select(s => s.Value).FirstOrDefaultAsync();
        if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var at))
            LastBackupAt = at;
    }

    /// <summary>Makes a full backup now. Returns the new file. Throws <see cref="BusinessException"/> with a clear message on failure.</summary>
    public async Task<BackupFile> BackupAsync(string suffix = "")
    {
        if (!IsLocalServer)
            throw new BusinessException(
                $"Backups are made on the computer that runs SQL Server ({cfg.ServerName}). Please take the backup there.");

        await gate.WaitAsync();
        IsRunning = true;
        LastError = null;
        Changed?.Invoke();
        try
        {
            var folder = cfg.BackupFolder;
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                throw new BusinessException($"Could not create the backup folder {folder}: {ex.Message}");
            }

            var path = Path.Combine(folder, $"{DbName}_{DateTime.Now:yyyyMMdd_HHmmss}{suffix}.bak");
            var quotedDb = "[" + DbName.Replace("]", "]]") + "]";

            try
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                db.Database.SetCommandTimeout(BackupTimeoutSeconds);
                // BACKUP can't run inside a transaction; ExecuteSqlRaw doesn't start one.
                // No COMPRESSION: not supported on Express. The db name comes from our own config (quoted above).
                var sql = "BACKUP DATABASE " + quotedDb + " TO DISK = @path WITH INIT, CHECKSUM, NAME = N'GenZ-POS backup'";
                await db.Database.ExecuteSqlRawAsync(sql, new SqlParameter("@path", path));
            }
            catch (SqlException ex)
            {
                throw new BusinessException(ExplainSqlError(ex, folder));
            }

            var now = DateTime.Now;
            LastBackupAt = now;
            await SaveLastBackupAtAsync(now);
            ApplyRetention();

            var info = new FileInfo(path);
            AppLog.Info($"Backup created: {path}");
            return new BackupFile(info.Name, info.FullName, info.Exists ? info.LastWriteTime : now, info.Exists ? info.Length : 0);
        }
        catch (Exception ex)
        {
            LastError = ex is BusinessException ? ex.Message : "Backup failed: " + ex.GetBaseException().Message;
            AppLog.Error("Backup failed: " + LastError, ex);
            throw ex is BusinessException ? ex : new BusinessException(LastError);
        }
        finally
        {
            IsRunning = false;
            gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Daily automatic backup (called in the background after startup): only when enabled, on the SQL Server PC,
    /// and when there's no backup from today yet. Never throws; failures go to <see cref="LastError"/> and the log.
    /// </summary>
    public async Task RunDailyIfDueAsync()
    {
        try
        {
            if (!cfg.AutoBackupDaily || !IsLocalServer) return;
            var newest = ListBackups(1).FirstOrDefault();
            if (newest is not null && newest.CreatedAt.Date == DateTime.Today) return;
            if (LastBackupAt?.Date == DateTime.Today && newest is not null) return;

            await BackupAsync();
        }
        catch (Exception ex)
        {
            // BackupAsync already logged it and set LastError.
            LastError ??= "Automatic backup failed: " + ex.Message;
            Changed?.Invoke();
        }
    }

    /// <summary>Backups in the folder, newest first.</summary>
    public IReadOnlyList<BackupFile> ListBackups(int max = 50)
    {
        try
        {
            if (!Directory.Exists(cfg.BackupFolder)) return [];
            return new DirectoryInfo(cfg.BackupFolder).GetFiles($"{DbName}_*.bak")
                .OrderByDescending(f => f.LastWriteTime)
                .Take(max)
                .Select(f => new BackupFile(f.Name, f.FullName, f.LastWriteTime, f.Length))
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not list backups in {cfg.BackupFolder}", ex);
            return [];
        }
    }

    /// <summary>Keeps the newest <c>KeepBackups</c> files, deletes older ones.</summary>
    private void ApplyRetention()
    {
        try
        {
            var old = new DirectoryInfo(cfg.BackupFolder).GetFiles($"{DbName}_*.bak")
                .OrderByDescending(f => f.LastWriteTime)
                .Skip(Math.Max(1, cfg.KeepBackups));
            foreach (var file in old)
            {
                try { file.Delete(); }
                catch (Exception ex) { AppLog.Warn($"Could not delete old backup {file.FullName}", ex); }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Backup clean-up failed", ex);
        }
    }

    private async Task SaveLastBackupAtAsync(DateTime at)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var value = at.ToString("o");
            var row = await db.Settings.FindAsync(AppSetting.LastBackupAt);
            if (row is null) db.Settings.Add(new AppSetting { Key = AppSetting.LastBackupAt, Value = value });
            else row.Value = value;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Before the first migration the Settings table may not exist (pre-upgrade backup). Not important.
            AppLog.Warn("Could not record the backup time", ex);
        }
    }

    private string ExplainSqlError(SqlException ex, string folder)
    {
        var numbers = ex.Errors.Cast<SqlError>().Select(e => e.Number).ToHashSet();
        var text = string.Join(" ", ex.Errors.Cast<SqlError>().Select(e => e.Message));

        if (numbers.Contains(3201) || text.Contains("Operating system error 5", StringComparison.OrdinalIgnoreCase))
            return $"SQL Server is not allowed to write into the backup folder {folder}. " +
                   "Fix (once): right-click deploy\\setup.cmd → Run as administrator, " +
                   $"or in an administrator Command Prompt run:  icacls \"{folder}\" /grant \"NT Service\\MSSQLSERVER\":(OI)(CI)M";
        if (numbers.Contains(3202) || text.Contains("Operating system error 112", StringComparison.OrdinalIgnoreCase))
            return $"The disk holding {folder} is full. Delete old files or choose another backup folder.";
        if (numbers.Contains(-2))
            return "The backup took too long and was stopped. Please try again when the shop is quiet.";
        if (numbers.Overlaps([-1, 2, 53, 4060, 18456]))
            return "Can't connect to SQL Server to make the backup. Make sure it is running.";
        return "Backup failed: " + ex.Message;
    }

    /// <summary>
    /// True when the connection string points at SQL Server on this PC:
    /// ".", "(local)", "localhost", "127.0.0.1", this machine's name (optionally with \INSTANCE or ,port),
    /// "(localdb)\...", or a local named pipe "np:\\.\pipe\...".
    /// </summary>
    public static bool IsLocal(string connectionString)
    {
        string dataSource;
        try { dataSource = new SqlConnectionStringBuilder(connectionString).DataSource ?? ""; }
        catch { return false; }

        var s = dataSource.Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..]; break; }

        string host;
        if (s.StartsWith(@"\\"))
        {
            // Named pipe: \\HOST\pipe\...
            var rest = s[2..];
            var slash = rest.IndexOf('\\');
            host = slash >= 0 ? rest[..slash] : rest;
        }
        else
        {
            host = s.Split('\\', ',')[0];
        }
        host = host.Trim();

        if (host.Length == 0) return false;
        if (host.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)) return true;
        return host is "." or "127.0.0.1" or "::1"
            || host.Equals("(local)", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}
