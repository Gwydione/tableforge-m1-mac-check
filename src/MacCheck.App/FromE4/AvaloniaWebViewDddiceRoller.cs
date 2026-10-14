using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Platform;
using E4.Dddice;
using TableForge.Dice;

namespace E4.App;

/// <summary>
/// Port of TableForge 1.0.0's <c>WebViewDddiceRoller</c> (WPF + WebView2) to Avalonia's <see cref="NativeWebView"/>. Same
/// contract (<see cref="IDddiceRoomRoller"/>), same session source, same <see cref="DddiceRollTracker"/>, same page and
/// protocol, same timeouts and the same "any failure means the next attempt starts from a fresh page" rule. Differences,
/// each forced by the platform-neutral control:
///  - the page comes from a loopback server instead of a WebView2 virtual host (<see cref="LoopbackPageServer"/>);
///  - messages arrive through <c>invokeCSharpAction</c> and are validated before use (<see cref="PageMessages"/>);
///  - the browser surface can be closed and reopened (<see cref="CloseBrowser"/>), and losing it is noticed.
/// The token is handed to the page only as an argument of <c>window.tf.init</c>, exactly as in 1.0.0.
/// </summary>
public sealed class AvaloniaWebViewDddiceRoller : IDddiceRoomRoller, IDisposable
{
    private readonly Func<NativeWebView, Task> _attach;
    private readonly Action<NativeWebView> _detach;
    private readonly Action<NativeWebView> _configure;
    private readonly DddiceSessionSource _sessions;
    private readonly DddiceRollTracker _tracker = new();
    private readonly LoopbackPageServer _server;
    private readonly DiagnosticLog _log;
    private NativeWebView? _view;
    private DddiceSession? _session;
    private bool _isReady;
    private bool _adapterReady;
    private TaskCompletionSource? _adapter;
    private TaskCompletionSource? _pageReady;
    private TaskCompletionSource? _pageLoaded;

    public TimeSpan PrepareTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RollTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised for every validated page message (kind, raw JSON) — for the status display and the test runner.</summary>
    public event Action<string, string>? PageMessage;
    public event Action<string>? StateChanged;

    public string BrowserState { get; private set; } = "not created";
    public string ConnectionState { get; private set; } = "—";
    public WebViewAdapterInfo? AdapterInfo => _view?.AdapterInfo;
    public NativeWebView? View => _view;
    public bool IsReady => _isReady;
    public int RejectedMessages { get; private set; }
    public int IgnoredMessages { get; private set; }
    public string? LastFinishedJson { get; private set; }
    public DddiceSession? Session => _session;

    public AvaloniaWebViewDddiceRoller(Func<NativeWebView, Task> attach, Action<NativeWebView> detach, Action<NativeWebView> configure,
        DddiceRest rest, DiagnosticLog log)
    {
        _attach = attach;
        _detach = detach;
        _configure = configure;
        _sessions = new DddiceSessionSource(rest); // guest only: E4 never reads or stores an account
        _log = log;
        _server = new LoopbackPageServer(HostPage.ForAvalonia(DevSimulateLateBridge && !OperatingSystem.IsMacOS()), log.Write);
    }

    /// <summary>
    /// DEVELOPMENT ONLY (Windows/Linux self-test): serve the page with the bridge hidden until its load event, which is the
    /// order WKWebView uses on macOS (see <see cref="MacCheck.Core.BridgeCheck"/>). Ignored on macOS.
    /// </summary>
    public static bool DevSimulateLateBridge { get; set; }

    /// <summary>The last answer of the bridge check (bounded page facts), for the results.</summary>
    public MacCheck.Core.BridgeCheck.Facts? LastBridgeFacts { get; private set; }

    public Uri PageUri => _server.PageUri;

