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
            readyState: document.readyState, errors: (d.errors || []).slice(0, 5), blocked: (d.blocked || []).slice(0, 5),
            gl: window.__tfGl || (window.__tfGl = (() => { const r = { webgl2: 'none', webgl: 'none', renderer: '', hardware: 'unknown' };
              const release = c => { try { const l = c.getExtension('WEBGL_lose_context'); if (l) l.loseContext(); } catch (e) {} };
              for (const k of ['webgl2', 'webgl']) { let c = null; try { c = document.createElement('canvas').getContext(k); } catch (e) {}
                if (!c) continue; let p = null; try { p = c.getShaderPrecisionFormat(c.VERTEX_SHADER, c.HIGH_FLOAT); } catch (e) {}
                r[k] = c.isContextLost() ? 'lost' : p ? 'ok' : 'unusable';
                if (!r.renderer) { try { const i = c.getExtension('WEBGL_debug_renderer_info');
                  r.renderer = String(c.getParameter(i ? i.UNMASKED_RENDERER_WEBGL : c.RENDERER)).slice(0, 80); } catch (e) {} }
                release(c); }
              try { const c = document.createElement('canvas').getContext('webgl', { failIfMajorPerformanceCaveat: true });
                r.hardware = c ? 'yes' : 'no'; if (c) release(c); } catch (e) {}
              return r; })()) }; })())
        """;

    /// <summary>Has the page announce itself through the bridge (the message 1.0.0's page sends while loading).</summary>
    public const string ReplayScript = "window.invokeCSharpAction({ kind: 'pageLoaded' }); 'sent'";

    public sealed record Facts(string Bridge, string BridgeAtStart, string Tf, string Sdk, bool SdkLoadFailed, string Origin,
        string ReadyState, IReadOnlyList<string> Errors, IReadOnlyList<string> Blocked, Graphics? Gl = null)
    {
        public bool BridgeReady => Bridge == "function" && Tf == "object";

        public override string ToString() =>
            $"invokeCSharpAction={Bridge} (at document start: {BridgeAtStart}), tf={Tf}, ThreeDDice={Sdk}, sdkLoadFailed={SdkLoadFailed}, " +
            $"origin={Origin}, readyState={ReadyState}, script errors={Errors.Count}{(Errors.Count > 0 ? " [" + string.Join(" | ", Errors) + "]" : "")}, " +
            $"blocked resources={Blocked.Count}{(Blocked.Count > 0 ? " [" + string.Join(" | ", Blocked) + "]" : "")}, " +
            (Gl?.ToString() ?? "WebGL not probed");
    }

    /// <summary>
    /// What the page's own engine offers for 3D (probed once per page, on throwaway canvases released at once). Each context
    /// is "ok", "none" (no context at all), "lost", or "unusable": a context whose <c>getShaderPrecisionFormat</c> answers
    /// null, which is the call dddice's 3D engine failed on, on the trial-2 Intel runner. <c>Hardware</c> is "no" when the
    /// engine gives WebGL only with a major performance caveat (software rendering) or not at all. <c>Renderer</c> is the
    /// engine's renderer name (a GPU model or a software renderer), at most 80 characters.
    /// </summary>
    public sealed record Graphics(string WebGl2, string WebGl, string Renderer, string Hardware)
    {
        /// <summary>dddice's 3D engine needs a working WebGL2 context.</summary>
        public bool Usable => WebGl2 == "ok";

        public override string ToString() =>
            $"WebGL2={WebGl2}, WebGL={WebGl}, hardware-accelerated={Hardware}, renderer={(Renderer.Length > 0 ? Renderer : "(none)")}";
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
            Graphics? gl = null;
            if (r.TryGetProperty("gl", out var g) && g.ValueKind == JsonValueKind.Object)
            {
                string G(string name, int max) => g.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString()!, max) : "?";
                gl = new Graphics(G("webgl2", 12), G("webgl", 12), G("renderer", 80), G("hardware", 12));
            }
            return new Facts(S("bridge"), S("bridgeAtStart"), S("tf"), S("sdk"),
                r.TryGetProperty("sdkLoadFailed", out var f) && f.ValueKind == JsonValueKind.True, S("origin"), S("readyState"), L("errors"), L("blocked"), gl);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The message for a dice start that failed on the page (<c>initFailed</c>, not a refused login). TableForge 1.0.0 calls
    /// every such failure a connection problem; when dddice's 3D engine could not get working graphics (WebGL) that is
    /// misleading, so a graphics failure — recognised from the engine's error or from the page's WebGL facts — gets its own.
    /// </summary>
    public static string StartFailureMessage(string? pageError, Facts? facts) =>
        IsGraphicsFailure(pageError, facts) ? GraphicsUnavailable : "TableForge could not connect to dddice's room. Check the internet connection.";

    public const string GraphicsUnavailable =
        "The 3D dice could not start: 3D graphics (WebGL) were not available in the dice view. This is not an internet connection problem.";

    /// <summary>
    /// The renderer name is usually the GPU model: system information, recorded only with the tester's consent to share it
    /// (results format). Without that consent it is replaced; whether graphics are hardware-accelerated stays.
    /// </summary>
    public static string WithholdRenderer(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"renderer=[^;\r\n]*", "renderer=<not recorded: no consent to share system information>");

    public static bool IsGraphicsFailure(string? pageError, Facts? facts) =>
        facts?.Gl is { Usable: false, WebGl2: not "?" }
        || pageError is not null && System.Text.RegularExpressions.Regex.IsMatch(pageError, @"(?i)webgl|getShaderPrecisionFormat|getContext|shader|creating .*context");

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
