using Microsoft.AspNetCore.Components;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using GenZPOS.Data;
using GenZPOS.Services;
using Radzen;

namespace GenZPOS.Components;

/// <summary>Common services and error handling for pages and dialogs.</summary>
public abstract class PageBase : ComponentBase
{
    [Inject] protected IDbContextFactory<AppDbContext> DbFactory { get; set; } = default!;
    [Inject] protected NotificationService Notifications { get; set; } = default!;
    [Inject] protected DialogService Dialogs { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected CurrentUser CurrentUser { get; set; } = default!;

    protected bool Busy { get; private set; }

    /// <summary>Whether the logged-in user's role has this right (use to hide buttons; services enforce it too).</summary>
    protected bool Can(string permission) => CurrentUser.Has(permission);

    /// <summary>Runs an action, shows a friendly error if it fails. Returns true on success.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, string? successMessage = null)
    {
        Busy = true;
        try
        {
            await action();
            if (successMessage is not null)
                Toast(NotificationSeverity.Success, successMessage);
            return true;
        }
        catch (Exception ex)
        {
            if (ex is not BusinessException)
                AppLog.Error($"Error on {GetType().Name}", ex);
            Toast(NotificationSeverity.Error, FriendlyMessage(ex), 6000);
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    protected void Toast(NotificationSeverity severity, string message, double durationMs = 3000) =>
        Notifications.Notify(new NotificationMessage { Severity = severity, Summary = message, Duration = durationMs });

    protected async Task<bool> ConfirmAsync(string message) =>
        await Dialogs.Confirm(message, "Please confirm", new ConfirmOptions { OkButtonText = "Yes", CancelButtonText = "No" }) == true;

    private const string SeeLog = " (Details were saved in the log: Settings & Backup → Open logs folder.)";

    /// <summary>
    /// Turns an exception into a message a shop user can act on. Anything that isn't a
    /// <see cref="BusinessException"/> is also written to the log file (once per exception).
    /// </summary>
    public static string FriendlyMessage(Exception ex)
    {
        if (ex is not BusinessException)
            AppLog.Error("Error shown to user", ex);

        return ex switch
        {
            BusinessException => ex.Message,
            _ when (ex as SqlException ?? ex.InnerException as SqlException) is { } sql => sql.Number switch
            {
                547 => "This record is used elsewhere (products, invoices, stock…) and can't be deleted. Mark it inactive instead.",
                2601 or 2627 => "A record with the same name already exists.",
                2628 or 8152 => "Some text is too long. Please shorten it and try again.",
                1205 => "Another computer was saving at the same moment. Please press Save again.",
                -2 => "The database took too long to answer. Please try again.",
                -1 or 2 or 53 or 4060 or 18456 => "Can't connect to the database (SQL Server). Make sure the computer that holds the data is on and SQL Server is running.",
                _ => "Database error: " + sql.Message + SeeLog,
            },
            DbUpdateConcurrencyException => "This record was changed or deleted meanwhile. Please reopen it and try again.",
            _ => "Something went wrong: " + ex.GetBaseException().Message + SeeLog,
        };
    }
}
