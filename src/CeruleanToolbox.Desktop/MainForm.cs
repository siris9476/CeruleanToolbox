using System.Text.Json;
using CeruleanToolbox.Components;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Web.WebView2.Core;

namespace CeruleanToolbox.Desktop;

public sealed class MainForm : Form
{
    public MainForm(IServiceProvider services)
    {
        Text = "CeruleanToolbox";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "app.ico"));

        var blazorWebView = new BlazorWebView
        {
            Dock = DockStyle.Fill,
            HostPage = "wwwroot\\index.html",
            Services = services,
        };
        blazorWebView.RootComponents.Add<Routes>("#app");
        blazorWebView.BlazorWebViewInitialized += async (_, e) =>
        {
            // The console pastes with a right click, which reads the clipboard; the window only shows this app's own pages.
            e.WebView.CoreWebView2.PermissionRequested += (sender, args) =>
            {
                if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                {
                    args.State = CoreWebView2PermissionState.Allow;
                }
            };
            await ForwardWebViewErrorsAsync(e.WebView.CoreWebView2);
        };

        Controls.Add(blazorWebView);
    }

    // A Blazor error ("An unhandled error has occurred") is reported by the page's JavaScript on its console,
    // not through .NET logging. Subscribing to the DevTools protocol events copies those messages into the log file.
    private static async Task ForwardWebViewErrorsAsync(CoreWebView2 webView)
    {
        try
        {
            await webView.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await webView.CallDevToolsProtocolMethodAsync("Log.enable", "{}");

            webView.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived +=
                (_, args) => Forward("JS exception", args.ParameterObjectAsJson, "exceptionDetails");
            webView.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived +=
                (_, args) => ForwardConsole(args.ParameterObjectAsJson);
            webView.GetDevToolsProtocolEventReceiver("Log.entryAdded").DevToolsProtocolEventReceived +=
                (_, args) => ForwardLogEntry(args.ParameterObjectAsJson);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("[WebView] cannot attach the JS error log: " + ex.Message);
        }
    }

    private static void Forward(string kind, string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var node = doc.RootElement.TryGetProperty(property, out var p) ? p : doc.RootElement;
            var text = node.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d)
                ? d.GetString()
                : node.TryGetProperty("text", out var t) ? t.GetString() : node.ToString();
            ErrorLog.Write($"[{kind}] {text}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            ErrorLog.Write($"[{kind}] {json}");
        }
    }

    private static void ForwardConsole(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type is not ("error" or "warning" or "assert")) return;

            var parts = doc.RootElement.GetProperty("args").EnumerateArray().Select(a =>
                a.TryGetProperty("value", out var v) ? v.ToString()
                : a.TryGetProperty("description", out var d) ? d.GetString() : a.ToString());
            ErrorLog.Write($"[JS console.{type}] {string.Join(' ', parts)}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // an unexpected message shape must not break logging
        }
    }

    private static void ForwardLogEntry(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var entry = doc.RootElement.GetProperty("entry");
            var level = entry.GetProperty("level").GetString();
            if (level is not ("error" or "warning")) return;

            var url = entry.TryGetProperty("url", out var u) ? u.GetString() : "";
            ErrorLog.Write($"[WebView log.{level}] {entry.GetProperty("text").GetString()} {url}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
        }
    }
}
