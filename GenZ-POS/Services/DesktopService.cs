using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Web.WebView2.Core;

namespace GenZPOS.Services;

/// <summary>
/// Native desktop features (printing, file dialogs, Explorer) called straight from C#.
/// Blazor components run on the WPF UI thread, but calls are marshalled through the dispatcher anyway.
/// </summary>
public class DesktopService
{
    private Window? _window;
    private BlazorWebView? _webView;

    public void Attach(Window window, BlazorWebView webView)
    {
        _window = window;
        _webView = webView;
    }

    /// <summary>Opens the print dialog for the current page (print CSS hides the app chrome).</summary>
    public Task PrintAsync() =>
        Dispatch(() => _webView?.WebView.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser));

    /// <summary>Asks where to save, then writes the text. Returns the path, or null if cancelled.</summary>
    public async Task<string?> SaveTextFileAsync(string suggestedName, string filter, string content)
    {
        var path = await PickSaveFileAsync(suggestedName, filter);
        if (path is not null)
            await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);
        return path;
    }

    /// <summary>Shows a Save As dialog. Returns the chosen path, or null if cancelled.</summary>
    public async Task<string?> PickSaveFileAsync(string suggestedName, string filter)
    {
        string? path = null;
        await Dispatch(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = suggestedName, Filter = filter, OverwritePrompt = true };
            if (dialog.ShowDialog(_window) == true)
                path = dialog.FileName;
        });
        return path;
    }

    /// <summary>
    /// Asks where to save a copy of <paramref name="sourcePath"/> (e.g. a USB drive) and copies it there off the UI thread.
    /// Returns the destination path, or null if cancelled.
    /// </summary>
    public async Task<string?> CopyFileAsync(string sourcePath, string filter)
    {
        if (!File.Exists(sourcePath))
            throw new BusinessException("The file no longer exists: " + sourcePath);

        var dest = await PickSaveFileAsync(Path.GetFileName(sourcePath), filter);
        if (dest is null) return null;
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("Please choose a different place (e.g. your USB drive).");

        try
        {
            await Task.Run(() => File.Copy(sourcePath, dest, overwrite: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BusinessException($"Could not copy the file to {dest}: {ex.Message}");
        }
        return dest;
    }

    /// <summary>Shows an Open dialog. Returns the chosen path, or null if cancelled.</summary>
    public async Task<string?> PickOpenFileAsync(string filter)
    {
        string? path = null;
        await Dispatch(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
            if (dialog.ShowDialog(_window) == true)
                path = dialog.FileName;
        });
        return path;
    }

    /// <summary>Shows a folder picker (e.g. the USB / cloud folder for backup copies). Null if cancelled.</summary>
    public async Task<string?> PickFolderAsync(string title, string? initial = null)
    {
        string? path = null;
        await Dispatch(() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
            if (dialog.ShowDialog(_window) == true)
                path = dialog.FolderName;
        });
        return path;
    }

    /// <summary>Reads a whole file into memory; works while the file is open in Excel.</summary>
    public async Task<MemoryStream> ReadFileAsync(string path)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var copy = new MemoryStream();
            await file.CopyToAsync(copy);
            copy.Position = 0;
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BusinessException($"Could not read {path}: {ex.Message}");
        }
    }

    /// <summary>Writes bytes to a file the user picked (e.g. an Excel export).</summary>
    public async Task WriteFileAsync(string path, byte[] content)
    {
        try
        {
            await File.WriteAllBytesAsync(path, content);
        }
        catch (IOException ex)
        {
            throw new BusinessException($"Could not save {Path.GetFileName(path)}. If it is open in Excel, close it and try again. ({ex.Message})");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new BusinessException($"Windows doesn't allow saving to {path}. Choose another folder. ({ex.Message})");
        }
    }

    /// <summary>Opens a file with its Windows program (e.g. an .xlsx in Excel).</summary>
    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new BusinessException($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>Opens a folder in Windows Explorer (creates it first if missing).</summary>
    public void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new BusinessException($"Could not open {folder}: {ex.Message}");
        }
    }

    private Task Dispatch(Action action) =>
        _window is null ? Task.CompletedTask : _window.Dispatcher.InvokeAsync(action).Task;
}
