using System.Reflection;

namespace E4.Dddice;

/// <summary>
/// The dice page E4 serves: TableForge 1.0.0's own <c>Assets/dddice-host.html</c> (embedded byte-identical, see tests) with
/// one change to its code — its bridge. 1.0.0 posts through WebView2's <c>window.chrome.webview.postMessage</c>; Avalonia's
/// NativeWebView injects <c>invokeCSharpAction</c> on every platform (WebView2, WKWebView, WebKitGTK/WPE) instead. The
/// message objects, their kinds and the <c>window.tf</c> API are untouched, so the dice protocol is 1.0.0's.
/// </summary>
public static class HostPage
{
    public const string OriginalBridge = "window.chrome.webview.postMessage(m)";
    public const string AvaloniaBridge = "window.invokeCSharpAction(m)";

    public static string Original()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("dddice-host.v1.0.0.html")
            ?? throw new InvalidOperationException("The 1.0.0 dice page is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public const string Head = "<head>";

    /// <summary>
    /// The page with its one bridge call swapped. Throws unless the original call occurs exactly once.
    /// M1 adds one more thing, outside the 1.0.0 code: <see cref="MacCheck.Core.PageDiagnostics.Prelude"/> right after
    /// <c>&lt;head&gt;</c> (bounded diagnostics; it never posts). <paramref name="simulateLateBridge"/> is development-only
    /// (see <see cref="MacCheck.Core.PageDiagnostics.SimulateLateBridge"/>).
    /// </summary>
    public static string ForAvalonia(bool simulateLateBridge = false)
    {
        var page = Original();
        var first = page.IndexOf(OriginalBridge, StringComparison.Ordinal);
        if (first < 0 || page.IndexOf(OriginalBridge, first + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The 1.0.0 page does not contain its bridge call exactly once.");
        var head = page.IndexOf(Head, StringComparison.Ordinal);
        if (head < 0 || page.IndexOf(Head, head + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The 1.0.0 page does not contain <head> exactly once.");
        var added = (simulateLateBridge ? MacCheck.Core.PageDiagnostics.SimulateLateBridge : "") + MacCheck.Core.PageDiagnostics.Prelude;
        return page.Replace(OriginalBridge, AvaloniaBridge, StringComparison.Ordinal).Insert(head + Head.Length, added);
    }
}
