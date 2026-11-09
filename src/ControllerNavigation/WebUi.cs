using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ControllerNavigation;

/// <summary>
/// What the on-screen keyboard's WebView2 needs (the only web page left): one environment (the
/// user data folder under AppInfo.DataDir), the embedded ui/keyboard.* files served from a virtual
/// https origin (the exe sits in a read-only folder, so nothing is written to disk), a guard that
/// keeps the view on that origin, and the JSON message helpers. Every member is for the UI thread.
/// </summary>
static class WebUi
{
    /// <summary>The virtual origin the embedded ui/* files are served from (never resolved: WebResourceRequested answers first).</summary>
    public const string Origin = "https://ui.controllernavigation.invalid";

    const string PageCsp =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
        + "font-src 'self'; connect-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    static readonly Assembly Self = typeof(WebUi).Assembly;
    static Task<CoreWebView2Environment>? environment;

    /// <summary>The messages to a page as JSON: SettingsStore's naming and null rules, compact.</summary>
    public static readonly JsonSerializerOptions Json = new(SettingsStore.Json)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// The shared WebView2 environment. A failure (no runtime) is not remembered, so installing the
    /// runtime and opening the window again works without restarting the tool.
    /// </summary>
    public static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (environment is { IsFaulted: false, IsCanceled: false })
            return environment;
        return environment = CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(AppInfo.DataDir, "WebView2")
        );
    }

    /// <summary>True when the failure means the WebView2 runtime is not installed.</summary>
    public static bool IsRuntimeMissing(Exception e) =>
        e is WebView2RuntimeNotFoundException
        || e.InnerException is WebView2RuntimeNotFoundException
        || e is DllNotFoundException
        || e is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 126 };

    /// <summary>
    /// Locks a view down to the embedded pages and serves them: no dev tools, no menus, no new
    /// windows, no navigation away from Origin, every permission request denied. onBrowserGone runs
    /// (UI thread) when WebView2's browser process dies and the view cannot be used any more; a
    /// renderer that crashes or hangs is reloaded (three times a minute at most).
    /// </summary>
    public static void Attach(CoreWebView2 core, string who, Action<string> onBrowserGone)
    {
        var s = core.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;

        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => Serve(core, e, who);
        core.NavigationStarting += (_, e) =>
        {
            if (IsOurs(e.Uri))
                return;
            e.Cancel = true;
            Log.Warn($"{who}: navigation to {Describe(e.Uri)} refused");
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (IsOurs(e.Uri))
                return;
            e.Cancel = true;
            Log.Warn($"{who}: a frame's navigation to {Describe(e.Uri)} refused");
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;

        var failures = new Queue<long>();
        core.ProcessFailed += (_, e) =>
        {
            Log.Error(
                $"{who}: WebView2 process failed: {e.ProcessFailedKind} ({e.Reason}, exit code {e.ExitCode})"
            );
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                onBrowserGone(e.ProcessFailedKind.ToString());
                return;
            }
            if (
                e.ProcessFailedKind
                is not (
                    CoreWebView2ProcessFailedKind.RenderProcessExited
                    or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                )
            )
                return;
            var now = Environment.TickCount64;
            failures.Enqueue(now);
            while (failures.Count > 0 && now - failures.Peek() > 60_000)
                failures.Dequeue();
            if (failures.Count <= 3)
                core.Reload();
            else
                onBrowserGone("the page keeps crashing");
        };
    }

    static bool IsOurs(string? uri) =>
        uri is not null && uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase);

    // Never the full URL of anything else (it could hold a token): just where it points.
    static string Describe(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : "?";

    static void Serve(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e, string who)
    {
        if (!IsOurs(e.Request.Uri))
            return;
        var path = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath).TrimStart('/');
        using var resource =
            e.Request.Method == "GET" ? Self.GetManifestResourceStream("ui/" + path) : null;
        if (resource is null)
        {
            Log.Warn($"{who}: {e.Request.Method} /{path} not found");
            e.Response = core.Environment.CreateWebResourceResponse(
                null,
                404,
                "Not Found",
                "Content-Type: text/plain\r\n"
            );
            return;
        }
        var body = new MemoryStream();
        resource.CopyTo(body);
        body.Position = 0;
        var type = ContentType(path);
        var headers = $"Content-Type: {type}\r\nCache-Control: no-store\r\n";
        if (type.StartsWith("text/html", StringComparison.Ordinal))
            headers += $"Content-Security-Policy: {PageCsp}\r\n";
        e.Response = core.Environment.CreateWebResourceResponse(body, 200, "OK", headers);
    }

    static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };

    /// <summary>Sends a message to the page (it arrives as the "message" event's data).</summary>
    public static void Post(CoreWebView2? core, object message)
    {
        try
        {
            core?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
        }
        catch (Exception e)
        {
            Log.Warn("Web message not sent: " + e.Message);
        }
    }

    /// <summary>A message from our own page as JSON; null (logged) when it came from anywhere else or is not JSON.</summary>
    public static JsonElement? Read(CoreWebView2WebMessageReceivedEventArgs e, string who)
    {
        if (!IsOurs(e.Source))
        {
            Log.Warn($"{who}: a message from {Describe(e.Source)} dropped");
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A string property of a message, or null.</summary>
    public static string? Str(JsonElement message, string name) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