    public void Reset()
    {
        _isReady = false;
        _session = null;
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_isReady) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PrepareTimeout);
        try
        {
            SetState("preparing");
            _session = null;
            _session = await _sessions.GetAsync(timeout.Token);
            _log.RegisterSecret(_session.Token);
            _log.Write($"session: {_session}"); // DddiceSession.ToString never includes the token
            await EnsureWebViewAsync(timeout.Token);
            await LoadPageAsync(timeout.Token);
            _isReady = true;
            SetState("ready");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetState("failed");
            throw new DddiceException("dddice did not become ready in time. Check the internet connection.");
        }
        catch (DddiceException ex) when (ex.IsAuthProblem)
        {
            _sessions.AuthFailed(_session);
            _session = null;
            SetState("failed");
            throw;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DddiceException))
        {
            SetState("failed");
            throw new DddiceException("The dddice dice could not be started: " + ex.Message, inner: ex);
        }
        catch (Exception) { SetState("failed"); throw; }
    }

    private async Task EnsureWebViewAsync(CancellationToken ct)
    {
        if (_view is not null && _adapterReady) return;
        if (_view is null)
        {
            var view = new NativeWebView();
            _view = view;
            _adapter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _adapterReady = false;
            _configure(view); // per-platform environment (profile folder, dev tools off, test-only switches)
            view.AdapterCreated += (_, _) => { _adapterReady = true; _adapter?.TrySetResult(); _log.Write($"browser: adapter created ({Describe(view.AdapterInfo)})"); };
            view.AdapterDestroyed += (_, _) => OnAdapterDestroyed(view);
            view.NewWindowRequested += (_, e) => { e.Handled = true; _log.Write("browser: new window refused"); };
            view.NavigationStarted += (_, e) =>
            {
                var allowed = e.Request is { } uri && uri.GetLeftPart(UriPartial.Path) == _server.PageUri.GetLeftPart(UriPartial.Path);
                e.Cancel = !allowed; // the page never leaves its own page (1.0.0: same rule for its virtual host)
                _log.Write($"browser: navigation {(allowed ? "allowed" : "BLOCKED")} {(allowed ? "<dice page>" : Safe(e.Request))}");
            };
            view.NavigationCompleted += (_, e) =>
            {
                var ours = e.Request is { } uri && uri.GetLeftPart(UriPartial.Path) == _server.PageUri.GetLeftPart(UriPartial.Path);
                _log.Write($"browser: navigation completed success={e.IsSuccess} {(ours ? "<dice page>" : Safe(e.Request))}");
                if (!e.IsSuccess) _pageLoaded?.TrySetException(new DddiceException("The dddice dice page could not be loaded."));
                else if (ours && _pageLoaded is { } loading) _ = CheckBridgeAsync(view, loading);
                // Defence in depth: if the engine let a cancelled navigation through anyway (seen with the WebKitGTK
                // adapter), the dice page is gone and a foreign page now has the bridge. Stop trusting the page at once.
                if (e.Request is not null && !ours && e.Request.Scheme != "about") OnEscapedNavigation(e.Request);
            };
            view.WebMessageReceived += (_, e) => OnPageMessage(e.Body);
            SetState("creating");
            await _attach(view);
        }
        await _adapter!.Task.WaitAsync(ct);
    }

    private async Task LoadPageAsync(CancellationToken ct)
    {
        var view = _view!;
        _pageLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SetState("loading page");
        view.Navigate(_server.PageUri); // a fresh page (and so a fresh dddice-js) for every preparation, as in 1.0.0
        await _pageLoaded.Task.WaitAsync(ct);
        var session = _session!;
        SetState("connecting");
        await view.InvokeScript(
            $"window.tf.init({JsonSerializer.Serialize(session.Token)}, {JsonSerializer.Serialize(session.Room)}, {JsonSerializer.Serialize(session.Theme)})");
        await _pageReady.Task.WaitAsync(ct);
    }

    /// <summary>
    /// M1 correction (macOS): WKWebView gets <c>invokeCSharpAction</c> only after the page has loaded, so the page's own
    /// <c>pageLoaded</c> (sent while loading) is lost there. After the dice page's NavigationCompleted, ask the page what it
    /// has; if the bridge and <c>window.tf</c> exist and <c>pageLoaded</c> has not arrived, have the page send it through
    /// the bridge. <see cref="LoadPageAsync"/> still waits for the message itself, so delivery is proven. Bounded: 10 probes
    /// 200 ms apart. On WebView2/WebKitGTK the page's own message normally arrives first and nothing is replayed.
    /// </summary>
    private async Task CheckBridgeAsync(NativeWebView view, TaskCompletionSource loading)
    {
        try
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (!ReferenceEquals(view, _view) || !ReferenceEquals(loading, _pageLoaded)) return;
                var facts = MacCheck.Core.BridgeCheck.Parse(await view.InvokeScript(MacCheck.Core.BridgeCheck.ProbeScript));
                LastBridgeFacts = facts;
                if (attempt == 0 || facts?.BridgeReady == true) _log.Write($"bridge check: {facts?.ToString() ?? "no answer from the page"}");
                if (loading.Task.IsCompleted) return;
                if (MacCheck.Core.BridgeCheck.ShouldReplay(facts, pageLoadedReceived: false))
                {
                    _log.Write("bridge check: pageLoaded was not received; asking the page to send it through the bridge");
                    await view.InvokeScript(MacCheck.Core.BridgeCheck.ReplayScript);
                    return;
                }
                await Task.Delay(200);
            }
            _log.Write("bridge check: the bridge did not become available after the page loaded");
        }
        catch (Exception ex) { _log.Write($"bridge check failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
    }

    public async Task<IReadOnlyList<DddiceFace>> RollAsync(IReadOnlyList<string> diceTypes, CancellationToken cancellationToken)
    {
        if (!_isReady || _view is null || _session is not { } session) throw new DddiceException("dddice is not ready.");

        var externalId = Guid.NewGuid().ToString("N");
        var settled = _tracker.Begin(externalId);
        CurrentExternalId = externalId;
        try
        {
            var dice = new JsonArray(diceTypes.Select(d => (JsonNode)new JsonObject { ["type"] = d, ["theme"] = session.Theme }).ToArray());
            try
            {
                await _view.InvokeScript("window.tf.clear()"); // sweep the previous roll's dice off the tray
                await _view.InvokeScript($"window.tf.roll({dice.ToJsonString()}, '{externalId}')");
            }
            catch (Exception ex) when (ex is not (DddiceException or OperationCanceledException))
            {
                // WebView2's ExecuteScriptAsync (1.0.0) does not throw for a script error; NativeWebView's InvokeScript
                // does on WebKit. Either way the page cannot roll: same rule as any dddice failure.
                throw new DddiceException("The dice page did not accept the roll: " + ex.Message.Split('\n')[0]);
            }
            return await settled.WaitAsync(RollTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _isReady = false;
            SetState("failed (roll timed out)");
            throw new DddiceException($"The dddice roll did not finish within {RollTimeout.TotalSeconds:0} seconds.");
        }
        catch (DddiceException)
        {
            _isReady = false; // whatever went wrong, the next attempt starts from a fresh page
            SetState("failed (roll)");
            throw;
        }
        finally
        {
            _tracker.Abandon();
            CurrentExternalId = null;
        }
    }

    public string? CurrentExternalId { get; private set; }

    private void OnPageMessage(string? body)
    {
        if (!PageMessages.TryValidate(body, out var kind, out var reason))
        {
            RejectedMessages++;
            _log.Write($"bridge: REJECTED message ({reason})");
            return;
        }
        var json = body!;
        var root = JsonDocument.Parse(json).RootElement;
        var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        _log.Write($"bridge: {kind}{Detail(kind, root)}");

        switch (kind)
        {
            case "pageLoaded": _pageLoaded?.TrySetResult(); break;
            case "ready": _pageReady?.TrySetResult(); break;
            case "sdkLoadFailed":
                _pageReady?.TrySetException(new DddiceException("The dddice dice could not be downloaded from dddice.com. Check the internet connection."));
                break;
            case "initFailed":
                var auth = message.Contains("401") || message.Contains("403");
                _pageReady?.TrySetException(new DddiceException(auth
                    ? "dddice refused the guest login for its room."
                    : "TableForge could not connect to dddice's room. Check the internet connection.", auth));
                break;
            case "connectionState" when root.TryGetProperty("state", out var s):
                ConnectionState = s.GetString() ?? "?";
                StateChanged?.Invoke(BrowserState);
                if (ConnectionState is "unavailable" or "failed")
                {
                    if (_tracker.IsRolling) _tracker.Fail("The connection to dddice was lost during the roll.");
                    _isReady = false;
                    SetState($"not ready (connection {ConnectionState})");
                }
                break;
            default:
                if (kind == "roll:finished") LastFinishedJson = json;
                var wasRolling = _tracker.IsRolling;
                _tracker.OnPageMessage(json);
                if (kind.StartsWith("roll:") && (!wasRolling || IsForeign(root))) { IgnoredMessages++; _log.Write($"bridge: {kind} ignored (no matching roll in progress)"); }
                break;
        }
        PageMessage?.Invoke(kind, json);
    }

    private bool IsForeign(JsonElement root) =>
        root.TryGetProperty("roll", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("externalId", out var id)
        && id.ValueKind == JsonValueKind.String && id.GetString() != CurrentExternalId;

    private static string Detail(string kind, JsonElement root) => kind switch
    {
        "connectionState" => $" {root.GetProperty("state")}",
        "roll:started" or "roll:finished" when root.GetProperty("roll") is { ValueKind: JsonValueKind.Object } r =>
            $" ext={(r.TryGetProperty("externalId", out var e) ? e.ToString()[..Math.Min(8, e.ToString().Length)] : "-")} values=[{string.Join(",", r.GetProperty("values").EnumerateArray().Select(v => $"{v.GetProperty("type")}:{v.GetProperty("value")}"))}] total={(r.TryGetProperty("total", out var t) ? t.ToString() : "?")}",
        "rollError" or "initFailed" or "warmSkipped" => $" {(root.TryGetProperty("message", out var m) ? m.ToString() : "")}",
        "warmed" => $" {root.GetProperty("ms")} ms",
        _ => "",
    };

    public int EscapedNavigations { get; private set; }

    private void OnEscapedNavigation(Uri where)
    {
        EscapedNavigations++;
        _log.Write($"browser: NAVIGATION ESCAPED to {Safe(where)} — page no longer trusted; leaving it for a fresh prepare");
        if (_tracker.IsRolling) _tracker.Fail("The dice page navigated away during the roll.");
        _isReady = false;
        _pageReady?.TrySetException(new DddiceException("The dice page navigated away."));
        SetState("not ready (page navigated away)");
        _view?.Navigate(new Uri("about:blank"));
    }

    private void OnAdapterDestroyed(NativeWebView view)
    {
        _log.Write("browser: adapter destroyed");
        if (!ReferenceEquals(view, _view)) return;
        _adapterReady = false;
        _adapter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_tracker.IsRolling) _tracker.Fail("The dice browser closed during the roll.");
        _isReady = false;
        SetState("closed");
    }

    /// <summary>Removes the browser surface entirely (its adapter and native browser go). The next prepare creates a new one.</summary>
    public void CloseBrowser()
    {
        if (_view is not { } view) return;
        _view = null;
        _adapterReady = false;
        if (_tracker.IsRolling) _tracker.Fail("The dice browser closed during the roll.");
        _isReady = false;
        _detach(view);
        SetState("closed");
    }

    /// <summary>Reloads the page in place (as a renderer crash, a stray F5 or a page-initiated reload would).</summary>
    public void ReloadPage()
    {
        _log.Write("browser: reload requested");
        _view?.Refresh();
    }

    private void SetState(string state)
    {
        BrowserState = state;
        StateChanged?.Invoke(state);
    }

    public static string Describe(WebViewAdapterInfo? info) => info is null ? "no adapter" : $"{info.Type}/{info.Engine} {info.Version}";

    private static string Safe(Uri? uri) => uri is null ? "(null)" : uri.GetLeftPart(UriPartial.Path);

    public void Dispose()
    {
        CloseBrowser();
        _server.Dispose();
    }
}
