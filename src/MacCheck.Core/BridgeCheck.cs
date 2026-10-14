using System.Text.Json;

namespace MacCheck.Core;

/// <summary>
/// M1 correction for the first macOS runner trial (run 38477574883): on macOS, Avalonia.Controls.WebView 12.1.0 defines
/// <c>invokeCSharpAction</c> only in WKWebView's <c>didFinishNavigation</c> (it evaluates the bridge script after the page
/// has loaded, then raises NavigationCompleted). WebView2 and WebKitGTK/WPE install it before the page's first script.
/// TableForge 1.0.0's page posts <c>pageLoaded</c> from an inline script while loading, inside a try/catch, so on macOS
/// that one message is lost and the roller never calls <c>tf.init</c>.
///
/// The correction keeps the 1.0.0 page and protocol: when NavigationCompleted reports the dice page, the app asks the page
/// (through InvokeScript) what it has, and — only if the bridge and <c>window.tf</c> exist and <c>pageLoaded</c> has not
/// arrived — has the page send <c>pageLoaded</c> through the real bridge. The app still waits for that message to arrive,
/// so page → app delivery is proven, not assumed.
/// </summary>
public static class BridgeCheck
{
    /// <summary>
    /// Bounded, non-sensitive facts about the page (no token, no room, no URLs beyond scheme and host). The diagnostic
    /// prelude (<see cref="PageDiagnostics"/>) supplies the bridge type at document start, early script errors and blocked
    /// resources (host names only).
    /// </summary>
    public const string ProbeScript = """
        JSON.stringify((() => { const d = window.__tfDiag || {};
          return { bridge: typeof window.invokeCSharpAction, bridgeAtStart: d.bridgeAtStart || 'unknown', tf: typeof window.tf,
            sdk: typeof ThreeDDice, sdkLoadFailed: !!window.__sdkLoadFailed, origin: location.protocol + '//' + location.hostname,
            readyState: document.readyState, errors: (d.errors || []).slice(0, 5), blocked: (d.blocked || []).slice(0, 5) }; })())
        """;

    /// <summary>Has the page announce itself through the bridge (the message 1.0.0's page sends while loading).</summary>
    public const string ReplayScript = "window.invokeCSharpAction({ kind: 'pageLoaded' }); 'sent'";

    public sealed record Facts(string Bridge, string BridgeAtStart, string Tf, string Sdk, bool SdkLoadFailed, string Origin,
        string ReadyState, IReadOnlyList<string> Errors, IReadOnlyList<string> Blocked)
    {
        public bool BridgeReady => Bridge == "function" && Tf == "object";

        public override string ToString() =>
            $"invokeCSharpAction={Bridge} (at document start: {BridgeAtStart}), tf={Tf}, ThreeDDice={Sdk}, sdkLoadFailed={SdkLoadFailed}, " +
            $"origin={Origin}, readyState={ReadyState}, script errors={Errors.Count}{(Errors.Count > 0 ? " [" + string.Join(" | ", Errors) + "]" : "")}, " +
            $"blocked resources={Blocked.Count}{(Blocked.Count > 0 ? " [" + string.Join(" | ", Blocked) + "]" : "")}";
    }

    /// <summary>Parses the probe's answer (InvokeScript returns the JSON string, possibly itself JSON-quoted).</summary>
    public static Facts? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var text = raw.TrimStart()[0] == '"' ? JsonSerializer.Deserialize<string>(raw) : raw;
            if (text is null) return null;
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString()!, 40) : "?";
            List<string> L(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Take(5).Select(x => Clip(x.ToString(), 160)).ToList() : [];
            return new Facts(S("bridge"), S("bridgeAtStart"), S("tf"), S("sdk"),
                r.TryGetProperty("sdkLoadFailed", out var f) && f.ValueKind == JsonValueKind.True, S("origin"), S("readyState"), L("errors"), L("blocked"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Whether the app should have the page send <c>pageLoaded</c> again: only when the page is ours, loaded, the bridge
    /// and <c>window.tf</c> exist, and the page's own <c>pageLoaded</c> has not arrived (so WebView2/WebKitGTK, which
    /// deliver it themselves, are unaffected).
    /// </summary>
    public static bool ShouldReplay(Facts? facts, bool pageLoadedReceived) => !pageLoadedReceived && facts is { BridgeReady: true };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Diagnostic prelude added at the top of the served dice page (M1 only; bounded and non-sensitive): records the type of
/// <c>invokeCSharpAction</c> when the document starts, the first 5 script errors (160 characters each) and the first 5
/// resources that failed to load (tag and host name only). It never posts anything and never touches the 1.0.0 code.
/// </summary>
public static class PageDiagnostics
{
    public const string Prelude =
        "<script>window.__tfDiag={bridgeAtStart:typeof window.invokeCSharpAction,errors:[],blocked:[]};" +
        "addEventListener('error',function(e){var d=window.__tfDiag,t=e.target;" +
        "if(t&&t!==window&&(t.src||t.href)){if(d.blocked.length<5){var h='?';try{h=new URL(t.src||t.href).hostname}catch(x){}d.blocked.push(String(t.tagName).toLowerCase()+' '+h)}}" +
        "else if(d.errors.length<5)d.errors.push(String(e.message).slice(0,160))},true);</script>";

    /// <summary>
    /// DEVELOPMENT ONLY (Windows/Linux): hides the bridge until the page's load event, reproducing the order WKWebView uses
    /// on macOS, so the correction can be verified without a Mac. Never used on macOS or in the tester's app.
    /// </summary>
    public const string SimulateLateBridge =
        "<script>(function(){var b=window.invokeCSharpAction;window.invokeCSharpAction=undefined;" +
        "addEventListener('load',function(){setTimeout(function(){window.invokeCSharpAction=b},0)})})();</script>";
}
