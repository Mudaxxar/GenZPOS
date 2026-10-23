using System.ComponentModel;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using GenZPOS.Components;
using GenZPOS.Services;

namespace GenZPOS;

public partial class MainWindow : Window
{
    private bool webViewDisposed;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        Title = $"GenZ-POS v{AppConfig.Version}";
        blazorWebView.Services = services;
        blazorWebView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Routes) });
        services.GetRequiredService<DesktopService>().Attach(this, blazorWebView);

#if !DEBUG
        // Shop build: F5 / Ctrl+R would reload the page and wipe a half-entered invoice; Ctrl+P, F3, zoom keys etc.
        // are browser features too. Page keydown handlers (F2/F4/F8/F9 hotkeys, Enter navigation) still get their keys.
        blazorWebView.BlazorWebViewInitialized += (_, e) =>
        {
            var settings = e.WebView.CoreWebView2.Settings;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
        };
#endif
    }

    // Shut the WebView (and its composition surface) down while the window still exists.
    // Letting WPF tear it down during process exit can fail-fast in CoreMessaging.dll (0xc0000602).
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || webViewDisposed) return;

        e.Cancel = true;
        webViewDisposed = true;
        try
        {
            await blazorWebView.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Closing anyway.
            AppLog.Warn("WebView dispose failed on close", ex);
        }
        Close();
    }
}
