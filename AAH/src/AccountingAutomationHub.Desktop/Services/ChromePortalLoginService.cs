using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AccountingAutomationHub.Desktop.Services;

public sealed record PortalLoginSessionResult(
    PortalLoginKind Portal,
    bool Prepared,
    bool? LoginSucceeded,
    string Message,
    DateTime TimestampUtc,
    string Url);

/// <summary>
/// Chrome-only assisted portal login. It verifies an official government host, fills the
/// credential fields, and deliberately stops before the final authentication action.
/// The user completes CAPTCHA/OTP and presses Login/Continue. A bounded background monitor
/// then records success or a visible portal error for status history.
/// </summary>
public sealed class ChromePortalLoginService
{
    private static readonly string[] ChromeCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
    ];

    private const string GstUrl = "https://services.gst.gov.in/services/login";
    private const string IncomeTaxUrl = "https://eportal.incometax.gov.in/iec/foservices/#/login";

    public event EventHandler<PortalLoginSessionResult>? SessionStatusChanged;

    public async Task<PortalLoginSessionResult> PrepareAsync(
        PortalLoginKind portal,
        string userId,
        string password,
        CancellationToken cancellationToken = default)
    {
        var chrome = FindChrome();
        if (chrome.Length == 0)
            return Result(portal, false, null, "Google Chrome was not found. Install Chrome or repair its installation first.", string.Empty);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrEmpty(password))
            return Result(portal, false, null, "Enter and save both User ID/PAN and Password first.", string.Empty);

        var url = portal == PortalLoginKind.Gst ? GstUrl : IncomeTaxUrl;
        var port = ReserveLoopbackPort();
        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AccountingAutomationHub", "PortalChrome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);

        var start = new ProcessStartInfo
        {
            FileName = chrome,
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"--remote-debugging-port={port} --remote-debugging-address=127.0.0.1 --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --new-window \"{url}\""
        };
        try { Process.Start(start); }
        catch (Exception exception)
        {
            return Result(portal, false, null, "Chrome could not be started: " + exception.Message, string.Empty);
        }

        try
        {
            var debuggerUrl = await WaitForPageDebuggerAsync(port, portal, cancellationToken).ConfigureAwait(false);
            if (debuggerUrl.Length == 0)
                return Result(portal, false, null, "Chrome opened, but AAH could not attach to the official portal tab.", url);

            var cdp = new CdpConnection(new Uri(debuggerUrl));
            await cdp.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await cdp.CommandAsync("Runtime.enable", new { }, cancellationToken).ConfigureAwait(false);

            var host = await cdp.EvaluateStringAsync("location.hostname", cancellationToken).ConfigureAwait(false);
            if (!IsAllowedHost(portal, host))
            {
                await cdp.DisposeAsync().ConfigureAwait(false);
                return Result(portal, false, null, $"Safety stop: unexpected portal host '{host}'.", url);
            }

            bool filled;
            if (portal == PortalLoginKind.Gst)
            {
                filled = await FillGstAsync(cdp, userId, password, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                filled = await FillIncomeTaxAsync(cdp, userId, password, cancellationToken).ConfigureAwait(false);
            }

            if (!filled)
            {
                await cdp.DisposeAsync().ConfigureAwait(false);
                return Result(portal, false, null, "Official portal opened, but the current login fields could not be filled. Portal markup may have changed.", url);
            }

            var prepared = Result(portal, true, null,
                portal == PortalLoginKind.Gst
                    ? "Chrome is ready. Complete CAPTCHA/2-step authentication if shown and press Login yourself."
                    : "Chrome is ready. Verify the Secure Access Message and press the final Continue/Login yourself.",
                await cdp.EvaluateStringAsync("location.href", cancellationToken).ConfigureAwait(false));
            SessionStatusChanged?.Invoke(this, prepared);

            _ = MonitorAsync(portal, cdp, CancellationToken.None);
            return prepared;
        }
        catch (Exception exception)
        {
            return Result(portal, false, null, "Portal preparation failed: " + exception.Message, url);
        }
    }

    private async Task MonitorAsync(PortalLoginKind portal, CdpConnection cdp, CancellationToken ignored)
    {
        var deadline = DateTime.UtcNow.AddMinutes(10);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                string url;
                string body;
                string host;
                try
                {
                    url = await cdp.EvaluateStringAsync("location.href", CancellationToken.None).ConfigureAwait(false);
                    host = await cdp.EvaluateStringAsync("location.hostname", CancellationToken.None).ConfigureAwait(false);
                    body = await cdp.EvaluateStringAsync("(document.body && document.body.innerText || '').slice(0,120000)", CancellationToken.None).ConfigureAwait(false);
                }
                catch { break; }

                if (!IsAllowedHost(portal, host))
                {
                    SessionStatusChanged?.Invoke(this, Result(portal, true, false, "Login session left the expected government portal domain.", url));
                    break;
                }

                var normalized = body.ToLowerInvariant();
                if (ContainsAny(normalized,
                    "invalid username", "invalid user id", "invalid password", "incorrect password",
                    "username or password is incorrect", "user id or password is incorrect",
                    "login failed", "authentication failed", "invalid credentials",
                    "invalid captcha", "incorrect captcha", "captcha entered is incorrect",
                    "invalid otp", "otp is invalid", "incorrect otp"))
                {
                    SessionStatusChanged?.Invoke(this, Result(portal, true, false, "Portal reported a login/credential error.", url));
                    break;
                }

                var successText = ContainsAny(normalized,
                    "logout", "log out", "dashboard", "welcome", "my profile", "view profile");
                var passwordStillVisible = await cdp.EvaluateBoolAsync("!!document.querySelector('input[type=password]:not([disabled])')", CancellationToken.None).ConfigureAwait(false);
                var loginLikeUrl = url.Contains("login", StringComparison.OrdinalIgnoreCase);
                if (successText && (!loginLikeUrl || !passwordStillVisible))
                {
                    SessionStatusChanged?.Invoke(this, Result(portal, true, true, "Portal login detected successfully.", url));
                    break;
                }
            }
        }
        finally
        {
            await cdp.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> FillGstAsync(CdpConnection cdp, string userId, string password, CancellationToken token)
    {
        var script = FillScript(
            ["#username", "input[name=username]", "input[autocomplete=username]", "input[placeholder*=Username i]"],
            userId,
            ["#user_pass", "#password", "input[name=password]", "input[autocomplete=current-password]", "input[type=password]"],
            password);
        return await cdp.EvaluateBoolAsync(script, token).ConfigureAwait(false);
    }

    private static async Task<bool> FillIncomeTaxAsync(CdpConnection cdp, string userId, string password, CancellationToken token)
    {
        var userScript = FillOnlyScript(
            ["#panAdhaarUserId", "#userId", "input[name=userId]", "input[autocomplete=username]", "input[placeholder*=User\\ ID i]"], userId);
        if (!await cdp.EvaluateBoolAsync(userScript, token).ConfigureAwait(false)) return false;

        // This is the non-authenticating User-ID step required to expose the password field.
        var continueScript = "(()=>{const bs=[...document.querySelectorAll('button,input[type=submit]')];const b=bs.find(x=>((x.innerText||x.value||'').trim().toLowerCase()==='continue'));if(!b)return false;b.click();return true;})()";
        await cdp.EvaluateBoolAsync(continueScript, token).ConfigureAwait(false);

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500, token).ConfigureAwait(false);
            var passScript = FillOnlyScript(
                ["#loginPasswordField", "#password", "input[name=password]", "input[autocomplete=current-password]", "input[type=password]"], password);
            if (await cdp.EvaluateBoolAsync(passScript, token).ConfigureAwait(false)) return true;
        }
        return false;
    }

    private static string FillScript(string[] userSelectors, string userId, string[] passwordSelectors, string password) =>
        "(()=>{" + SelectorHelper() +
        $"const u=pick({JsonSerializer.Serialize(userSelectors)});const p=pick({JsonSerializer.Serialize(passwordSelectors)});" +
        "if(!u||!p)return false;" + SetValue("u", userId) + SetValue("p", password) + "return true;})()";

    private static string FillOnlyScript(string[] selectors, string value) =>
        "(()=>{" + SelectorHelper() + $"const e=pick({JsonSerializer.Serialize(selectors)});if(!e)return false;" + SetValue("e", value) + "return true;})()";

    private static string SelectorHelper() =>
        "const pick=s=>{for(const q of s){try{const e=document.querySelector(q);if(e&&e.offsetParent!==null)return e;}catch{}}return null;};";

    private static string SetValue(string variable, string value)
    {
        var json = JsonSerializer.Serialize(value ?? string.Empty);
        return $"{{const d=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value');d&&d.set?d.set.call({variable},{json}):{variable}.value={json};{variable}.dispatchEvent(new Event('input',{{bubbles:true}}));{variable}.dispatchEvent(new Event('change',{{bubbles:true}}));}}";
    }

    private static bool IsAllowedHost(PortalLoginKind portal, string host)
    {
        host = (host ?? string.Empty).Trim().ToLowerInvariant();
        return portal == PortalLoginKind.Gst
            ? host is "services.gst.gov.in" or "www.gst.gov.in" or "gst.gov.in"
            : host is "eportal.incometax.gov.in" or "www.incometax.gov.in" or "incometax.gov.in";
    }

    private static PortalLoginSessionResult Result(PortalLoginKind portal, bool prepared, bool? success, string message, string url) =>
        new(portal, prepared, success, message, DateTime.UtcNow, url ?? string.Empty);

    private static string FindChrome()
    {
        foreach (var candidate in ChromeCandidates)
            if (File.Exists(candidate)) return candidate;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(part.Trim(), "chrome.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return string.Empty;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> WaitForPageDebuggerAsync(int port, PortalLoginKind portal, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 50; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json", token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (!element.TryGetProperty("type", out var type) || type.GetString() != "page") continue;
                    var pageUrl = element.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty;
                    var host = Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
                    if (!IsAllowedHost(portal, host)) continue;
                    if (element.TryGetProperty("webSocketDebuggerUrl", out var ws)) return ws.GetString() ?? string.Empty;
                }
            }
            catch { }
            await Task.Delay(200, token).ConfigureAwait(false);
        }
        return string.Empty;
    }

    private sealed class CdpConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly Uri _uri;
        private int _nextId;

        public CdpConnection(Uri uri) => _uri = uri;

        public Task ConnectAsync(CancellationToken token) => _socket.ConnectAsync(_uri, token);

        public async Task<JsonElement> CommandAsync(string method, object parameters, CancellationToken token)
        {
            var id = Interlocked.Increment(ref _nextId);
            var payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            while (true)
            {
                var text = await ReceiveTextAsync(token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
                return root.TryGetProperty("result", out var result) ? result.Clone() : default;
            }
        }

        public async Task<string> EvaluateStringAsync(string expression, CancellationToken token)
        {
            var result = await CommandAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, token).ConfigureAwait(false);
            if (!result.TryGetProperty("result", out var value)) return string.Empty;
            if (value.TryGetProperty("value", out var actual)) return actual.ValueKind == JsonValueKind.String ? actual.GetString() ?? string.Empty : actual.ToString();
            return string.Empty;
        }

        public async Task<bool> EvaluateBoolAsync(string expression, CancellationToken token)
        {
            var result = await CommandAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, token).ConfigureAwait(false);
            return result.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var actual) && actual.ValueKind == JsonValueKind.True;
        }

        private async Task<string> ReceiveTextAsync(CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            using var stream = new MemoryStream();
            while (true)
            {
                var received = await _socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close) throw new IOException("Chrome DevTools session closed.");
                stream.Write(buffer, 0, received.Count);
                if (received.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "AAH monitor complete", CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            _socket.Dispose();
        }
    }
}
